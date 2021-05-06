using System;
using AirDropAnywhere.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Hosting
{
    /// <summary>
    /// Extensions used to configure AirDrop services in an <see cref="IApplicationBuilder"/>.
    /// </summary>
    public static class AirDropWebHostBuilderExtensions
    {
        /// <summary>
        /// Configures AirDrop services and endpoints in the specified <see cref="IWebHostBuilder"/>.
        /// </summary>
        /// <param name="builder">An <see cref="IWebHostBuilder"/> to add services/endpoints to.</param>
        /// <param name="setupAction">An <see cref="Action{AirDropOptions}"/> to configure the provided <see cref="AirDropOptions"/>.</param>
        public static IWebHostBuilder ConfigureAirDrop(this IWebHostBuilder builder, Action<AirDropOptions>? setupAction = null)
        {
            Utils.AssertPlatform();
            Utils.AssertNetworkInterfaces();
            
            return builder
                .ConfigureKestrel(
                    options =>
                    {
                        var airDropOptions = options.ApplicationServices.GetRequiredService<IOptions<AirDropOptions>>();
                        options.ConfigureEndpointDefaults(
                            options =>
                            {
                                // TODO: use a self-generated certificate
                                options.UseHttps();
                            });
                        
                        options.ListenAnyIP(airDropOptions.Value.ListenPort);
                    }
                )
                .ConfigureServices(
                    services =>
                    {
                        services.AddRouting();
                        services.AddAirDrop(setupAction);
                    })
                .Configure(
                    app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(
                            endpoints =>
                            {
                                endpoints.MapAirDrop();
                            }
                        );
                    }
                );
        }
    }
}