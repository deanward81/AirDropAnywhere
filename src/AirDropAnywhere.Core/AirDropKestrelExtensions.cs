using System;
using System.Security.Cryptography.X509Certificates;
using AirDropAnywhere.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Hosting
{
    /// <summary>
    /// Extensions used to configure AirDrop defaults in Kestrel.
    /// </summary>
    public static class AirDropKestrelExtensions
    {
        /// <summary>
        /// Configures AirDrop defaults for Kestrel.
        /// </summary>
        /// <param name="options">A <see cref="KestrelServerOptions"/> instance to configure.</param>
        /// <param name="cert">An <see cref="X509Certificate2"/> representing the certificate to use for the AirDrop HTTPS endpoint.</param>
        public static void ConfigureAirDropDefaults(this KestrelServerOptions options, X509Certificate2 cert)
        {
            if (cert == null)
            {
                throw new ArgumentNullException(nameof(cert));
            }
            
            var airDropOptions = options.ApplicationServices.GetRequiredService<IOptions<AirDropOptions>>();
            options.ConfigureEndpointDefaults(
                endpointDefaults =>
                {
                    endpointDefaults.UseHttps(cert);
                });

            options.ListenAnyIP(airDropOptions.Value.ListenPort);
        }
    }
}