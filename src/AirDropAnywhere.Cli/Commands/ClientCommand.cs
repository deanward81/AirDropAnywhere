using System;
using System.Net.Http;
using System.Net.Security;
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
        public ClientCommand(IAnsiConsole console, ILogger<ClientCommand> logger) : base(console, logger)
        {
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            Console.MarkupLine(Program.ApplicationName);

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
            return new(
                Console.Prompt(
                    new ConfirmationPrompt($"Incoming files from [bold]{request.SenderComputerName}[/]. Accept?")
                )
            );
        }
        
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<server>")]
            public string Server { get; set; } = null!;
            
            [CommandArgument(0, "<port>")]
            public ushort Port { get; set; } = default!;
        }
    }
}