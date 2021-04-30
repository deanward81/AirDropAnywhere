using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Enclave.UdpPerf;
using Makaretu.Dns;
using Makaretu.Dns.Resolving;

namespace AirDropAnywhere.Core.MulticastDns
{
    /// <summary>
    /// Handles advertising mDNS services by responding to matching requests. 
    /// </summary>
    internal class MulticastDnsServer
    {
        private const int MulticastPort = 5353;
        // ReSharper disable InconsistentNaming
        private static readonly IPAddress MulticastAddressIPv4 = IPAddress.Parse("224.0.0.251");
        private static readonly IPAddress MulticastAddressIPv6 = IPAddress.Parse("FF02::FB");
        private static readonly IPEndPoint MulticastEndpointIPv4 = new(MulticastAddressIPv4, MulticastPort);
        private static readonly IPEndPoint MulticastEndpointIPv6 = new(MulticastAddressIPv6, MulticastPort);
        // ReSharper restore InconsistentNaming

        private readonly ImmutableArray<NetworkInterface> _networkInterfaces;
        private readonly CancellationToken _cancellationToken;
        private readonly NameServer _nameServer;

        private ImmutableDictionary<AddressFamily, SocketListener>? _listeners;
        private ImmutableDictionary<AddressFamily, SocketClient>? _unicastClients;
        private ImmutableDictionary<(AddressFamily, int), SocketClient>? _multicastClients;
        private List<Task>? _listenerTasks;

        public MulticastDnsServer(ImmutableArray<NetworkInterface> networkInterfaces, CancellationToken cancellationToken)
        {
            if (networkInterfaces.IsDefaultOrEmpty)
            {
                throw new ArgumentException("Interfaces are required.", nameof(networkInterfaces));
            }
            
            _networkInterfaces = networkInterfaces;
            _cancellationToken = cancellationToken;
            _nameServer = new NameServer()
            {
                Catalog = new Catalog()
            };
        }

        public async ValueTask RegisterAsync(MulticastDnsService service)
        {
            // add services to the name service
            // and pre-emptively announce ourselves across our multicast clients
            var catalog = _nameServer.Catalog;
            var msg = service.ToMessage();
            
            catalog.Add(
                new PTRRecord { Name = MulticastDnsService.Discovery, DomainName = service.QualifiedServiceName },
                authoritative: true
            );
            catalog.Add(
                new PTRRecord { Name = service.QualifiedServiceName, DomainName = service.QualifiedInstanceName },
                authoritative: true
            );
            
            foreach (var resourceRecord in msg.Answers)
            {
                catalog.Add(resourceRecord, authoritative: true);
            }

            if (_multicastClients != null)
            {
                foreach (var client in _multicastClients.Values)
                {
                    await client.SendAsync(msg);
                }
            }
        }
        
        public ValueTask StartAsync()
        {
            var multicastClients = ImmutableDictionary.CreateBuilder<(AddressFamily, int), SocketClient>();
            var unicastClients = ImmutableDictionary.CreateBuilder<AddressFamily, SocketClient>();
            var listeners = ImmutableDictionary.CreateBuilder<AddressFamily, SocketListener>();
            foreach (var networkInterface in _networkInterfaces)
            {
                var interfaceProperties = networkInterface.GetIPProperties();
                // grab the addresses for each interface
                // and configure a listeners and clients for each one to handle
                // sending and receiving of multicast traffic
                var addresses = GetNetworkInterfaceLocalAddresses(networkInterface);
                foreach (var address in addresses)
                {
                    if (!listeners.ContainsKey(address.AddressFamily))
                    {
                        listeners.Add(address.AddressFamily, CreateListener(address));
                    }

                    if (!unicastClients.ContainsKey(address.AddressFamily))
                    {
                        unicastClients.Add(address.AddressFamily, CreateUnicastClient(address));
                    }

                    var interfaceIndex = 0;
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        interfaceIndex = interfaceProperties.GetIPv4Properties().Index;
                    }
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        interfaceIndex = interfaceProperties.GetIPv6Properties().Index;
                    }

                    multicastClients.Add((address.AddressFamily, interfaceIndex), CreateMulticastClient(address));
                }
            }

