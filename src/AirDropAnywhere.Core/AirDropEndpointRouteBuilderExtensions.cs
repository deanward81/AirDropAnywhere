using System;
using AirDropAnywhere.Core;
using Microsoft.AspNetCore.Routing;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder
{
    /// <summary>
    /// Provides extension methods for <see cref="IEndpointRouteBuilder"/> to add an AirDrop service.
    /// </summary>
    public static class AirDropEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Adds an AirDrop endpoint to the <see cref="IEndpointRouteBuilder"/>.
        /// </summary>
        /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/> to add the AirDrop endpoint to.</param>
        /// <returns>A convention routes for the AirDrop endpoint.</returns>
        public static IEndpointRouteBuilder MapAirDrop(
           this IEndpointRouteBuilder endpoints
        )
        {
            if (endpoints == null)
            {
                throw new ArgumentNullException(nameof(endpoints));
            }

            return MapAirDropCore(endpoints, null);
        }

        /// <summary>
        /// Adds an AirDrop endpoint to the <see cref="IEndpointRouteBuilder"/> with the specified options.
        /// </summary>
        /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/> to add the AirDrop endpoints to.</param>
        /// <param name="options">A <see cref="AirDropOptions"/> used to configure AirDrop.</param>
        /// <returns>A convention routes for the AirDrop endpoints.</returns>
        public static IEndpointRouteBuilder MapAirDrop(
           this IEndpointRouteBuilder endpoints,
           AirDropOptions options
        )
        {
            if (endpoints == null)
            {
                throw new ArgumentNullException(nameof(endpoints));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return MapAirDropCore(endpoints, options);
        }

        private static IEndpointRouteBuilder MapAirDropCore(IEndpointRouteBuilder endpoints, AirDropOptions? options)
        {
            Utils.AssertPlatform();
            Utils.AssertNetworkInterfaces();
            
            if (endpoints.ServiceProvider.GetService(typeof(AirDropService)) == null)
            {
                throw new InvalidOperationException(
                    "Unable to find services: make sure to call AddAirDrop in your ConfigureServices(...) method!"
                );
            }
            
            endpoints.MapPost("Discover", ctx => AirDropRouteHandler.ExecuteAsync(ctx, r => r.DiscoverAsync()));
            endpoints.MapPost("Ask", ctx => AirDropRouteHandler.ExecuteAsync(ctx, r => r.AskAsync()));
            endpoints.MapPost("Upload", ctx => AirDropRouteHandler.ExecuteAsync(ctx, r => r.UploadAsync()));
            return endpoints;
        }
    }
    
    
}
