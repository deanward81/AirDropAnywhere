using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Hubs;
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
            var uri = new UriBuilder(
                Uri.UriSchemeHttps, settings.Server, settings.Port, "/airdrop"
            ).Uri;

            await using var hubConnection = CreateHubConnection(uri);
            using var cancellationTokenSource = CreateCancellationTokenSource();

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
                clientChannel.Reader.ReadAllAsync(cancellationTokenSource.Token)
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

        public class Settings : CommandSettings
        {
            [CommandOption("--server")]
            public string Server { get; init; } = null!;
            
            [CommandOption("--port")]
            public ushort Port { get; init; } = default!;

            [CommandOption("--path")]
            public string Path { get; init; } = null!;

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(Server))
                {
                    return ValidationResult.Error("Invalid server specified.");
                }
                
                if (Port == 0)
                {
                    return ValidationResult.Error("Invalid port specified.");
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