using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Sources;

public sealed class ProcessingHostSourceIntakeClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostSourceIntakeClient> logger) : ISourceIntakeClient
{
    public async Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default)
    {
        return await LoadAsync(
                selectionKind,
                path,
                settings,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        IProgress<SourceIntakeProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await hostSupervisor.EnsureAvailableAsync(cancellationToken);

            if (state.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(
                    "processing-host-unavailable",
                    "The Processing Host is not available to load sources.");
            }

            var response = await requestClient.RequestSourceLoadAsync(
                selectionKind,
                path,
                settings,
                progress,
                cancellationToken);
            return new SourceIntakeClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Sources,
                response.Failure?.Code,
                response.Failure?.Description)
            {
                Issues = response.Issues
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Source loading could not be completed by the Processing Host");
            return Reject(
                "processing-host-unavailable",
                "The Processing Host could not complete the source-loading request.");
        }
    }

    public async Task<SourceRefreshClientResult> RefreshAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            var state = await hostSupervisor.EnsureAvailableAsync(cancellationToken);

            if (state.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectRefresh(
                    source,
                    "processing-host-unavailable",
                    "The Processing Host is not available to refresh sources.");
            }

            var response = await requestClient.RequestSourceRefreshAsync(source, cancellationToken);
            return new SourceRefreshClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Source,
                response.Failure?.Code,
                response.Failure?.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Source refresh could not be completed by the Processing Host");
            return RejectRefresh(
                source,
                "processing-host-unavailable",
                "The Processing Host could not complete the source-refresh request.");
        }
    }

    private static SourceIntakeClientResult Reject(string code, string description)
    {
        return new SourceIntakeClientResult(
            false,
            Array.Empty<LoadedSourceContract>(),
            code,
            description);
    }

    private static SourceRefreshClientResult RejectRefresh(
        LoadedSourceContract source,
        string code,
        string description)
    {
        return new SourceRefreshClientResult(
            false,
            source with { Status = LoadedSourceStatus.Unavailable },
            code,
            description);
    }
}
