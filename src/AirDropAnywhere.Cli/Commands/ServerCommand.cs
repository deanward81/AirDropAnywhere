using System;
using System.Threading;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Logging;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli.Commands
{
    internal class ServerCommand : CommandBase
    {
        public ServerCommand(IAnsiConsole console, ILogger<ServerCommand> logger) : base(console, logger)
        {
        }
        
        public override async Task<int> ExecuteAsync(CommandContext context)
        {
            Console.MarkupLine(Program.ApplicationName);
            
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
                                    options.ListenPort = 34553;
                                }
                            )
                            .SuppressStatusMessages(true)
                            .Build();

                        await webHost.StartAsync(cancellationTokenSource.Token);
                    }
                );
            
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
    }
}