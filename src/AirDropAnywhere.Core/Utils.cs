using System;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AirDropAnywhere.Core.Serialization;
using Microsoft.AspNetCore.Http;

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
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new InvalidOperationException(
                    "AirDropAnywhere is currently only supported on MacOS or Linux because it needs support for either the AWDL or OWL protocol."
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
                    "No awdl0 interface found on this system. AirDrop Anywhere is currently only supported on systems that support the AWDL protocol."
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
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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
        
        /// <summary>
        /// Writes an object of the specified type from the HTTP request using Apple's plist binary format.
        /// </summary>
        public static ValueTask<T> ReadFromPropertyListAsync<T>(this HttpRequest request) where T : class, new()
        {
            if (!request.ContentLength.HasValue || request.ContentLength > PropertyListSerializer.MaxPropertyListLength)
            {
                throw new HttpRequestException("Content length is too long.");
            }

            return PropertyListSerializer.DeserializeAsync<T>(request.Body);
        }
        
        /// <summary>
        /// Writes the specified object to the HTTP response in Apple's plist binary format.
        /// </summary>
        public static ValueTask WriteAsPropertyListAsync<T>(this HttpResponse response, T obj) where T : class
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }

            response.ContentType = "application/octet-stream";
            return PropertyListSerializer.SerializeAsync(obj, response.Body);
        }

        /// <summary>
        /// Generates a 12 character random string. 
        /// </summary>
        public static string GetRandomString()
        {
            const string charset = "abcdefghijklmnopqrstuvwxyz0123456789";
            Span<byte> bytes = stackalloc byte[12];
            Span<char> chars = stackalloc char[12];
            RandomNumberGenerator.Fill(bytes);

            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = charset[bytes[i] % (charset.Length)];
            }

            return new string(chars);
        }
        
        private static bool TryGetOctalDigit(byte c, out int value)
        {
            value = c - '0';
            return value <= 7;
        }
                
        /// <summary>
        /// Parses an octal string to a <see cref="uint"/>.
        /// </summary>
        /// <remarks>
        /// Ripped off a little from .NET Core code
        /// https://source.dot.net/#System.Private.CoreLib/ParseNumbers.cs,574
        /// </remarks>
        public static bool TryParseOctalToUInt32(ReadOnlySpan<byte> s, out uint value)
        {
            if (s.Length == 0)
            {
                value = default;
                return false;
            }
            
            uint result = 0;
            const uint maxValue = uint.MaxValue / 8;
                 
            var i = 0;
            // Read all of the digits and convert to a number
            while (i < s.Length && TryGetOctalDigit(s[i], out var digit))
            {
                if (result > maxValue)
                {
                    value = default;
                    return false;
                } 
                        
                uint temp = result * (uint) 8 + (uint) digit;
                if (temp > maxValue)
                {
                    value = default;
                    return false;
                }
                result = temp;
                i++;
            }

            if (i != s.Length)
            {
                value = default;
                return false;
            }
            
            value = result;
            return true;
        }

    }
}