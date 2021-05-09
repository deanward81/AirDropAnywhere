using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AirDropAnywhere.Cli.Logging
{
    internal class SpectreInlineLoggerProvider : ILoggerProvider
    {
        private readonly IAnsiConsole _console;
        private readonly ConcurrentDictionary<string, SpectreInlineLogger> _loggers = new();

        public SpectreInlineLoggerProvider(IAnsiConsole console)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new SpectreInlineLogger(name, _console));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }
    }
}