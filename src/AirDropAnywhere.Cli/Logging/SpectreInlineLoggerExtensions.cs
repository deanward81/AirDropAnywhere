using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AirDropAnywhere.Cli.Logging
{
    internal static class SpectreInlineLoggerExtensions
    {
        public static ILoggingBuilder AddSpectreConsole(this ILoggingBuilder builder) =>
            builder.AddProvider(new SpectreInlineLoggerProvider(AnsiConsole.Console));
    }
}