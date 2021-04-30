using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = _optionsMonitor.CurrentValue;
            var serviceId = options.ServiceId;
            if (string.IsNullOrWhiteSpace(serviceId))
            {
                // generate a random identifier for this instance
                serviceId = GenerateRandomServiceId();
            }

            // on MacOS, make sure AWDL is spun up by the OS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("Starting AWDL...");
                Interop.StartAWDLBrowsing();
            }
            
            // we only support binding to the AWDL interface for mDNS
            // this is asserted when this class is instantiated
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.SupportsMulticast)
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Ppp)
                .ToArray();

            var awdlInterfaces = networkInterfaces
                .Where(i => i.IsAwdlInterface())
                .ToImmutableArray();

            _cancellationTokenSource = new CancellationTokenSource();
            _mDnsServer = new MulticastDnsServer(awdlInterfaces, _cancellationTokenSource.Token);

            _logger.LogInformation("Starting mDNS listener...");
            await _mDnsServer.StartAsync();
            
            _logger.LogInformation("Registering AirDrop service '{0}'...", serviceId);
            await _mDnsServer.RegisterAsync(
                new MulticastDnsService.Builder()
                    .SetNames("_airdrop._tcp", serviceId, Guid.NewGuid().ToString("D"))
                    .AddEndpoints(
                        networkInterfaces
                            .Select(i => i.GetIPProperties())
                            .SelectMany(p => p.UnicastAddresses)
                            .Where(p => !IPAddress.IsLoopback(p.Address))
                            .Select(ip => new IPEndPoint(ip.Address, options.ListenPort))
                    )
                    .AddProperty("flags", ((uint) AirDropReceiverFlags.Default).ToString())
                    .Build()
            );
        }

        public async Task StopAsync(CancellationToken cancellationToken)
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
            
            // on MacOS, make sure AWDL is stopped by the OS
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogInformation("Stopping AWDL...");
                Interop.StopAWDLBrowsing();
            }
        }
        
        private static string GenerateRandomServiceId()
        {
            const string charset = "abcdefghijklmnopqrstuvwxyz0123456789";
            Span<byte> bytes = stackalloc byte[12];
            Span<char> chars = stackalloc char[12];
            using (var crypto = new RNGCryptoServiceProvider())
            {
                crypto.GetNonZeroBytes(bytes);
            }

            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = charset[bytes[i] % (charset.Length)];
            }

            return new string(chars);
        }
    }
}