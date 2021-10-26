using System.Threading.Tasks;
using AirDropAnywhere.Cli.Certificates;
using AirDropAnywhere.Cli.Commands;
using AirDropAnywhere.Cli.Http;
using AirDropAnywhere.Cli.Logging;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Cli.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli
{
    internal static class Program
    {
        public const string ApplicationName = "🌐 AirDrop Anywhere";
        public static Task<int> Main(string[] args)
        {
            var services = new ServiceCollection();

            services
                .AddHttpClient("airdrop")
                .ConfigurePrimaryHttpMessageHandler(
                    HttpHandlerFactory.Create
                );
            
            services
                .AddSingleton(AnsiConsole.Console)
                .AddSingleton<CertificateManager>()
                .AddHttpLogging(
                    options =>
                    {
                        options.LoggingFields = HttpLoggingFields.All;
                    }
                )
                .AddLogging(
                    builder =>
                    {
                        builder.ClearProviders();
                        builder.AddConfiguration(Configuration.Logging);
                        builder.AddSpectreConsole();
                    }
                );

            var typeRegistrar = new DependencyInjectionRegistrar(services);
            var app = new CommandApp(typeRegistrar);
            app.Configure(
                c =>
                {
                    c.AddCommand<ServerCommand>("server");
                    c.AddCommand<ClientCommand>("client");
                }
            );
            
            AnsiConsole.WriteLine(ApplicationName);
            return app.RunAsync(args);
        }
    }
}
