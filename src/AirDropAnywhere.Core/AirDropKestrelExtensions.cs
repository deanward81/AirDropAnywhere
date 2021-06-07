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
        public static void ConfigureAirDropDefaults(this KestrelServerOptions options)
        {
            var airDropOptions = options.ApplicationServices.GetRequiredService<IOptions<AirDropOptions>>();
            options.ConfigureEndpointDefaults(
                endpointDefaults =>
                {
                    // TODO: use a self-generated certificate
                    endpointDefaults.UseHttps();
                });

            options.ListenAnyIP(airDropOptions.Value.ListenPort);
        }
    }
}