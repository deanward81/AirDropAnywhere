// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ClassNeverInstantiated.Global
#pragma warning disable 8618
namespace AirDropAnywhere.Core.Protocol
{
    public class FileMetadata
    {
        /// <summary>
        /// Gets the name of the file.
        /// </summary>
        public string FileName { get; private set; }
        /// <summary>
        /// Gets the type of the file.
        /// </summary>
        public string FileType { get; private set; }
        public string FileBomPath { get; private set; }
        /// <summary>
        /// Gets a value indicating whether the "file" is actually a directory.
        /// </summary>
        public bool FileIsDirectory { get; private set; }
        public bool ConvertMediaFormats { get; private set; }
    }
}