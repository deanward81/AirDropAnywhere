// MIT license
// https://github.com/enclave-networks/research.udp-perf/tree/main/Enclave.UdpPerf

using Microsoft.Extensions.ObjectPool;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// ReSharper disable once CheckNamespace
namespace Enclave.UdpPerf
{
    internal static class UdpSocketExtensions
    {
        // This pool of socket events means that we don't need to keep allocating the SocketEventArgs.
        // The main reason we want to pool these (apart from just reducing allocations), is that, on windows at least, within the depths 
        // of the underlying SocketAsyncEventArgs implementation, each one holds an instance of PreAllocatedNativeOverlapped,
        // an IOCP-specific object which is VERY expensive to allocate each time.        
        private static readonly ObjectPool<UdpAwaitableSocketAsyncEventArgs> _socketEventPool = ObjectPool.Create<UdpAwaitableSocketAsyncEventArgs>();

        /// <summary>
        /// Send a block of data to a specified destination, and complete asynchronously.
        /// </summary>
        /// <param name="socket">The socket to send on.</param>
        /// <param name="destination">The destination of the data.</param>
        /// <param name="data">The data buffer itself.</param>
        /// <returns>The number of bytes transferred.</returns>
        public static async ValueTask<int> SendToAsync(this Socket socket, EndPoint destination, ReadOnlyMemory<byte> data)
        {
            // Get an async argument from the socket event pool.
            var asyncArgs = _socketEventPool.Get();

            asyncArgs.RemoteEndPoint = destination;
            asyncArgs.SetBuffer(MemoryMarshal.AsMemory(data));

            try
            {
                return await asyncArgs.DoSendToAsync(socket);
            }
            finally
            {
                _socketEventPool.Return(asyncArgs);
            }
        }

        /// <summary>
        /// Asynchronously receive a block of data, getting the amount of data received, and the remote endpoint that
        /// sent it.
        /// </summary>
        /// <param name="socket">The socket to send on.</param>
        /// <param name="endpoint"><see cref="IPEndPoint"/> to send data to.</param>
        /// <param name="buffer">The buffer to place data in.</param>
        /// <returns>The number of bytes transferred.</returns>
        public static async ValueTask<SocketReceiveResult> ReceiveFromAsync(this Socket socket, IPEndPoint endpoint, Memory<byte> buffer)
        {
            // Get an async argument from the socket event pool.
            var asyncArgs = _socketEventPool.Get();

            asyncArgs.RemoteEndPoint = endpoint;
            asyncArgs.SetBuffer(buffer);

            try
            {
                var recvdBytes = await asyncArgs.ReceiveFromAsync(socket);

                return new SocketReceiveResult(asyncArgs.RemoteEndPoint, asyncArgs.ReceiveMessageFromPacketInfo, recvdBytes);

            }
            finally
            {
                _socketEventPool.Return(asyncArgs);
            }
        }

        public readonly struct SocketReceiveResult
        {
            public SocketReceiveResult(
                EndPoint endpoint,
                IPPacketInformation packetInformation,
                int receivedBytes
            )
            {
                Endpoint = endpoint;
                PacketInformation = packetInformation;
                ReceivedBytes = receivedBytes;
            }
            
            public EndPoint Endpoint { get; }
            public IPPacketInformation PacketInformation { get; }
            public int ReceivedBytes { get; }
        }
    }
}
