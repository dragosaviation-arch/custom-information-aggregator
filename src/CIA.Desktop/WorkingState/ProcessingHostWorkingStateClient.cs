using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.WorkingState;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.WorkingState;

public sealed class ProcessingHostWorkingStateClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostWorkingStateClient> logger) : IWorkingStateClient
{
    public Task<WorkingStateClientResult> SaveAsync(
        OperationCorrelation correlation,
        string targetPath,
        WorkingStateSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            correlation,
            token => requestClient.RequestWorkingStateSaveAsync(
                correlation,
                targetPath,
                snapshot,
                token),
            cancellationToken);

    public Task<WorkingStateClientResult> RestoreAsync(
        OperationCorrelation correlation,
        string packagePath,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            correlation,
            token => requestClient.RequestWorkingStateRestoreAsync(
                correlation,
                packagePath,
                token),
            cancellationToken);

    private async Task<WorkingStateClientResult> ExecuteAsync<TResponse>(
        OperationCorrelation correlation,
        Func<CancellationToken, Task<TResponse>> request,
        CancellationToken cancellationToken)
        where TResponse : IpcResponse
    {
        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, "processing-host-unavailable");
            }

            var response = await request(cancellationToken).ConfigureAwait(false);
            return response switch
            {
                SaveWorkingStateResponse saved => new WorkingStateClientResult(
                    saved.Acceptance == CommandAcceptance.Accepted,
                    saved.Completion,
                    saved.Manifest,
                    saved.Failure?.Code,
                    saved.Failure?.Description),
                RestoreWorkingStateResponse restored => new WorkingStateClientResult(
                    restored.Acceptance == CommandAcceptance.Accepted,
                    restored.Completion,
                    restored.Manifest,
                    restored.Failure?.Code,
                    restored.Failure?.Description),
                _ => throw new InvalidOperationException("The working-state response type is unsupported.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Working-state operation {OperationId} could not be completed by the Processing Host",
                correlation.OperationId);
            return Reject(correlation, "processing-host-unavailable");
        }
    }

    private static WorkingStateClientResult Reject(
        OperationCorrelation correlation,
        string failureCode) =>
        new(
            false,
            OperationCompletion.FromTerminalOutcome(
                correlation,
                OperationOutcome.Failed,
                []),
            Manifest: null,
            failureCode,
            "The Processing Host could not complete the working-state operation.");
}
