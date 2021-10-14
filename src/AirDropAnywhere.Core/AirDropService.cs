using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        
        private MulticastDnsManager? _mDnsManager;
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
            
            // we support binding to all interfaces for mDNS
            // so that we can answer queries for our HTTP endpoints
            var networkInterfaces = GetNetworkInterfaces();

            _cancellationTokenSource = new CancellationTokenSource();
            _logger.LogInformation("Initializing mDNS...");
            _mDnsManager = await MulticastDnsManager.CreateAsync(networkInterfaces, _cancellationTokenSource.Token);
            _logger.LogInformation("Registering AirDrop HTTP service with mDNS...");
            await _mDnsManager.RegisterAsync(CreateHttpMulticastDnsService());
        }

        async Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource = null;
            }

            if (_mDnsManager != null)
            {
                _logger.LogInformation("Stopping mDNS...");
                await _mDnsManager.DisposeAsync();
                _mDnsManager = null;
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

            var service = CreatePeerMulticastDnsService(peer);
            
            // keep a record of the peer and its service
            _peersById.AddOrUpdate(
                peer.Id,
                static (_, value) => value, 
                static (_, _, newValue) => newValue,
                new PeerMetadata(peer, service)
            );
            
            // and broadcast its existence to the world
            return _mDnsManager!.RegisterAsync(service);
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

            return _mDnsManager!.UnregisterAsync(peerMetadata.Service);
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

        private MulticastDnsService CreatePeerMulticastDnsService(AirDropPeer peer) =>
            new MulticastDnsService.Builder()
                .SetNames("_airdrop._tcp", peer.Id, peer.Id)
                .AddEndpoints(GetAllEndpoints(GetAwdlInterfaces()))
                .AddProperty("flags", ((uint) AirDropReceiverFlags.Default).ToString())
                .Build();
        
        private MulticastDnsService CreateHttpMulticastDnsService() =>
            new MulticastDnsService.Builder()
                .SetNames("_airdrop_proxy._tcp", "airdrop", "airdrop")
                .AddEndpoints(GetAllEndpoints(GetNetworkInterfaces()))
                .Build();

        private IEnumerable<IPEndPoint> GetAllEndpoints(IEnumerable<NetworkInterface> interfaces) =>
            interfaces
                .Select(i => i.GetIPProperties())
                .SelectMany(p => p.UnicastAddresses)
                .Where(p => !IPAddress.IsLoopback(p.Address))
                .Select(ip => new IPEndPoint(ip.Address, _optionsMonitor.CurrentValue.ListenPort));

        private static ImmutableArray<NetworkInterface> GetAwdlInterfaces() =>
            GetNetworkInterfaces(i => i.IsAwdlInterface());
        
        private static ImmutableArray<NetworkInterface> GetNetworkInterfaces(Func<NetworkInterface, bool>? filter = null)
        {
            var networkInterfaces = MulticastDnsManager.GetMulticastInterfaces();
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