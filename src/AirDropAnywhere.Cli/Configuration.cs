using Microsoft.Extensions.Configuration;

namespace AirDropAnywhere.Cli
{
    internal static class Configuration
    {
        private static IConfiguration? _default;
        public static IConfiguration Default => _default ??= new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", true, true)
            .Build();

        public static IConfiguration Logging => Default.GetSection("Logging");
    }
}