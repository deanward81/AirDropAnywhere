using System;
using AirDropAnywhere.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
                                endpoints.MapGet("", ctx => ctx.Response.WriteAsync("Hello World"));
                            }
                        );
                    }
                );
        }
    }
}