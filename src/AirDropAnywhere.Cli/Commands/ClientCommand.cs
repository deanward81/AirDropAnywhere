using System;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Hubs;
using AirDropAnywhere.Core.MulticastDns;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli.Commands
{
    internal class ClientCommand : CommandBase<ClientCommand.Settings>
    {
        private readonly HttpClient _httpClient;

        public ClientCommand(IAnsiConsole console, ILogger<ClientCommand> logger, IHttpClientFactory httpClientFactory) : base(console, logger)
        {
            if (httpClientFactory == null)
            {
                throw new ArgumentNullException(nameof(httpClientFactory));
            }
            
            _httpClient = httpClientFactory.CreateClient("airdrop");
        }
        
        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            if (settings.UseDiscovery)
            {
                // use mDNS to locate the server / port we need to connect to
                var endpoint = await DiscoverAsync();
                if (endpoint != null)
                {
                    (settings.Server, settings.Port) = (endpoint.Host, (ushort)endpoint.Port);
                }
            }

            var uri = new UriBuilder(
                Uri.UriSchemeHttps, settings.Server, settings.Port, "/airdrop"
            ).Uri;

            using var cancellationTokenSource = CreateCancellationTokenSource();
            await using var hubConnection = CreateHubConnection(uri);
            await Console.Status()
                .StartAsync(
                    $"Connecting to [bold]{settings.Server}:{settings.Port}[/]",
                    _ => hubConnection.StartAsync(cancellationTokenSource.Token)
                );
                
            var clientChannel = Channel.CreateUnbounded<AirDropHubMessage>();

            // start the full duplex stream between the server and the client
            // we'll initially send our "connect" message to identify ourselves
            // and then wait for the server to push messages to us
            var serverMessages = hubConnection.StreamAsync<AirDropHubMessage>(
                nameof(AirDropHub.StreamAsync),
                clientChannel.Reader.ReadAllAsync(cancellationTokenSource.Token), 
                cancellationTokenSource.Token
            );

            Logger.LogInformation("Registering client...");
            await clientChannel.Writer.WriteAsync(
                await AirDropHubMessage.CreateAsync<ConnectMessage>(
                    m =>
                    {
                        m.Name = Environment.MachineName;
                        return default;
                    }
                ),
                cancellationTokenSource.Token
            );
            
            Logger.LogInformation("Waiting for peers...");
            await foreach (var message in serverMessages.WithCancellation(cancellationTokenSource.Token))
            {
                Logger.LogDebug("Received '{MessageType}' message...", message.GetType());
                switch (message)
                {
                    case CanAcceptFilesRequestMessage askRequest:
                        
                        await clientChannel.Writer.WriteAsync(
                            await AirDropHubMessage.CreateAsync(
                                async (CanAcceptFilesResponseMessage m, CanAcceptFilesRequestMessage r) =>
                                {
                                    m.Accepted = await OnCanAcceptFilesAsync(askRequest);
                                    m.ReplyTo = r.Id;
                                },
                                askRequest
                            ),
                            cancellationTokenSource.Token
                        );
                        break;
                    
                    case OnFileUploadedRequestMessage fileUploadedRequest:

                        await clientChannel.Writer.WriteAsync(
                            await AirDropHubMessage.CreateAsync(
                                async (OnFileUploadedResponseMessage m, OnFileUploadedRequestMessage r) =>
                                {
                                    m.ReplyTo = r.Id;
                                    
                                    // download the file to our download path
                                    await DownloadFileAsync(r, settings.Path);
                                },
                                fileUploadedRequest
                            ),
                            cancellationTokenSource.Token
                        );
                        break;
                    
                    default:
                        Logger.LogWarning("No handler for '{MessageType}' message", message.GetType());
                        break;
                }
            }

            // deliberately not passing cancellation token here
            // by the time we make it here the token is already cancelled
            // and we wouldn't be able to stop the connection gracefully!
            await hubConnection.StopAsync();
            return 0;
        }

        private async ValueTask<DnsEndPoint?> DiscoverAsync()
        {
            return await Console.Status()
                .StartAsync(
                    "Discovering AirDrop endpoints...",
                    async _ =>
                    {
                        using var timedCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var networkInterfaces = MulticastDnsManager.GetMulticastInterfaces().ToImmutableArray();
                        var mDnsManager = await MulticastDnsManager.CreateAsync(networkInterfaces, timedCancellation.Token);
                        try
                        {
                            await foreach (var endpoint in mDnsManager.DiscoverAsync("_airdrop_proxy._tcp", timedCancellation.Token))
                            {
                                // we only care about the first endpoint!
                                return endpoint;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // cancellation token was cancelled, nothing to see here, move along
                        }

                        return null;
                    }
                );
        }

        private CancellationTokenSource CreateCancellationTokenSource()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            
            void Disconnect()
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    Logger.LogInformation("Disconnecting...");
                    cancellationTokenSource.Cancel();
                }
            }
            
            System.Console.CancelKeyPress += (_, _) => Disconnect();
            return cancellationTokenSource;
        }
        
        private HubConnection CreateHubConnection(Uri uri)
        {
            return new HubConnectionBuilder()
                .AddJsonProtocol(
                    options =>
                    {
                        options.PayloadSerializerOptions = new JsonSerializerOptions
                        {
                            Converters =
                            {
                                PolymorphicJsonConverter.Create(typeof(AirDropHubMessage))
                            }
                        };
                    }
                )
                .WithUrl(uri, options =>
                {
                    // ignore TLS certificate errors - we're deliberately
                    // using self-signed certificates so no need to worry
                    // about validating them here
                    options.HttpMessageHandlerFactory = _ =>
                    {
                        return new SocketsHttpHandler
                        {
                            SslOptions = new SslClientAuthenticationOptions
                            {
                                RemoteCertificateValidationCallback = delegate { return true; }
                            }
                        };
                    };
                })
                .Build();
        }
        private ValueTask<bool> OnCanAcceptFilesAsync(CanAcceptFilesRequestMessage request)
        {
            var stringBuilder = new StringBuilder()
                .Append("Incoming files from [bold]")
                .Append(request.SenderComputerName)
                .AppendLine("[/]:");
            
            foreach (var file in request.Files)
            {
                stringBuilder.Append(" ‣ ").AppendLine(file.Name);
            }
            
            Console.MarkupLine(stringBuilder.ToString());
            
            return new(
                Console.Prompt(
                    new ConfirmationPrompt("Accept?")
                )
            );
        }
        
        private async ValueTask DownloadFileAsync(OnFileUploadedRequestMessage request, string basePath)
        {
            Logger.LogInformation("Downloading '{File}'...", request.Name);
            var downloadPath = Path.Join(basePath, request.Name);
            using var response = await _httpClient.GetAsync(request.Url);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError(
                    "Unable to download file '{File}': {ResponseCode}", 
                    request.Name, 
                    response.StatusCode
                );
                return;
            }
            
            await using var outputStream = File.Create(downloadPath);
            await response.Content.CopyToAsync(outputStream);
            Logger.LogInformation("Downloaded '{File}'...", request.Name);
        }

        // ReSharper disable once ClassNeverInstantiated.Global
        public class Settings : CommandSettings
        {
            [CommandOption("--server")]
            public string Server { get; internal set; } = null!;
            
            [CommandOption("--port")]
            public ushort Port { get; internal set; }

            [CommandOption("--path")]
            public string Path { get; init; } = null!;

            public bool UseDiscovery => string.IsNullOrEmpty(Server) && Port == 0;

            public override ValidationResult Validate()
            {
                var hasServer = !string.IsNullOrEmpty(Server);
                var hasPort = Port > 0;
                // server / port must be specified together
                if (hasServer && !hasPort)
                {
                    return ValidationResult.Error("Must specify a port if specifying a server.");
                }

                if (!hasServer && hasPort)
                {
                    return ValidationResult.Error("Must specify a server if specifying a port.");
                }
                
                if (string.IsNullOrEmpty(Path))
                {
                    return ValidationResult.Error("Invalid path specified.");
                }

                if (!Directory.Exists(Path))
                {
                    return ValidationResult.Error("Specified path does not exist.");
                }
                
                return base.Validate();
            }
        }
    }
}