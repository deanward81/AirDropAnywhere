using System;
using System.Threading.Tasks;
using AirDropAnywhere.Cli.Commands;
using AirDropAnywhere.Cli.Logging;
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
            var services = new ServiceCollection()
                .AddSingleton(AnsiConsole.Console)
                .AddHttpClient()
                .AddLogging(
                    builder =>
                    {
                        builder.ClearProviders();
                        builder.AddProvider(new SpectreInlineLoggerProvider(AnsiConsole.Console));
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
