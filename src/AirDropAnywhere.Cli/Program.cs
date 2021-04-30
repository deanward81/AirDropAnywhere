using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AirDropAnywhere.Cli
{
    class Program
    {
        static Task Main(string[] args)
        {
            var cliConfig = new ConfigurationBuilder()
                .AddCommandLine(args)
                .Build();
            
            var webHost = WebHost.CreateDefaultBuilder(args)
                .ConfigureLogging(
                    (ctx, builder) =>
                    {
                        builder.ClearProviders();
                        builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                        builder.AddConsole();
                    }
                )
                .ConfigureAirDrop(
                    options =>
                    {
                        cliConfig.Bind(options);
                        options.ListenPort = 34553;
                    }
                )
                .Build();

            return webHost.RunAsync();
        }
    }
}
