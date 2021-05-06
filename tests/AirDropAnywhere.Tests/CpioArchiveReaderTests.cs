using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AirDropAnywhere.Core.Compression;
using Xunit;
using Xunit.Abstractions;

namespace AirDropAnywhere.Tests
{
    public class CpioArchiveReaderTests : IDisposable
    {
        private readonly string _outputPath;
        private readonly ITestOutputHelper _log;
        
        public CpioArchiveReaderTests(ITestOutputHelper log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _outputPath = Path.Join(
                Environment.CurrentDirectory, "extracted", DateTime.UtcNow.ToString("yyyyMMddTHHmm.fff")
            );
            
            if (!Directory.Exists(_outputPath))
            {
                Directory.CreateDirectory(_outputPath);
            }
            
            _log.WriteLine("Extracting files to {0}", _outputPath);
        }
        
        [Fact]
        public async Task ExtractSingleFile()
        {
            await using var fileStream = File.OpenRead("test.single.cpio");
            await using var cpioArchiveReader = CpioArchiveReader.Create(fileStream);
            await cpioArchiveReader.ExtractAsync(_outputPath);
            
            // we're expecting just one file of length 33 bytes
            var directoryInfo = new DirectoryInfo(_outputPath);
            var files = directoryInfo.GetFiles();
            Assert.Single(files, f => f.Length == 33);
        }
        
        [Fact]
        public async Task ExtractMultipleFiles()
        {
            await using var fileStream = File.OpenRead("test.multiple.cpio");
            await using var cpioArchiveReader = CpioArchiveReader.Create(fileStream);
            await cpioArchiveReader.ExtractAsync(_outputPath);
            
            // we're expecting 100 files, each of length 1024
            var directoryInfo = new DirectoryInfo(_outputPath);
            var files = directoryInfo.GetFiles();
            Assert.Equal(100, files.Length);
            Assert.True(files.All(f => f.Length == 1024));
        }
        
        [Fact]
        public async Task ExtractLargeFiles()
        {
            await using var fileStream = File.OpenRead("test.large.cpio");
            await using var cpioArchiveReader = CpioArchiveReader.Create(fileStream);
            await cpioArchiveReader.ExtractAsync(_outputPath);
            
            // we're expecting 5 files, each of length 10240
            var directoryInfo = new DirectoryInfo(_outputPath);
            var files = directoryInfo.GetFiles();
            Assert.Equal(5, files.Length);
            Assert.True(files.All(f => f.Length == 10240));
        }

        public void Dispose()
        {
            if (Directory.Exists(_outputPath))
            {
             //   Directory.Delete(_outputPath, true);
            }
        }
    }
}
