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
using System.Threading.Channels;
using System.Threading.Tasks;
using Enclave.UdpPerf;
using Makaretu.Dns;
using Makaretu.Dns.Resolving;

namespace AirDropAnywhere.Core.MulticastDns
{
    /// <summary>
    /// Manages advertising and discovering mDNS services across one or more network interfaces. 
    /// </summary>
    public class MulticastDnsManager : IAsyncDisposable
    {
        private const int MulticastPort = 5353;
        // ReSharper disable InconsistentNaming
        private static readonly IPAddress MulticastAddressIPv4 = IPAddress.Parse("224.0.0.251");
        private static readonly IPAddress MulticastAddressIPv6 = IPAddress.Parse("FF02::FB");
        private static readonly IPEndPoint MulticastEndpointIPv4 = new(MulticastAddressIPv4, MulticastPort);
        private static readonly IPEndPoint MulticastEndpointIPv6 = new(MulticastAddressIPv6, MulticastPort);
        // ReSharper restore InconsistentNaming

        private readonly ImmutableArray<NetworkInterface> _networkInterfaces;
        private readonly NameServer _nameServer;

        private CancellationTokenSource? _cancellationTokenSource;
        private ImmutableDictionary<Guid, ChannelWriter<Message>>? _responseHandlers;
        private ImmutableDictionary<AddressFamily, SocketListener>? _listeners;
        private ImmutableDictionary<AddressFamily, SocketClient>? _unicastClients;
        private ImmutableDictionary<(AddressFamily, int), SocketClient>? _multicastClients;
        private List<Task>? _listenerTasks;

        private MulticastDnsManager(ImmutableArray<NetworkInterface> networkInterfaces)
        {
            if (networkInterfaces.IsDefaultOrEmpty)
            {
                throw new ArgumentException("Interfaces are required.", nameof(networkInterfaces));
            }
            
            _networkInterfaces = networkInterfaces.ToImmutableArray();
            _nameServer = new NameServer
            {
                Catalog = new Catalog()
            };
        }

        /// <summary>
        /// Creates a new instance of a <see cref="MulticastDnsManager"/>.
        /// </summary>
        /// <param name="networkInterfaces">
        /// Network interfaces that the instance should bind to.
        /// </param>
        /// <param name="cancellationToken">
        /// <see cref="CancellationToken"/> used to teardown 
        /// </param>
        /// <returns></returns>
        public static async ValueTask<MulticastDnsManager> CreateAsync(ImmutableArray<NetworkInterface> networkInterfaces, CancellationToken cancellationToken)
        {
            var mgr = new MulticastDnsManager(networkInterfaces);
            await mgr.InitializeAsync(cancellationToken);
            return mgr;
        }

