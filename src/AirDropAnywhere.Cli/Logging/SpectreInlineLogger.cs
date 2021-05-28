using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AirDropAnywhere.Cli.Logging
{
    internal class SpectreInlineLogger : ILogger
    {
        private readonly string _name;
        private readonly IAnsiConsole _console;

        public SpectreInlineLogger(string name, IAnsiConsole console)
        {
            _name = name;
            _console = console;
        }

        public IDisposable BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var stringBuilder = new StringBuilder(80);
            stringBuilder.Append(GetLevelMarkup(logLevel));
            stringBuilder.AppendFormat("[dim grey]{0}[/] ", _name);
            stringBuilder.Append(Markup.Escape(formatter(state, exception)));
            _console.MarkupLine(stringBuilder.ToString());
        }

        private static string GetLevelMarkup(LogLevel level)
        {
            return level switch
            {
                LogLevel.Critical => "[bold underline white on red]|CRIT|:[/] ",
                LogLevel.Error => "[bold red]|ERROR|:[/] ",
                LogLevel.Warning => "[bold orange3]| WARN|:[/] ",
                LogLevel.Information => "[bold dim]| INFO|:[/] ",
                LogLevel.Debug => "[dim]|DEBUG|:[/] ",
                LogLevel.Trace => "[dim grey]|TRACE|:[/] ",
                _ => "|UNKWN|: "
            };
        }
    }
}