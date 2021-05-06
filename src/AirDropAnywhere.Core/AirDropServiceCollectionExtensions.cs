using System;
using System.Runtime.InteropServices;
using AirDropAnywhere.Core;
using AirDropAnywhere.Core.HttpTransport;
using Microsoft.AspNetCore.Connections;
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                services.AddSingleton<IConnectionListenerFactory, AwdlSocketTransportFactory>();
            }

            if (setupAction != null)
            {
                services.Configure(setupAction);
            }

            return services;
        }
    }
}