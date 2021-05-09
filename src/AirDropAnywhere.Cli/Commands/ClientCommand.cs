using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli.Commands
{
    internal class ClientCommand : CommandBase
    {
        public ClientCommand(IAnsiConsole console, ILogger<ClientCommand> logger) : base(console, logger)
        {
        }

        public override async Task<int> ExecuteAsync(CommandContext context)
        {
            Console.MarkupLine(Program.ApplicationName);
            
            await Console.Status()
                .StartAsync(
                    "Connecting to [bold]localhost[/]",
                    async ctx =>
                    {
                        for (var i = 0; i < 10; i++)
                        {
                            await Task.Delay(500);
                        }
                    }
                );

            return 0;
        }
    }
}