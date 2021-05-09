using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using AirDropAnywhere.Core.Compression;
using AirDropAnywhere.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirDropAnywhere.Core
{
    public class AirDropRouteHandler
    {
        private readonly ILogger<AirDropRouteHandler> _logger;
        
        public AirDropRouteHandler(ILogger<AirDropRouteHandler> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static Task ExecuteAsync(HttpContext ctx, Func<HttpContext, AirDropRouteHandler, Task> executor)
        {
            return executor(
                ctx, ctx.RequestServices.GetRequiredService<AirDropRouteHandler>()
            );
        }

        public async Task DiscoverAsync(HttpContext ctx)
        {
            // TODO: handle contacts
            var discoverRequest = await ctx.Request.ReadFromPropertyListAsync<DiscoverRequest>();
            if (!discoverRequest.TryGetSenderRecordData(out var contactData))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            
            await ctx.Response.WriteAsPropertyListAsync(
                new DiscoverResponse(ctx.Request.Host.Host, "Hello World", MediaCapabilities.Default)
            );
        }

        public async Task AskAsync(HttpContext ctx)
        {
            var askRequest = await ctx.Request.ReadFromPropertyListAsync<AskRequest>();
            // TODO: notify some UI about the request
            await ctx.Response.WriteAsPropertyListAsync(
                new AskResponse(ctx.Request.Host.Host, "Hello World")
            );
        }
        
        public async Task UploadAsync(HttpContext ctx)
        {
            if (ctx.Request.ContentType != "application/x-cpio")
            {
                // AirDrop also supports a format called "DvZip"
                // which appears to be completely undocumented
                // we explicitly _do not_ enable the flag that sends
                // this data format - so we're expecting a GZIP encoded 
                // CPIO file - if we don't have that then return a 422
                ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            }

            // extract the CPIO file directly to disk
            var extractionPath = Path.Join(Path.GetTempPath(), Utils.GetRandomString());
            if (!Directory.Exists(extractionPath))
            {
                Directory.CreateDirectory(extractionPath);
            }
            
            try
            {
                // NOTE: Apple doesn't pass the Content-Encoding header
                // here but they do encode the request using gzip - so decompress
                // using that prior to extracting the cpio archive to disk
                await using (var requestStream = new GZipStream(ctx.Request.Body, CompressionMode.Decompress, true))
                await using (var cpioArchiveReader = CpioArchiveReader.Create(requestStream))
                {
                    await cpioArchiveReader.ExtractAsync(extractionPath);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(extractionPath, true);
                }
                catch (Exception ex)
                {
                    // best effort to clean-up - if it fails
                    // there's little we can do here, so leave the orphaned file
                    _logger.LogWarning(ex, "Unable to delete extraction directory '{ExtractionPath}'", extractionPath);
                }
            }
        }
    }
}