            _listeners = listeners.ToImmutable();
            _unicastClients = unicastClients.ToImmutable();
            _multicastClients = multicastClients.ToImmutable();
            _listenerTasks = new List<Task>(_listeners.Count);
            foreach (var listener in _listeners.Values)
            {
                _listenerTasks.Add(
                    Task.Run(
                        async () =>
                        {
                            await foreach (var receiveResult in listener.ReceiveAsync(_cancellationToken))
                            {
                                var request = receiveResult.Message;
                                if (!request.IsQuery)
                                {
                                    continue;
                                }
                                
                                // normalize unicast responses
                                // see https://github.com/richardschneider/net-mdns/blob/master/src/ServiceDiscovery.cs#L382-L392
                                var useUnicast = false;
                                foreach (var q in request.Questions)
                                {
                                    if (((ushort)q.Class & 0x8000) != 0)
                                    {
                                        useUnicast = true;
                                        q.Class = (DnsClass)((ushort)q.Class & 0x7fff);
                                    }
                                }
                                
                                var response = await _nameServer.ResolveAsync(request, _cancellationToken);
                                if (response.Status != MessageStatus.NoError)
                                {
                                    // couldn't resolve the request, ignore it
                                    continue;
                                }
                                
                                // All MDNS answers are authoritative and have a transaction
                                // ID of zero.
                                response.AA = true;
                                response.Id = 0;

                                // All MDNS answers must not contain any questions.
                                response.Questions.Clear();

                                var endpoint = receiveResult.Endpoint;
                                var packetInformation = receiveResult.PacketInformation;
                                if (useUnicast && _unicastClients!.TryGetValue(endpoint.AddressFamily, out var client))
                                {
                                    // a unicast response is needed for this query
                                    // send a response directly to the endpoint that sent it
                                    await client.SendAsync(response, endpoint);
                                }
                                else if (!useUnicast && _multicastClients!.TryGetValue((endpoint.AddressFamily, packetInformation.Interface), out client))
                                {
                                    // send a multicast response using 
                                    // the specific interface that the query was received on
                                    await client.SendAsync(response);
                                }
                            }
                        }
                    )
                );
            }
            return default;
        }

        public async ValueTask StopAsync()
        {
            if (_listenerTasks != null)
            {
                await Task.WhenAll(_listenerTasks);
            }

            if (_multicastClients != null)
            {
                foreach (var client in _multicastClients.Values)
                {
                    client.Socket.Dispose();
                }
            }
            
            if (_unicastClients != null)
            {
                foreach (var client in _unicastClients.Values)
                {
                    client.Socket.Dispose();
                }
            }

            if (_listeners != null)
            {
                foreach (var listener in _listeners.Values)
                {
                    listener.Socket.Dispose();
                }
            }
            
            _listeners = null;
            _listenerTasks = null;
            _multicastClients = null;
            _unicastClients = null;
        }
        
        private static IEnumerable<IPAddress> GetNetworkInterfaceLocalAddresses(NetworkInterface networkInterface)
        {
            return networkInterface
                    .GetIPProperties()
                    .UnicastAddresses
                    .Select(x => x.Address)
                    .Where(x => x.AddressFamily != AddressFamily.InterNetworkV6 || x.IsIPv6LinkLocal)
                ;
        }
        
        private static SocketListener CreateListener(IPAddress ipAddress)
        {
            var localEndpoint = default(IPEndPoint);
            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                localEndpoint = new IPEndPoint(IPAddress.Any, MulticastPort);
            }
            else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                localEndpoint = new IPEndPoint(IPAddress.IPv6Any, MulticastPort);
            }
            else
            {
                throw new ArgumentException($"Unsupported IP address: {ipAddress}", nameof(ipAddress));
            }

            var socket = new Socket(ipAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.SetAwdlSocketOption();
            socket.SetReuseAddressSocketOption();
            
            if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(MulticastAddressIPv6, ipAddress.ScopeId));
            }
            else if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddressIPv4, ipAddress));
            }
            socket.Bind(localEndpoint);
            return new SocketListener(localEndpoint, socket);
        }

        private static SocketClient CreateUnicastClient(IPAddress ipAddress)
        {
            var localEndpoint = default(IPEndPoint);
            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                localEndpoint = new IPEndPoint(IPAddress.Any, 0);
            }
            else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                localEndpoint = new IPEndPoint(IPAddress.IPv6Any, 0);
            }
            else
            {
                throw new ArgumentException($"Unsupported IP address: {ipAddress}", nameof(ipAddress));
            }

            var socket = new Socket(ipAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.SetAwdlSocketOption();
            socket.SetReuseAddressSocketOption();
            socket.Bind(localEndpoint);
            return new SocketClient(localEndpoint, socket);
        }
        
        private static SocketClient CreateMulticastClient(IPAddress ipAddress)
        {
            var remoteEndpoint = default(IPEndPoint);
            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                remoteEndpoint = MulticastEndpointIPv4;
            }
            else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ipAddress.ScopeId > 0)
                {
                    remoteEndpoint = new IPEndPoint(
                        new IPAddress(MulticastAddressIPv6.GetAddressBytes(), ipAddress.ScopeId),
                        MulticastPort
                    );
                }
                else
                {
                    remoteEndpoint = MulticastEndpointIPv6;
                }
            }
            else
            {
                throw new ArgumentException($"Unsupported IP address: {ipAddress}", nameof(ipAddress));
            }
            
            var socket = new Socket(ipAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.SetAwdlSocketOption();
            socket.SetReuseAddressSocketOption();
            socket.Bind(new IPEndPoint(ipAddress, MulticastPort));
            return new SocketClient(remoteEndpoint, socket);
        }
        
        private readonly struct SocketClient
        {
            public SocketClient(IPEndPoint endpoint, Socket socket)
            {
                Endpoint = endpoint;
                Socket = socket;
            }
            
            public IPEndPoint Endpoint { get; }
            public Socket Socket { get; }

            public async ValueTask SendAsync(Message message, IPEndPoint? endpoint = null)
            {
                // allocate a small buffer for our packets
                var buffer = ArrayPool<byte>.Shared.Rent(Message.MaxLength);
                var bufferMemory = buffer.AsMemory();
                try
                {
                    int length;
                    await using (var memoryStream = new MemoryStream(buffer, true))
                    {
                        message.Write(memoryStream);
                        length = (int) memoryStream.Position;
                    }
                    await Socket.SendToAsync(endpoint ?? Endpoint, bufferMemory[..length]);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        
        private readonly struct SocketListener
        {
            public SocketListener(IPEndPoint endpoint, Socket socket)
            {
                Endpoint = endpoint;
                Socket = socket;
            }
            
            public IPEndPoint Endpoint { get; }
            public Socket Socket { get; }
            
            public async IAsyncEnumerable<ReceiveResult> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                // allocate a small buffer for our packets
                var buffer = GC.AllocateArray<byte>(Message.MaxLength, true);
                var bufferMemory = buffer.AsMemory();
                while (!cancellationToken.IsCancellationRequested)
                {
                    // continually listen for messages from the socket
                    // decode them and queue them into the receiving channel
                    var result = await Socket.ReceiveFromAsync(Endpoint, bufferMemory);
                    // ideally this would use a pooled set of message objects
                    // rather than allocating a new one each time but the underlying
                    // API doesn't readily support such things
                    var message = new Message();
                    message.Read(buffer, 0, result.ReceivedBytes);
                    yield return new ReceiveResult(message, (IPEndPoint) result.Endpoint, result.PacketInformation);
                }
            }
        }
        
        private readonly struct ReceiveResult
        {
            public ReceiveResult(Message message, IPEndPoint endpoint, IPPacketInformation packetInformation)
            {
                Message = message;
                Endpoint = endpoint;
                PacketInformation = packetInformation;
            }
            
            public Message Message { get; }
            public IPEndPoint Endpoint { get; }
            public IPPacketInformation PacketInformation { get; }
        }
    }
}