#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace AirDropAnywhere.Cli.Hubs
{
    internal class CanAcceptFilesRequestMessage : AirDropHubMessage
    {
        public string SenderComputerName { get; set; }
        public byte[] FileIcon { get; set; }
        public IEnumerable<CanAcceptFileMetadata> Files { get; set; } = Enumerable.Empty<CanAcceptFileMetadata>();
    }
}