using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirDropAnywhere.Core.Compression
{
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
        
        public async ValueTask ExtractAsync(string outputPath, CancellationToken cancellationToken = default)
        {
            if (outputPath == null)
            {
                throw new ArgumentNullException(nameof(outputPath));
            }

            if (!Path.IsPathFullyQualified(outputPath))
            {
                throw new ArgumentException("Output path must be fully qualified.", nameof(outputPath));
            }

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
        }

        private void ExtractArchiveEntries(
            ref ReadOnlySequence<byte> sequence,
            ref CpioReaderState state,
            string outputPath, 
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
                        // we're found the TRAILER!! entry
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

        /// <summary>
        /// CPIO ODC ASCII format header
        /// </summary>
        private readonly struct CpioEntryMetadata
        {
            public const int Length = 76;
            private CpioEntryMetadata(int fileNameSize, int fileSize)
            {
                FileNameSize = fileNameSize;
                FileSize = fileSize;
            }
            
            public int FileNameSize { get; }
            public int FileSize { get; }

            public enum ParseError
            {
                None,
                InvalidBufferSize,
                InvalidMagic,
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
                // file name & size, and the timestamp - parse out the octal strings
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
                metadata = new CpioEntryMetadata((int)nameSize, (int)fileSize);
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

            public CpioReaderState OnFileNameRead(string filePath) => new(
                CpioReadOperation.FileData, Metadata, File.Create(filePath)
            );
            
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