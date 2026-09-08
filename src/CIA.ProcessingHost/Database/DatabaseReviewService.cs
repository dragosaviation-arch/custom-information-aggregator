using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.ProcessingHost.Repository;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Database;

public sealed class DatabaseReviewService(
    StructuredInformationRepository repository,
    ILogger<DatabaseReviewService> logger)
{
    public async Task<DatabaseReviewHostResult> ReadPageAsync(
        OperationId generationId,
        int startRowOrdinal,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await repository.ReadPublishedDatabasePageAsync(
                    generationId,
                    startRowOrdinal,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            return page is null
                ? DatabaseReviewHostResult.Reject(
                    "database-generation-out-of-date",
                    "The requested Database generation is no longer the active published generation.")
                : DatabaseReviewHostResult.Accept(page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Published Database generation {GenerationId} could not provide review rows {StartRowOrdinal} through {EndRowOrdinal}",
                generationId,
                startRowOrdinal,
                startRowOrdinal + rowCount - 1);
            return DatabaseReviewHostResult.Reject(
                "database-review-read-failed",
                "The published Database could not provide the requested bounded review page.");
        }
    }
}

public sealed record DatabaseReviewHostResult(
    bool Accepted,
    DatabaseReviewPage? Page,
    IpcFailure? Failure)
{
    internal static DatabaseReviewHostResult Accept(DatabaseReviewPage page)
    {
        return new DatabaseReviewHostResult(true, page, Failure: null);
    }

    internal static DatabaseReviewHostResult Reject(string code, string description)
    {
        return new DatabaseReviewHostResult(
            false,
            Page: null,
            new IpcFailure(code, description));
    }
}
