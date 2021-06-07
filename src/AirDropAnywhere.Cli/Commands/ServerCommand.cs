using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Hubs;
using AirDropAnywhere.Cli.Logging;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli.Commands
{
    internal class ServerCommand : CommandBase<ServerCommand.Settings>
    {
        public ServerCommand(IAnsiConsole console, ILogger<ServerCommand> logger) : base(console, logger)
        {
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            var webHost = default(IWebHost);
            using var cancellationTokenSource = new CancellationTokenSource();
            
            await Console.Status()
                .Spinner(Spinner.Known.Earth)
                .StartAsync(
                    "Starting AirDrop services...",
                    async _ =>
                    {
                        webHost = WebHost.CreateDefaultBuilder()
                            .ConfigureLogging(
                                (hostContext, builder) =>
                                {
                                    builder.ClearProviders();
                                    builder.AddConfiguration(hostContext.Configuration.GetSection("Logging"));
                                    builder.AddProvider(new SpectreInlineLoggerProvider(Console));
                                }
                            )
                            .ConfigureAirDrop(
                                options =>
                                {
                                    options.ListenPort = settings.Port;
                                }
                            )
                            .ConfigureServices(
                                services =>
                                {
                                    services.Configure<StaticFileOptions>(options =>
                                        options.FileProvider = new ManifestEmbeddedFileProvider(
                                            typeof(ServerCommand).Assembly, "wwwroot"
                                        )
                                    );
                                    services.AddRouting();
                                    services
                                        .AddSignalR(
                                            options =>
                                            {
                                                options.EnableDetailedErrors = true;
                                            }
                                        )
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
                                        );
                                }
                            )
                            .Configure(
                                app =>
                                {
                                    app
                                        .UseRouting()
                                        .UseStaticFiles()
                                        .UseEndpoints(
                                            endpoints =>
                                            {
                                                endpoints.MapAirDrop();
                                                endpoints.MapHub<AirDropHub>("/airdrop");
                                                endpoints.MapFallbackToFile("index.html");
                                            }
                                        );

                                }
                            )
                            .SuppressStatusMessages(true)
                            .Build();

                        await webHost.StartAsync(cancellationTokenSource.Token);
                    }
                );

            var serverAddresses = webHost!.ServerFeatures.Get<IServerAddressesFeature>().Addresses;
            foreach (var serverAddress in serverAddresses)
            {
                Logger.LogInformation("Listening on {Url}", serverAddress);
            }

            Logger.LogInformation("Waiting for AirDrop clients...");
            
            // ReSharper disable AccessToDisposedClosure
            void Shutdown()
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    Logger.LogInformation("Shutting down AirDrop services...");
                    cancellationTokenSource.Cancel();
                }
            }
            
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            System.Console.CancelKeyPress += (_, _) => Shutdown();
            await webHost.WaitForShutdownAsync(cancellationTokenSource.Token);
            return 0;
        }
        
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<port>")]
            public ushort Port { get; set; }
        }
    }
}