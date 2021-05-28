using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using AirDropAnywhere.Core.Compression;
using AirDropAnywhere.Core.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirDropAnywhere.Core
{
    public class AirDropRouteHandler
    {
        private readonly ILogger<AirDropRouteHandler> _logger;
        private readonly AirDropPeer _peer;
        private readonly HttpContext _ctx;
        
        public AirDropRouteHandler(
            HttpContext ctx,
            AirDropPeer peer,
            ILogger<AirDropRouteHandler> logger
        )
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private HttpRequest Request => _ctx.Request;
        private HttpResponse Response => _ctx.Response;

        public static Task ExecuteAsync(HttpContext ctx, Func<AirDropRouteHandler, Task> executor)
        {
            // when an AirDrop Anywhere "client" connects to the proxy
            // it registers itself as a "channel". When handling registration
            // of a channel the underlying code assigns it a unique identifier
            // which is used as the host header when using the HTTP API. This unique
            // identifier is advertised as a SRV record via mDNS.
            //
            // Here we attempt to map the host header to its underlying channel
            // If none is found then we 404, otherwise we new up the route handler,
            // and pass the services and channel that the HTTP API should be using
            // to handle the to and fro of the AirDrop protocol 
            var service = ctx.RequestServices.GetRequiredService<AirDropService>();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<AirDropRouteHandler>>();
            var hostSpan = ctx.Request.Host.Host.AsSpan();
            var firstPartIndex = hostSpan.IndexOf('.');
            if (firstPartIndex == -1)
            {
                return NotFound();
            }

            var channelId = hostSpan[..firstPartIndex];
            if (!service.TryGetPeer(channelId.ToString(), out var channel))
            {
                return NotFound();
            }

            var handler = new AirDropRouteHandler(ctx, channel, logger);
            return executor(handler);

            Task NotFound()
            {
                // we couldn't map the host header to the underlying
                // channel, so there's nothing for us to handle: 404!
                logger.LogInformation(
                    "Unable to find a connected channel from host header '{Host}'", 
                    ctx.Request.Host.Host
                 );
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
        }

        public async Task DiscoverAsync()
        {
            // TODO: handle contacts
            // we're currently operating in "Everyone" receive
            // mode which means discover will always return something
            // if there are any channels associated with the proxy
            var discoverRequest = await Request.ReadFromPropertyListAsync<DiscoverRequest>();
            if (!discoverRequest.TryGetSenderRecordData(out var contactData))
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await Response.WriteAsPropertyListAsync(
                new DiscoverResponse(_peer.Name, _peer.Name, MediaCapabilities.Default)
            );
        }

        public async Task AskAsync()
        {
            var askRequest = await Request.ReadFromPropertyListAsync<AskRequest>();
            var canAcceptFiles = await _peer.CanAcceptFilesAsync(askRequest);
            if (!canAcceptFiles)
            {
                // 406 Not Acceptable if the underlying channel
                // did not accept the files
                Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }
            
            // underlying channel accepted the files, tell the caller of the API
            await Response.WriteAsPropertyListAsync(
                new AskResponse(_peer.Name, _peer.Name)
            );
        }
        
        public async Task UploadAsync()
        {
            if (Request.ContentType != "application/x-cpio")
            {
                // AirDrop also supports a format called "DvZip"
                // which appears to be completely undocumented
                // we explicitly _do not_ enable the flag that sends
                // this data format - so we're expecting a GZIP encoded 
                // CPIO file - if we don't have that then return a 422
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
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
                await using (var requestStream = new GZipStream(Request.Body, CompressionMode.Decompress, true))
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