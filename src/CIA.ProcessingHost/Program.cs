using CIA.ProcessingHost.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost;

public sealed class Program
{
    private Program()
    {
    }

    public static async Task Main(string[] args)
    {
        using var host = ProcessingHostApplicationHost.Create(args);

        await host.StartAsync().ConfigureAwait(false);

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation(
            "CIA process started with role {ProcessRole}",
            ProcessingHostApplicationHost.ProcessRole);
        logger.LogInformation(
            "CIA process stopping with role {ProcessRole}",
            ProcessingHostApplicationHost.ProcessRole);

        await host.StopAsync().ConfigureAwait(false);
    }
}
