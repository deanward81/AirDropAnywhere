using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AirDropAnywhere.Core.HttpTransport
{
    /// <summary>
    /// Wraps a <see cref="SocketConnectionFactory" /> and ensures we
    /// listen on AWDL interfaces on MacOS. 
    /// </summary>
    internal class AwdlSocketTransportFactory : IConnectionListenerFactory
    {
        private readonly IConnectionListenerFactory _connectionListenerFactory;
        private readonly FieldInfo _listenSocketField;
        
        public AwdlSocketTransportFactory(IOptions<SocketTransportOptions> options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            // HACK: this merry little reflective dance is because of sealed internal classes
            // and no extensibility points, yay :/
            var socketConnectionListenerType = typeof(SocketTransportFactory).Assembly.GetType("Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionListener");
            if (socketConnectionListenerType == null)
            {
                throw new InvalidOperationException("Unable to find SocketConnectionListener type");
            }
            
            var listenSocketField = socketConnectionListenerType.GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic);
            if (listenSocketField == null)
            {
                throw new InvalidOperationException("Unable to find _listenSocket field in SocketConnectionListener");
            }

            _connectionListenerFactory = new SocketTransportFactory(options, loggerFactory);
            _listenSocketField = listenSocketField;
        }

        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            var transport = await _connectionListenerFactory.BindAsync(endpoint, cancellationToken);
            // HACK: fix up the listen socket to support listening on AWDL
            var listenSocket = (Socket?) _listenSocketField.GetValue(transport);
            if (listenSocket != null)
            {
                listenSocket.SetAwdlSocketOption();
            }

            return transport;
        }
    }
}