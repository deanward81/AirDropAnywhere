using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AirDropAnywhere.Core.MulticastDns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AirDropAnywhere.Core
{
    /// <summary>
    /// Hosted service that advertises AirDrop services via mDNS.
    /// </summary>
    public class AirDropService : IHostedService
    {
        private readonly IOptionsMonitor<AirDropOptions> _optionsMonitor;
        private readonly ILogger<AirDropService> _logger;
        private readonly ConcurrentDictionary<string, PeerMetadata> _peersById = new();
        
        private MulticastDnsServer? _mDnsServer;
        private CancellationTokenSource? _cancellationTokenSource;
        
        public AirDropService(
            IOptionsMonitor<AirDropOptions> optionsMonitor,
            ILogger<AirDropService> logger
        )
        {
            Utils.AssertPlatform();
            Utils.AssertNetworkInterfaces();
            
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        async Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            // on macOS, make sure AWDL is spun up by the OS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("Starting AWDL...");
                Interop.StartAWDLBrowsing();
            }
            
            // we only support binding to the AWDL interface for mDNS
            // and existence of this interface is asserted when
            // this class is instantiated - GetNetworkInterfaces will
            // only return interfaces that support an implementation of AWDL.
            var networkInterfaces = GetNetworkInterfaces(i => i.IsAwdlInterface());

            _cancellationTokenSource = new CancellationTokenSource();
            _mDnsServer = new MulticastDnsServer(networkInterfaces, _cancellationTokenSource.Token);

            _logger.LogInformation("Starting mDNS listener...");
            await _mDnsServer.StartAsync();
        }

        async Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource = null;
            }

            if (_mDnsServer != null)
            {
                _logger.LogInformation("Stopping mDNS listener...");
                await _mDnsServer.StopAsync();
                _mDnsServer = null;
            }

            // on macOS, make sure AWDL is stopped by the OS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("Stopping AWDL...");
                Interop.StopAWDLBrowsing();
            }
        }

        /// <summary>
        /// Registers an <see cref="AirDropPeer"/> so that it becomes discoverable to
        /// AirDrop-compatible devices.
        /// </summary>
        /// <param name="peer">
        /// An instance of <see cref="AirDropPeer"/>.
        /// </param>
        public ValueTask RegisterPeerAsync(AirDropPeer peer)
        {
            _logger.LogInformation("Registering AirDrop peer '{Id}'...", peer.Id);
            
            var service = new MulticastDnsService.Builder()
                .SetNames("_airdrop._tcp", peer.Id, peer.Id)
                .AddEndpoints(
                    GetNetworkInterfaces()
                        .Select(i => i.GetIPProperties())
                        .SelectMany(p => p.UnicastAddresses)
                        .Where(p => !IPAddress.IsLoopback(p.Address))
                        .Select(ip => new IPEndPoint(ip.Address, _optionsMonitor.CurrentValue.ListenPort))
                )
                .AddProperty("flags", ((uint) AirDropReceiverFlags.Default).ToString())
                .Build();

            // keep a record of the peer and its service
            _peersById.AddOrUpdate(
                peer.Id,
                (_, value) => value,
                (_, _, newValue) => newValue,
                new PeerMetadata(peer, service)
            );
            
            // and broadcast its existence to the world
            return _mDnsServer!.RegisterAsync(service);
        }

        /// <summary>
        /// Unregisters an <see cref="AirDropPeer"/> so that it is no longer discoverable by
        /// AirDrop-compatible devices. If the peer is not registered then this operation is no-op.
        /// </summary>
        /// <param name="peer">
        /// A previously registered instance of <see cref="AirDropPeer"/>.
        /// </param>
        public ValueTask UnregisterPeerAsync(AirDropPeer peer)
        {
            _logger.LogInformation("Unregistering AirDrop peer '{Id}'...", peer.Id);
            if (!_peersById.TryRemove(peer.Id, out var peerMetadata))
            {
                return default;
            }

            return _mDnsServer!.UnregisterAsync(peerMetadata.Service);
        }

        /// <summary>
        /// Attempts to get an <see cref="AirDropPeer"/> by its unique identifier.
        /// </summary>
        /// <param name="id">Unique identifier of a peer.</param>
        /// <param name="peer">
        /// If found, the instance of <see cref="AirDropPeer"/> identified by <paramref name="id"/>,
        /// <c>null</c> otherwise.
        /// </param>
        /// <returns>
        /// <c>true</c> if the peer was found, <c>false</c> otherwise.
        /// </returns>
        public bool TryGetPeer(string id, [MaybeNullWhen(false)] out AirDropPeer peer)
        {
            if (!_peersById.TryGetValue(id, out var peerMetadata))
            {
                peer = default;
                return false;
            }

            peer = peerMetadata.Peer;
            return true;
        }
        
        private static ImmutableArray<NetworkInterface> GetNetworkInterfaces(Func<NetworkInterface, bool>? filter = null)
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.SupportsMulticast)
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Ppp)
                .Where(i => i.IsAwdlInterface());

            if (filter != null)
            {
                networkInterfaces = networkInterfaces.Where(filter);
            }
            return networkInterfaces.ToImmutableArray();
        }

        private readonly struct PeerMetadata
        {
            public AirDropPeer Peer { get; }
            public MulticastDnsService Service { get; }

            public PeerMetadata(AirDropPeer peer, MulticastDnsService service)
            {
                Peer = peer;
                Service = service;
            }
        }
    }
}