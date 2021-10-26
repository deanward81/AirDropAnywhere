using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirDropAnywhere.Cli.Http
{
    /// <summary>
    /// Helpers used to configure an <see cref="HttpClient"/> used for AirDrop.
    /// </summary>
    internal static class HttpHandlerFactory
    {
        /// <summary>
        /// Creates an <see cref="SocketsHttpHandler"/> with default settings
        /// for connecting to AirDrop SignalR and HTTP endpoints.
        /// </summary>
        /// <returns>
        /// An instance of <see cref="SocketsHttpHandler"/>.
        /// </returns>
        public static SocketsHttpHandler Create() => new()
        {
            ConnectTimeout = _defaultConnectTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                // ignore TLS certificate errors - we're deliberately
                // using self-signed certificates so no need to worry
                // about validating them here
                RemoteCertificateValidationCallback = IgnoreRemoteCertificateValidator
            }
        };
        
        /// <summary>
        /// Default timeout used when connecting to an AirDrop HTTP endpoint.
        /// </summary>
        private static readonly TimeSpan _defaultConnectTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Implementation of <see cref="RemoteCertificateValidationCallback"/> that
        /// ignores the validity of a remote certificate. 
        /// </summary>
        // TODO: update to validate the certificate based upon a previously user-validated setting
        public static readonly RemoteCertificateValidationCallback IgnoreRemoteCertificateValidator =
            delegate { return true; };


        private static readonly ConcurrentDictionary<(string, int, AddressFamily), IPEndPoint> _hostMappings = new();
        
        /// <summary>
        /// Implementation of <see cref="SocketsHttpHandler.ConnectCallback"/> that attempts to connect
        /// to any of the IP addresses that a <see cref="DnsEndPoint"/> resolves to.
        /// </summary>
        private static readonly Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>
            _defaultConnectCallback = async (ctx, cancellationToken) =>
            {
                // when .NET attempts to connect to a DNS endpoint
                // it appears to naively pick the first IPv4 or IPv6 record
                // it resolves to, irrespective of the reachability of the underlying
                // address. When resolving using mDNS, depending on the network interfaces
                // available to the system that registered the DNS entry, we can often
                // have multiple IPv4 or IPv6 addresses returned to the resolver. If
                // .NET picks one that is not reachable to the client system then
                // connectivity to the endpoint will fail.
                //
                // This alternate connect callback attempts to connect to each resolved
                // IP address until it finds one that can be successfully connected to.
                var dnsEndPoint = ctx.DnsEndPoint;
                var mappingKey = (dnsEndPoint.Host, dnsEndPoint.Port, dnsEndPoint.AddressFamily);
                if (_hostMappings.TryGetValue(mappingKey, out var ipEndPoint))
                {
                    // we found an existing mapping between the host and an IP address, use it!
                    var (socket, _) = await ConnectAsync(ipEndPoint, cancellationToken);
                    if (socket != null)
                    {
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    
                    // otherwise something went wrong, try to lookup again and fail if necessary...
                }
                
                var ipAddresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host).ConfigureAwait(false);
                var lastError = default(Exception?);
                foreach (var ipAddress in ipAddresses)
                {
                    if (dnsEndPoint.AddressFamily != AddressFamily.Unspecified && ipAddress.AddressFamily != dnsEndPoint.AddressFamily)
                    {
                        // ignore any addresses that don't match what was required
                        // by the endpoint specified by the caller
                        continue;
                    }

                    // give each connect operation less time than the overall cancellation token
                    // so that we don't blow up prematurely
                    using var timedSource = new CancellationTokenSource(_defaultConnectTimeout / ipAddresses.Length);
                    using var aggregateSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timedSource.Token);

                    ipEndPoint = new IPEndPoint(ipAddress, dnsEndPoint.Port);
                    var (socket, error) = await ConnectAsync(ipEndPoint, aggregateSource.Token);
                    if (socket != null)
                    {
                        _hostMappings.AddOrUpdate(mappingKey, ipEndPoint, (_, _) => ipEndPoint);
                        return new NetworkStream(socket, ownsSocket: true);
                    }

                    lastError = error;
                }

                // we couldn't connect to any of the endpoints, give up
                throw lastError!;

                static async ValueTask<(Socket? Socket, Exception? Error)> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
                {
                    // Create and connect a socket using default settings.
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        return (null, ex);
                    }

                    return (socket, null);
                }
            };
    }
}