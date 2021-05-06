using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Claunia.PropertyList;

namespace AirDropAnywhere.Core.Serialization
{
    /// <summary>
    /// Helper class for serializing .NET types to / from property lists.
    /// </summary>
    internal class PropertyListSerializer
    {
        public const int MaxPropertyListLength = 1024 * 1024; // 1 MiB
        
        public static async ValueTask<T> DeserializeAsync<T>(Stream stream)
        {
            // this probably all seems a little convoluted but
            // plist-cil works best when it's passed a ReadOnlySpan<byte>
            // so try to minimize allocations as much as possible in this path
            var buffer = ArrayPool<byte>.Shared.Rent(MaxPropertyListLength);
            try
            {
                using (var memoryStream = new MemoryStream(buffer, 0, MaxPropertyListLength, true))
                {
                    await stream.CopyToAsync(memoryStream, 4096);
                    return Deserialize<T>(buffer.AsSpan()[..(int)memoryStream.Position]);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        
        public static T Deserialize<T>(ReadOnlySpan<byte> buffer)
        {
            return PropertyListConverter.ToObject<T>(
                PropertyListParser.Parse(buffer)
            );
        }

        public static async ValueTask SerializeAsync(object obj, Stream stream)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(MaxPropertyListLength);
            try
            {
                using (var memoryStream = new MemoryStream(buffer, true))
                {
                    BinaryPropertyListWriter.Write(
                        memoryStream, PropertyListConverter.ToNSObject(obj)
                    );
                    
                    await stream.WriteAsync(buffer, 0, (int)memoryStream.Position);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}