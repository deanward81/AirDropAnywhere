// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ClassNeverInstantiated.Global
#pragma warning disable 8618
namespace AirDropAnywhere.Core.Protocol
{
    internal class FileMetadata
    {
        public string FileName { get; private set; }
        public string FileType { get; private set; }
        public string FileBomPath { get; private set; }
        public bool FileIsDirectory { get; private set; }
        public bool ConvertMediaFormats { get; private set; }
    }
}