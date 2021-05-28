// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable InconsistentNaming
#pragma warning disable 8618
using System.Collections.Generic;
using System.Linq;

namespace AirDropAnywhere.Core.Protocol
{
    /// <summary>
    /// Body of a request to the /Ask endpoint in the AirDrop HTTP API.
    /// </summary>
    public class AskRequest
    {
        /// <summary>
        /// Gets the sender computer's name. Displayed when asking for receiving a file not from a contact
        /// </summary>
        public string SenderComputerName { get; private set; }
        /// <summary>
        /// Gets the model name of the sender
        /// </summary>
        public string SenderModelName { get; private set; }
        /// <summary>
        /// Gets the service id distributed over mDNS
        /// </summary>
        public string SenderID { get; private set; }
        /// <summary>
        /// Gets the bundle id of the sending application
        /// </summary>
        public string BundleID { get; private set; }
        /// <summary>
        /// Gets a value indicating whether the sender wants that media formats are converted
        /// </summary>
        public bool ConvertMediaFormats { get; private set; }
        /// <summary>
        /// Gets the sender's contact information.
        /// </summary>
        public byte[] SenderRecordData { get; private set; }
        /// <summary>
        /// Gets a JPEG2000 encoded file icon used for display.
        /// </summary>
        public byte[] FileIcon { get; private set; }
        /// <summary>
        /// Gets an <see cref="IEnumerable{T}"/> of <see cref="FileMetadata"/> objects
        /// containing metadata about the files the sender wishes to send.
        /// </summary>
        public IEnumerable<FileMetadata> Files { get; private set; } = Enumerable.Empty<FileMetadata>();
    }
}