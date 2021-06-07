using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Resources;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirDropAnywhere.Core.Compression
{
    /// <summary>
    /// Implements the code necessary to extract a CPIO archive in OIDC format.
    /// </summary>
    /// <remarks>
    /// See https://manpages.ubuntu.com/manpages/bionic/man5/cpio.5.html for further details
    /// on the structure of an OIDC formatted archive.
    /// </remarks>
    internal class CpioArchiveReader : IAsyncDisposable
    {
        private readonly PipeReader _pipeReader;
        
        private CpioArchiveReader(PipeReader pipeReader)
        {
            _pipeReader = pipeReader ?? throw new ArgumentNullException(nameof(pipeReader));
        }

        private byte[]? _workingBytes;
        private byte[] GetWorkingBytes() => _workingBytes ??= ArrayPool<byte>.Shared.Rent(4096);

        public static CpioArchiveReader Create(Stream stream) => new(
            PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true))
        );

        /// <summary>
        /// "TRAILER" filename used to indicate we're on the last record.
        /// </summary>
        private static readonly ReadOnlyMemory<byte> _trailerValue = new[]
        {
            (byte) 'T', (byte) 'R', (byte) 'A', (byte) 'I', (byte) 'L', 
            (byte) 'E', (byte) 'R', (byte) '!', (byte) '!', (byte) '!'
        };

        /// <summary>
        /// Prefix used for files in the current directory.
        /// </summary>
        private static readonly ReadOnlyMemory<byte> _currentDirectoryPrefix = new[]
        {
            (byte) '.', (byte) '/'
        };
        
        /// <summary>
        /// "Filename" used to indicate the current directory.
        /// </summary>
        private static readonly ReadOnlyMemory<byte> _currentDirectoryValue = new[]
        {
            (byte) '.'
        };

        /// <summary>
        /// "Filename" used to indicate the parent directory.
        /// </summary>
        private static readonly ReadOnlyMemory<byte> _parentDirectoryValue = new[]
        {
            (byte) '.', (byte) '.'
        };
        
        /// <summary>
        /// "magic" value at the start of each archive entry.
        /// </summary>
        private static readonly ReadOnlyMemory<byte> _magicValue = new[]
        {
            (byte)'0', (byte)'7', (byte)'0', (byte)'7', (byte)'0', (byte)'7'
        };

        /// <summary>
        /// Extracts a CPIO archive to the specified output path.
        /// </summary>
        /// <param name="outputPath">
        /// Path to extract files to.
        /// </param>
        /// <param name="cancellationToken">
        /// <see cref="CancellationToken"/> used to cancel the operation.
        /// </param>
        public async ValueTask<IEnumerable<string>> ExtractAsync(string outputPath, CancellationToken cancellationToken = default)
        {
            if (outputPath == null)
            {
                throw new ArgumentNullException(nameof(outputPath));
            }

            if (!Path.IsPathFullyQualified(outputPath))
            {
                throw new ArgumentException("Output path must be fully qualified.", nameof(outputPath));
            }

            var extractedFiles = ImmutableList.CreateBuilder<string>();
            var state = new CpioReaderState();
            while (true)
            {
                var readResult = await _pipeReader.ReadAsync(cancellationToken);
                var sequence = readResult.Buffer;
                var consumed = default(SequencePosition);
                if (!sequence.IsEmpty)
                {
                    ExtractArchiveEntries(
                        ref sequence,
                        ref state,
                        outputPath,
                        extractedFiles,
                        out consumed
                    );
                }
                
                if (state.Operation == CpioReadOperation.End)
                {
                    // consume the rest of the file, we're done here
                    await _pipeReader.CompleteAsync();
                    break;
                }

                if (readResult.IsCompleted)
                {
                    // we reached the end of the file prior to reaching the trailer
                    // this is an error
                    await _pipeReader.CompleteAsync(
                        new InvalidOperationException("Did not find trailer before end of file")
                    );
                    break;
                }
                    
                _pipeReader.AdvanceTo(consumed, sequence.End);
            }

            return extractedFiles.ToImmutable();
        }

        private void ExtractArchiveEntries(
            ref ReadOnlySequence<byte> sequence,
            ref CpioReaderState state,
            string outputPath,
            ImmutableList<string>.Builder extractedFiles,
            out SequencePosition consumed
        )
        {
            // attempt to read the next entry's metadata
            var sequenceReader = new SequenceReader<byte>(sequence);
            while (!sequenceReader.End)
            {
                if (state.Operation == CpioReadOperation.Metadata)
                {
                    if (!sequenceReader.IsNext(_magicValue.Span))
                    {
                        throw new InvalidOperationException("Could not find an archive entry.");
                    }

                    if (sequenceReader.UnreadSequence.Length < CpioEntryMetadata.Length)
                    {
                        // we can't yet process the header, return until we have enough data
                        break;
                    }

                    ReadOnlySpan<byte> headerSpan;
                    var headerSequence = sequenceReader.UnreadSequence.Slice(0, CpioEntryMetadata.Length);
                    if (headerSequence.IsSingleSegment)
                    {
                        headerSpan = headerSequence.FirstSpan;
                    }
                    else
                    {
                        var workingSpan = GetWorkingBytes().AsSpan();
                        headerSequence.CopyTo(workingSpan);
                        headerSpan = workingSpan[..CpioEntryMetadata.Length];
                    }

                    // parse the metadata
                    if (!CpioEntryMetadata.TryCreate(headerSpan, out var error, out var metadata))
                    {
                        throw new InvalidOperationException($"Unable to extract metadata from header. {error}");
                    }

                    // consume the header
                    sequenceReader.Advance(CpioEntryMetadata.Length);
                    state = state.OnMetadataRead(metadata);
                }
                else if (state.Operation == CpioReadOperation.FileName)
                {
                    // filename includes the NUL (\0) terminator
                    // so exclude it when creating our string
                    var fileNameSize = state.Metadata.FileNameSize - 1;
                    ReadOnlySpan<byte> fileNameSpan;
                    var fileNameSequence = sequenceReader.UnreadSequence.Slice(0, fileNameSize);
                    if (fileNameSequence.IsSingleSegment)
                    {
                        fileNameSpan = fileNameSequence.FirstSpan;
                    }
                    else
                    {
                        var workingSpan = GetWorkingBytes().AsSpan();
                        fileNameSequence.CopyTo(workingSpan);
                        fileNameSpan = workingSpan[..fileNameSize];
                    }

                    // trim off any ./ prefixes, they confuse things downstream
                    if (fileNameSpan.StartsWith(_currentDirectoryPrefix.Span))
                    {
                        fileNameSpan = fileNameSpan.TrimStart(_currentDirectoryPrefix.Span);
                    }

                    if (fileNameSpan.SequenceEqual(_currentDirectoryValue.Span) || fileNameSpan.SequenceEqual(_parentDirectoryValue.Span))
                    {
                        // we've found a current or parent directory entry
                        // ignore it...
                        sequenceReader.Advance(state.Metadata.FileNameSize);
                        sequenceReader.Advance(state.Metadata.FileSize);
                        state = state.Reset();
                        break;
                    }

                    if (fileNameSpan.SequenceEqual(_trailerValue.Span))
                    {
                        // we're found the TRAILER!!! entry
                        // we've reached the end of the file, exit early
                        sequenceReader.Advance(state.Metadata.FileNameSize);
                        sequenceReader.Advance(state.Metadata.FileSize);
                        state = state.OnTrailerRead();
                        break;
                    }

                    var filePath = Encoding.ASCII.GetString(fileNameSpan);
                    filePath = Path.Join(outputPath, filePath);
                    if (!Path.GetFullPath(filePath).StartsWith(outputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // path contains .. and is trying to break out
                        // of the extraction path - that's a no-no
                        throw new InvalidOperationException("Unexpected path traversal in filename");
                    }
                    
                    sequenceReader.Advance(state.Metadata.FileNameSize);
                    state = state.OnFileNameRead(filePath);
                    if (state.Metadata.Type == EntryType.File)
                    {
                        extractedFiles.Add(filePath);
                    }

                    if (state.Metadata.FileSize == 0)
                    {
                        // no bytes to read, skip the entry 
                        state = state.Reset();
                    }
                }
                else if (state.Operation == CpioReadOperation.FileData)
                {
                    // write all data in the sequence upto the expected length of the file
                    var totalBytesLeft = state.Metadata.FileSize - state.BytesWritten;
                    var bytesToRead = Math.Min(sequenceReader.UnreadSequence.Length, totalBytesLeft);
                    foreach (var segment in sequenceReader.UnreadSequence.Slice(0, bytesToRead))
                    {
                        state = state.OnFileDataRead(segment);
                    }

                    sequenceReader.Advance(bytesToRead);
                    if (state.BytesWritten == state.Metadata.FileSize)
                    {
                        // prepare for reading the next entry
                        state = state.Reset();
                    }
                }
            }

            consumed = sequenceReader.Position;
        }

        public ValueTask DisposeAsync()
        {
            if (_workingBytes != null)
            {
                ArrayPool<byte>.Shared.Return(_workingBytes);
            }
            
            return _pipeReader.CompleteAsync();
        }

        private enum EntryType
        {
            Directory = 1,
            File = 2,
            Other = 3,
        }
        
        /// <summary>
        /// CPIO ODC ASCII format header
        /// </summary>
        private readonly struct CpioEntryMetadata
        {
            public const int Length = 76;
            private CpioEntryMetadata(int fileNameSize, int fileSize, EntryType type)
            {
                FileNameSize = fileNameSize;
                FileSize = fileSize;
                Type = type;
            }
            
            public int FileNameSize { get; }
            public int FileSize { get; }
            public EntryType Type { get; }

            public enum ParseError
            {
                None,
                InvalidBufferSize,
                InvalidMagic,
                InvalidMode,
                InvalidFileNameSize,
                InvalidFileSize,
            }

            public static bool TryCreate(ReadOnlySpan<byte> buffer, out ParseError error, out CpioEntryMetadata metadata)
            {
                if (buffer.Length != 76)
                {
                    error = ParseError.InvalidBufferSize;
                    metadata = default;
                    return false;
                }

                var magicValue = buffer[..6];
                if (!magicValue.SequenceEqual(_magicValue.Span))
                {
                    error = ParseError.InvalidMagic;
                    metadata = default;
                    return false;
                }

                // we don't really care about much else other than the
                // mode, file name & size - parse out the octal strings and convert
                // to their underlying uint values
                var modeSpan = buffer.Slice(17, 6);
                var type = EntryType.Other;
                if (!Utils.TryParseOctalToUInt32(modeSpan, out var mode))
                {
                    error = ParseError.InvalidMode;
                    metadata = default;
                    return false;
                }

                const uint DirectoryMask = 2048;
                const uint FileMask = 4096;
                if ((mode & DirectoryMask) != 0)
                {
                    type = EntryType.Directory;
                }
                else if ((mode & FileMask) != 0)
                {
                    type = EntryType.File;
                }
                
                var nameSizeSpan = buffer.Slice(59, 6);
                if (!Utils.TryParseOctalToUInt32(nameSizeSpan, out var nameSize))
                {
                    error = ParseError.InvalidFileNameSize;
                    metadata = default;
                    return false;
                }

                var fileSizeSpan = buffer[65..];
                if (!Utils.TryParseOctalToUInt32(fileSizeSpan, out var fileSize))
                {
                    error = ParseError.InvalidFileSize;
                    metadata = default;
                    return false;
                }

                error = ParseError.None;
                metadata = new CpioEntryMetadata((int)nameSize, (int)fileSize, type);
                return true;
            }
        }

        private enum CpioReadOperation
        {
            Metadata,
            FileName,
            FileData,
            End,
        }
        
        private readonly struct CpioReaderState
        {
            private readonly Stream? _outputFile;

            public CpioReadOperation Operation { get; }
            public CpioEntryMetadata Metadata { get; }
            public long BytesWritten => _outputFile?.Length ?? 0;
            
            private CpioReaderState(CpioReadOperation operation, CpioEntryMetadata metadata, Stream? outputFile)
            {
                Operation = operation;
                Metadata = metadata;
                _outputFile = outputFile;
            }

            public CpioReaderState Reset()
            {
                _outputFile?.Dispose();
                return new CpioReaderState(
                    CpioReadOperation.Metadata, default, null
                );
            }

            public CpioReaderState OnMetadataRead(CpioEntryMetadata metadata) => new(
                CpioReadOperation.FileName, metadata, null
            );

            public CpioReaderState OnFileNameRead(string filePath)
            {
                var metadata = Metadata;
                if (metadata.Type == EntryType.File)
                {
                    // make sure any parent directory is created before we extract
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(filePath)!
                    );
                    return new(
                        CpioReadOperation.FileData, metadata, File.Create(filePath)
                    );
                }

                // other types are not supported
                return new(
                    CpioReadOperation.End, default, null
                );
            }

            public CpioReaderState OnFileDataRead(ReadOnlyMemory<byte> buffer)
            {
                _outputFile!.Write(buffer.Span);
                return this;
            }
            
            public CpioReaderState OnTrailerRead() => new(
                CpioReadOperation.End, default, null
            );
        }
    }
}