using System;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AirDropAnywhere.Cli.Commands
{
    public abstract class CommandBase : AsyncCommand
    {
        protected IAnsiConsole Console { get; }
        protected ILogger Logger { get; }

        protected CommandBase(IAnsiConsole console, ILogger logger)
        {
            Console = console ?? throw new ArgumentNullException(nameof(console));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
    }
}