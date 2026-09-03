using CIA.Contracts.Operations;

namespace CIA.Desktop.Hosting;

public interface IProcessingHostSupervisor
{
    ProcessingHostLifecycleSnapshot Current { get; }

    event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged;

    Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
        CancellationToken cancellationToken = default);

    Task<bool> RequestOperationCancellationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum ProcessingHostLifecycleState
{
    Stopped,
    Starting,
    Ready,
    Recreating,
    Stopping,
    Faulted
}

public sealed record ProcessingHostLifecycleSnapshot(
    ProcessingHostLifecycleState State,
    bool HostDesired,
    int? ProcessId,
    string? FailureCode);
