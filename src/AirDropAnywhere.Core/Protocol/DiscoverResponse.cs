using System.Text.Json;

namespace AirDropAnywhere.Core.Protocol
{
    /// <summary>
    /// Body of a response from the /Discover endpoint in the AirDrop HTTP API.
    /// </summary>
    internal class DiscoverResponse
    {
        public DiscoverResponse(string computerName, string modelName, MediaCapabilities mediaCapabilities)
        {
            ReceiverComputerName = computerName;
            ReceiverModelName = modelName;
            ReceiverMediaCapabilities = JsonSerializer.SerializeToUtf8Bytes(mediaCapabilities);
            // TODO: implement contact data
            //ReceiverRecordData = Array.Empty<byte>();
        }
        
        /// <summary>
        /// Gets the receiver computer's name. Displayed when selecting a "contact" to send to.
        /// </summary>
        public string ReceiverComputerName { get; }
        /// <summary>
        /// Gets the model name of the receiver.
        /// </summary>
        public string ReceiverModelName { get; }
        /// <summary>
        /// Gets the UTF-8 encoded bytes of a JSON payload detailing the
        /// media capabilities of the receiver.
        /// </summary>
        /// <remarks>
        /// This payload is represented in code by the <see cref="MediaCapabilities"/> class.
        /// </remarks>
        public byte[] ReceiverMediaCapabilities { get; }
        //public byte[] ReceiverRecordData { get; }
    }
}