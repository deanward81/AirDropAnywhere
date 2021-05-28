namespace AirDropAnywhere.Cli.Hubs
{
    internal class CanAcceptFilesResponseMessage : AirDropHubMessage
    {
        public bool Accepted { get; set; }
    }
}