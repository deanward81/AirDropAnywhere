using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AirDropAnywhere.Core
{
    /// <summary>
    /// Internal utility functions.
    /// </summary>
    internal static class Utils
    {
        /// <summary>
        /// Asserts that we're running on a supported platform.
        /// </summary>
        public static void AssertPlatform()
        {
            // TODO: support linux
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new InvalidOperationException(
                    "AirDropAnywhere is currently only supported on MacOS because it needs support for the AWDL protocol."
                );
            }
        }

        /// <summary>
        /// Asserts that the system has an AWDL network interface.
        /// </summary>
        public static void AssertNetworkInterfaces()
        {
            var hasAwdlInterface = NetworkInterface.GetAllNetworkInterfaces().Any(i => i.IsAwdlInterface());
            if (!hasAwdlInterface)
            {
                throw new InvalidOperationException(
                    "No awdl0 interface found on this system. AirDrop.NET is currently only supported on systems that support the AWDL protocol."
                );
            }
        }

        /// <summary>
        /// Determines if the specified network interface is used for AWDL.
        /// </summary>
        public static bool IsAwdlInterface(this NetworkInterface networkInterface) => networkInterface.Id == "awdl0";

        private static readonly ReadOnlyMemory<byte> _trueSocketValue = BitConverter.GetBytes(1);
        
        public static void SetReuseAddressSocketOption(this Socket socket)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            const int SO_REUSE_ADDR = 0x4;
            socket.SetRawSocketOption(
                (int)SocketOptionLevel.Socket, SO_REUSE_ADDR, _trueSocketValue.Span
            );
        }
        
        /// <summary>
        /// Configures a socket so that it can communicate over an AWDL interface.
        /// </summary>
        public static void SetAwdlSocketOption(this Socket socket)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }
            
            // from Apple header files:
            // sys/socket.h: #define	SO_RECV_ANYIF	0x1104
            // ReSharper disable once InconsistentNaming
            // ReSharper disable once IdentifierTypo
            const int SO_RECV_ANYIF = 0x1104;
            socket.SetRawSocketOption(
                (int)SocketOptionLevel.Socket, SO_RECV_ANYIF, _trueSocketValue.Span
            );
        }
    }
}