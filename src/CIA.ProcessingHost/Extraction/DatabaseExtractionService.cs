using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Extraction;

public sealed class DatabaseExtractionService(
    StructuredInformationRepository repository,
    CooperativeOperationCancellation operationCancellation,
    ILogger<DatabaseExtractionService> logger)
{
    private const string PublicationItemId = "extraction-publication";

    public async Task<DatabaseExtractionHostResult> ExtractAsync(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(databaseGeneration);

        var operation = operationCancellation.BeginOperation(
            correlation,
            "Extraction",
            "Published Database extraction",
            [new ProcessingItemPlan(PublicationItemId)]);
        if (!operation.TryStartItem(PublicationItemId, out var execution))
        {
            return DatabaseExtractionHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "extraction-not-started",
                "Extraction could not enter its processing boundary.");
        }

        using (execution)
        using (var extractionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   execution!.CancellationToken))
        {
            try
            {
                var result = await repository.ExtractPublishedDatabaseAsync(
                        correlation,
                        databaseGeneration,
                        extractionCancellation.Token,
                        operation.TryEnterNonCancellableCommitBoundary)
                    .ConfigureAwait(false);
                execution.CommitCompletedResult();
                return DatabaseExtractionHostResult.Accept(result, operation.Complete());
            }
            catch (OperationCanceledException)
                when (extractionCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    operationCancellation.RequestCancellation(correlation.OperationId);
                }

                execution.StopBeforeCommit();
                return DatabaseExtractionHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "extraction-cancelled",
                    "Extraction was cancelled before publication.");
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Extraction {OperationId} failed without publishing a result",
                    correlation.OperationId);
                execution.RecordFailure("extraction-failed");
                return DatabaseExtractionHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "extraction-failed",
                    "Extraction failed and no result was published.");
            }
        }
    }
}

public sealed record DatabaseExtractionHostResult(
    bool Accepted,
    OperationCompletion Completion,
    ExtractionResultSummary? PublishedResult,
    IpcFailure? Failure)
{
    internal static DatabaseExtractionHostResult Accept(
        ExtractionResultSummary result,
        OperationCompletion completion)
    {
        return new DatabaseExtractionHostResult(true, completion, result, Failure: null);
    }

    internal static DatabaseExtractionHostResult Reject(
        OperationCompletion completion,
        string failureCode,
        string failureDescription)
    {
        return new DatabaseExtractionHostResult(
            false,
            completion,
            PublishedResult: null,
            new IpcFailure(failureCode, failureDescription));
    }
}
