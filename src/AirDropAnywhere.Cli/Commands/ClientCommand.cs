using System;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Http;
using AirDropAnywhere.Cli.Hubs;
using AirDropAnywhere.Cli.Logging;
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
                if (endpoint == null)
                {
                    throw new ArgumentException("Could not discover an AirDrop endpoint to connect to!");
                }
                
                var hostBuilder = new StringBuilder(endpoint.Address.ToString());
                if (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    hostBuilder.Insert(0, '[').Append(']');
                }
                
                (settings.Server, settings.Port) = (hostBuilder.ToString(), (ushort)endpoint.Port);
            }
            
            var uri = new UriBuilder(
                Uri.UriSchemeHttps, settings.Server, settings.Port, "/airdrop"
            ).Uri;
            
            using var cancellationTokenSource = CreateCancellationTokenSource();
            await using var hubConnection = CreateHubConnection(uri);
            await Console.Status()
                .StartAsync(
                    $"Connecting to [bold]{settings.Server.EscapeMarkup()}:{settings.Port}[/]",
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

        private async ValueTask<IPEndPoint?> DiscoverAsync()
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
                            var endpoint = await mDnsManager
                                .DiscoverAsync("_airdrop_proxy._tcp", timedCancellation.Token)
                                .FirstOrDefaultAsync(timedCancellation.Token);

                            if (endpoint != null)
                            {
                                // perform a DNS lookup to locate the IP addresses for the host
                                return await ResolveAsync(endpoint, timedCancellation.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // cancellation token was cancelled, nothing to see here, move along
                        }

                        return default;
                    }
                );
        }

        private async ValueTask<IPEndPoint?> ResolveAsync(DnsEndPoint dnsEndPoint, CancellationToken cancellationToken)
        {
            const string logPrefix = nameof(ResolveAsync) + ": "; 
            
            // when .NET attempts to connect to a DNS endpoint
            // it appears to naively pick the first IPv4 or IPv6 record
            // it resolves to, irrespective of the reachability of the underlying
            // address. When resolving using mDNS, depending on the network interfaces
            // available to the system that registered the DNS entry, we can often
            // have multiple IPv4 or IPv6 addresses returned to the resolver. If
            // .NET picks one that is not reachable from the client system then
            // connectivity to the endpoint will fail.
            //
            // Here we resolve all the IP addresses for a host entry and attempt to connect
            // to each one until we are successful. That is the IP address we'll connect to
            //
            Logger.LogInformation(logPrefix + "Resolving {Host}", dnsEndPoint.Host);
            
            var ipAddresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken);
            
            Logger.LogInformation(
                logPrefix + "Found {Count} addresses for {Host}", 
                ipAddresses.Length, 
                dnsEndPoint.Host
            );
            
            foreach (var ipAddress in ipAddresses)
            {
                // _currently_ IPv6 link local addresses _do not_ work correctly
                // with .NET's Uri class - Uri strips the link local scope identifier
                // which means HttpClient and SignalR's web socket handling are unable
                // to route the connection to the right place. For now, attempt to
                // use an IPv4 address... This _really_ needs to be fixed up
                // perhaps using a ConnectCallback for the HTTP bits and passing
                // a NetworkStream directly to WebSocket.CreateFromStream for SignalR
                if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    Logger.LogInformation(logPrefix + "Ignoring non-IPv4 address '{Address}'...", ipAddress);   
                    continue;
                }

                Logger.LogInformation(logPrefix + "Checking connectivity to {Address}", ipAddress); 
                
                var ipEndPoint = new IPEndPoint(ipAddress, dnsEndPoint.Port);
                if (await TryConnectAsync(ipEndPoint, cancellationToken))
                {
                    Logger.LogInformation(
                        logPrefix + "Resolved {Host} to {Address}", 
                        dnsEndPoint.Host, 
                        ipAddress
                    );
                    return ipEndPoint;
                }
            }

            Logger.LogError("Unable to resolve {Server}", dnsEndPoint.Host);
            return default;
        }
        
        private async ValueTask<bool> TryConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
        {
            using var timedSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var aggregateSource = CancellationTokenSource.CreateLinkedTokenSource(timedSource.Token, cancellationToken);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endPoint, aggregateSource.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
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
                .ConfigureLogging(
                    builder =>
                    {
                        builder.ClearProviders();
                        builder.AddConfiguration(Configuration.Logging);
                        builder.AddSpectreConsole();
                    }
                )
                .WithUrl(uri, options =>
                {
                    options.HttpMessageHandlerFactory = _ => HttpHandlerFactory.Create();
                    options.WebSocketConfiguration = socketConfiguration =>
                    {
                        socketConfiguration.RemoteCertificateValidationCallback = HttpHandlerFactory.IgnoreRemoteCertificateValidator;
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