#nullable disable
namespace AirDropAnywhere.Cli.Hubs
{
    internal class OnFileUploadedRequestMessage : AirDropHubMessage
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }
}