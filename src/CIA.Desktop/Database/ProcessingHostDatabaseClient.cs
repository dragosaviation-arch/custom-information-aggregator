using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
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
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(specification);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, specification, "processing-host-unavailable");
            }

            var response = await requestClient.RequestDatabaseBuildAsync(
                    correlation,
                    specification,
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
            return Reject(correlation, specification, "processing-host-unavailable");
        }
    }

    public async Task<DatabaseReviewClientResult> ReadPageAsync(
        DatabaseReviewQuery query,
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
                    query,
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
                query.GenerationId);
            return RejectReview("processing-host-unavailable");
        }
    }

    public async Task<DatabaseRowInclusionClientResult> SetRowsIncludedAsync(
        DatabaseRowInclusionChange change,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectInclusion("processing-host-unavailable");
            }

            var response = await requestClient.RequestDatabaseRowsIncludedAsync(
                    change,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DatabaseRowInclusionClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.ChangedRowCount,
                response.Failure?.Code,
                response.Failure?.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database row inclusion could not be changed");
            return RejectInclusion("processing-host-unavailable");
        }
    }

    private static DatabaseClientResult Reject(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        string failureCode)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            specification.Datasets.SelectMany(dataset => dataset.Sources).Select(source => OperationItemStatus.Unprocessed(
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

    private static DatabaseRowInclusionClientResult RejectInclusion(string failureCode) =>
        new(false, 0, failureCode, "The Processing Host could not change Database row inclusion.");
}