        /// <summary>
        /// Gets all multicast capable network interfaces that are currently _up_.
        /// </summary>
        /// <returns>
        /// An <see cref="IEnumerable{T}"/> of <see cref="NetworkInterface"/> objects.
        /// </returns>
        public static IEnumerable<NetworkInterface> GetMulticastInterfaces() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.SupportsMulticast)
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Unknown)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Ppp);

        /// <summary>
        /// Discovers all instances of a service, resolving each one to the underlying
        /// host and port that it is hosted on. 
        /// </summary>
        /// <param name="serviceName">
        /// Name of a service to resolve, e.g. _airdrop_proxy._tcp
        /// </param>
        /// <returns>
        /// An <see cref="IAsyncEnumerable{T}"/> of <see cref="DnsEndPoint"/> representing
        /// </returns>
        public async IAsyncEnumerable<DnsEndPoint> DiscoverAsync(string serviceName, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = Channel.CreateUnbounded<Message>();
            var key = Guid.NewGuid();
            var serviceDomain = DomainName.Join(serviceName, MulticastDnsService.Root);

            // add a handler so responses can be dealt with...
            _responseHandlers = _responseHandlers!.Add(key, channel.Writer);

            static Message CreateQuery(DomainName name, DnsType type) => new()
            {
                Opcode = MessageOperation.Query,
                Questions =
                {
                    new()
                    {
                        Name = name,
                        Class = DnsClass.IN,
                        Type = type
                    }
                }
            };

            // first up send a query for any PTR records associated with the service
            await SendMulticastAsync(
                CreateQuery(serviceDomain, DnsType.PTR)
            );

            // enumerate any responses for the query we sent
            try
            {
                await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).WithCancellation(cancellationToken))
                {
                    foreach (var answer in message.Answers)
                    {
                        switch (answer)
                        {
                            case PTRRecord ptr when ptr.DomainName.IsSubdomainOf(serviceDomain):
                                // we have a PTR record, use its data to query for any SRV records
                                await SendMulticastAsync(
                                    CreateQuery(ptr.DomainName, DnsType.SRV)
                                );
                                break;
                            case SRVRecord srv when srv.Name.IsSubdomainOf(serviceDomain):
                                // we have a SRV record - yield its target and port as a DnsEndPoint
                                yield return new DnsEndPoint(srv.Target.ToString(), srv.Port);
                                break;
                        }
                    }
                }
            }
            finally
            {
                // we're done handling things, remove our handler
                _responseHandlers = _responseHandlers.Remove(key);
                
                // and close down the channel so nothing else can be written to it
                channel.Writer.Complete();
            }
        }

                
        internal async ValueTask RegisterAsync(MulticastDnsService service)
        {
            // add services to the name service
            // and pre-emptively announce ourselves across our multicast clients
            var catalog = _nameServer.Catalog;
            var msg = service.ToMessage();

            catalog.Add(
                new PTRRecord
                {
                    DomainName = service.QualifiedServiceName,
                    Name = MulticastDnsService.Discovery,
                    TTL = MulticastDnsService.DefaultTTL,
                },
                authoritative: true
            );
            catalog.Add(
                new PTRRecord
                {
                    DomainName = service.QualifiedInstanceName,
                    Name = service.QualifiedServiceName,
                    TTL = MulticastDnsService.DefaultTTL,
                },
                authoritative: true
            );

            foreach (var resourceRecord in msg.Answers)
            {
                catalog.Add(resourceRecord, authoritative: true);
            }

            await SendMulticastAsync(msg);
        }

        internal async ValueTask UnregisterAsync(MulticastDnsService service)
        {
            var catalog = _nameServer.Catalog;

            // remove all services advertised under this name
            catalog.TryRemove(service.QualifiedServiceName, out _);
            catalog.TryRemove(service.QualifiedInstanceName, out _);
            catalog.TryRemove(service.HostName, out _);

            // and pre-emptively announce ourselves with a 0 TTL
            var msg = service.ToMessage();

            foreach (var answer in msg.Answers)
            {
                answer.TTL = TimeSpan.Zero;
            }

            foreach (var additionalRecord in msg.AdditionalRecords)
            {
                additionalRecord.TTL = TimeSpan.Zero;
            }

            await SendMulticastAsync(msg);
        }
        
        private ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            var multicastClients = ImmutableDictionary.CreateBuilder<(AddressFamily, int), SocketClient>();
            var unicastClients = ImmutableDictionary.CreateBuilder<AddressFamily, SocketClient>();
            var listeners = ImmutableDictionary.CreateBuilder<AddressFamily, SocketListener>();
            foreach (var networkInterface in _networkInterfaces)
            {
                var interfaceProperties = networkInterface.GetIPProperties();
                // grab the addresses for each interface
                // and configure the relevant listeners and clients for each one to handle
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
                    // multicast clients are keyed by both the address family
                    // and the index of the interface they are associated with.
                    // When sending a multicast message we enumerate and send to
                    // _all_ clients. When responding to a multicast message we
                    // respond using the client associated with the interface index
                    // that the message was received on, otherwise the sender
                    // never sees the response.
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

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _responseHandlers = ImmutableDictionary<Guid, ChannelWriter<Message>>.Empty;
            _listeners = listeners.ToImmutable();
            _unicastClients = unicastClients.ToImmutable();
            _multicastClients = multicastClients.ToImmutable();
            _listenerTasks = new List<Task>(_listeners.Count);
            // now we have our listening sockets, hook up background threads
            // that listen for messages from each one
            foreach (var listener in _listeners.Values)
            {
                _listenerTasks.Add(
                    Task.Run(
                        () => ListenAsync(listener, _cancellationTokenSource.Token), _cancellationTokenSource.Token
                    )
                );
            }
            return default;
        }

        public async ValueTask DisposeAsync()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource = null;
            }

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

        private async Task ListenAsync(SocketListener listener, CancellationToken cancellationToken)
        {
            await foreach (var receiveResult in listener.ReceiveAsync(cancellationToken))
            {
                var request = receiveResult.Message;
                if (!request.IsQuery)
                {
                    // if we have response handlers then invoke them!
                    foreach (var responseHandler in _responseHandlers!.Values)
                    {
                        await responseHandler.WriteAsync(request, cancellationToken);
                    }
                    continue;
                }

                // normalize unicast responses
                // mDNS uses an additional bit to signify that a unicast response
                // is required for a message. This checks for that bit and adjusts
                // the query so that it represents the correct data.
                // see https://github.com/richardschneider/net-mdns/blob/master/src/ServiceDiscovery.cs#L382-L392
                var useUnicast = false;
                foreach (var q in request.Questions)
                {
                    if (((ushort) q.Class & 0x8000) != 0)
                    {
                        useUnicast = true;
                        q.Class = (DnsClass) ((ushort) q.Class & 0x7fff);
                    }
                }

                var response = await _nameServer.ResolveAsync(request, cancellationToken);
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
        
        private async ValueTask SendMulticastAsync(Message message)
        {
            if (_multicastClients != null)
            {
                foreach (var multicastClient in _multicastClients.Values)
                {
                    await multicastClient.SendAsync(message);
                }
            }
        }
        
        private static IEnumerable<IPAddress> GetNetworkInterfaceLocalAddresses(NetworkInterface networkInterface)
        {
            return networkInterface
                    .GetIPProperties()
                    .UnicastAddresses
                    .Select(x => x.Address)
                    .Where(ip => !IPAddress.IsLoopback(ip))
                    .Where(ip => ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.IsIPv6LinkLocal)
                ;
        }
        
        private static SocketListener CreateListener(IPAddress ipAddress)
        {
            IPEndPoint localEndpoint;
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
            IPEndPoint localEndpoint;
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
            IPEndPoint remoteEndpoint;
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
                var socket = Socket;
                
                // ReceiveFromAsync does not support cancellation directly
                // so register to close the socket when cancellation occurs
                cancellationToken.Register(
                    () => socket.Close()
                );
                
                // allocate a small buffer for our packets
                var buffer = GC.AllocateArray<byte>(Message.MaxLength, true);
                var bufferMemory = buffer.AsMemory();
                while (!cancellationToken.IsCancellationRequested)
                {
                    // continually listen for messages from the socket
                    // decode them and queue them into the receiving channel
                    UdpSocketExtensions.SocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveFromAsync(Endpoint, bufferMemory);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        // socket was closed
                        // probably by the cancellation token being cancelled
                        // try to continue the loop so we exit gracefully
                        continue;
                    }

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