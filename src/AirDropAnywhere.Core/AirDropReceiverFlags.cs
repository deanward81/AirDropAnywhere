using System;

namespace AirDropAnywhere.Core
{
    /// <summary>
    /// Flags attached to mDNS responses indicating what is supported by
    /// the AirDrop service.
    /// </summary>
    /// <remarks>
    /// Derived from OpenDrop: https://github.com/seemoo-lab/opendrop/blob/master/opendrop/config.py#L32-L51
    /// On MacOS the default is configured to be 0x3fb which is defined in <see cref="DefaultMacOS" />.
    /// OpenDrop currently supports a subset of this so we'll use that as our default for now.
    /// </remarks>
    [Flags]
    internal enum AirDropReceiverFlags : ushort
    {
        Url = 1 << 0,
        DvZip = 1 << 1,
        Pipelining = 1 << 2,
        MixedTypes = 1 << 3,
        Unknown1 = 1 << 4,
        Unknown2 = 1 << 5,
        Iris = 1 << 6,
        Discover = 1 << 7,
        Unknown3 = 1 << 8,
        AssetBundle = 1 << 9,
        
        /// <summary>
        /// Default broadcast by MacOS
        /// </summary>
        DefaultMacOS = Url | DvZip | MixedTypes | Unknown1 | Unknown2 | Iris | Discover | Unknown3 | AssetBundle,
        /// <summary>
        /// Default used by AirDropAnywhere, will extend as more of AirDrop is implemented.
        /// </summary>
        Default = Url | Pipelining | MixedTypes | Discover | AssetBundle,
    }
}