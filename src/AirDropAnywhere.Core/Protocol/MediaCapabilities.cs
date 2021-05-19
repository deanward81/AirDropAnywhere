namespace AirDropAnywhere.Core.Protocol
{
    internal class MediaCapabilities
    {
        public static readonly MediaCapabilities Default = new(1);
        
        public MediaCapabilities(int version)
        {
            Version = version;
        }
        
        public int Version { get; private set; }
    }
}