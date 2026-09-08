using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Database;

public sealed class ProcessingHostDatabaseClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostDatabaseClient> logger) : IDatabaseClient, IDatabaseReviewClient
{
    public async Task<DatabaseClientResult> BuildAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(mapping);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, sources, "processing-host-unavailable");
            }

            var response = await requestClient.RequestDatabaseBuildAsync(
                    correlation,
                    sources,
                    mapping,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DatabaseClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Completion,
                response.PublishedGeneration,
                response.Failure?.Code,
                response.Failure?.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Database build could not be completed by the Processing Host for operation {OperationId}",
                correlation.OperationId);
            return Reject(correlation, sources, "processing-host-unavailable");
        }
    }

    public async Task<DatabaseReviewClientResult> ReadPageAsync(
        OperationId generationId,
        int startRowOrdinal,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectReview("processing-host-unavailable");
            }

            var response = await requestClient.RequestDatabaseReviewPageAsync(
                    generationId,
                    startRowOrdinal,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DatabaseReviewClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Page,
                response.Failure?.Code,
                response.Failure?.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Database generation {GenerationId} review page could not be retrieved from the Processing Host",
                generationId);
            return RejectReview("processing-host-unavailable");
        }
    }

    private static DatabaseClientResult Reject(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        string failureCode)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            sources.Select(source => OperationItemStatus.Unprocessed(
                source.SourceId.ToString(),
                failureCode)).ToArray());
        return new DatabaseClientResult(
            false,
            completion,
            PublishedGeneration: null,
            failureCode,
            "The Processing Host could not complete Database generation.");
    }

    private static DatabaseReviewClientResult RejectReview(string failureCode)
    {
        return new DatabaseReviewClientResult(
            false,
            Page: null,
            failureCode,
            "The Processing Host could not provide the published Database review page.");
    }
}
