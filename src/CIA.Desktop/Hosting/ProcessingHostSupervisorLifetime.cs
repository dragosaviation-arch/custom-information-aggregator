using Microsoft.Extensions.Hosting;

namespace CIA.Desktop.Hosting;

internal sealed class ProcessingHostSupervisorLifetime(
    IProcessingHostSupervisor supervisor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return supervisor.StopAsync(cancellationToken);
    }
}
