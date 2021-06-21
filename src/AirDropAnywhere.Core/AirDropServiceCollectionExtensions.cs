using System;
using System.Net;
using System.Runtime.InteropServices;
using AirDropAnywhere.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Hosting;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extensions used to add AirDrop services to <see cref="IServiceCollection" />.
    /// </summary>
    public static class AirDropServiceCollectionExtensions
    {
        /// <summary>
        /// Adds AirDrop services to the specified <see cref="IServiceCollection" />.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
        /// <param name="setupAction">An <see cref="Action{AirDropOptions}"/> to configure the provided <see cref="AirDropOptions"/>.</param>
        public static IServiceCollection AddAirDrop(this IServiceCollection services, Action<AirDropOptions>? setupAction = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            
            Utils.AssertPlatform();
            Utils.AssertNetworkInterfaces();
            
            services.AddScoped<AirDropRouteHandler>();
            services.AddSingleton<AirDropService>();
            services.AddSingleton<IHostedService>(s => s.GetService<AirDropService>()!);
            services.AddOptions<AirDropOptions>().ValidateDataAnnotations();

            services.Configure<SocketTransportOptions>(
                x =>
                {
                    // on macOS, ensure we listen on the awdl0 interface
                    // by setting the SO_RECV_ANYIF socket option
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        x.CreateBoundListenSocket = endpoint =>
                        {
                            var socket = SocketTransportOptions.CreateDefaultBoundListenSocket(endpoint);
                            if (endpoint is IPEndPoint)
                            {
                                socket.SetAwdlSocketOption();
                            }
                            return socket;
                        };
                    }
                });

            if (setupAction != null)
            {
                services.Configure(setupAction);
            }

            return services;
        }
    }
}