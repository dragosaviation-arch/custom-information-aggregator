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
        DatabaseReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await repository.ReadPublishedDatabasePageAsync(
                    query,
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
                query.GenerationId,
                query.StartRowOrdinal,
                query.StartRowOrdinal + query.RowCount - 1);
            return DatabaseReviewHostResult.Reject(
                "database-review-read-failed",
                "The published Database could not provide the requested bounded review page.");
        }
    }

    public async Task<DatabaseRowInclusionHostResult> SetRowsIncludedAsync(
        DatabaseRowInclusionChange change,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var changed = await repository.SetPublishedDatabaseRowsIncludedAsync(
                    change, cancellationToken)
                .ConfigureAwait(false);
            return DatabaseRowInclusionHostResult.Accept(changed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Published Database generation {GenerationId} row inclusion could not be changed",
                change.GenerationId);
            return DatabaseRowInclusionHostResult.Reject(
                "database-row-inclusion-failed",
                "The published Database rows could not be updated.");
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

public sealed record DatabaseRowInclusionHostResult(
    bool Accepted,
    int ChangedRowCount,
    IpcFailure? Failure)
{
    internal static DatabaseRowInclusionHostResult Accept(int changed) => new(true, changed, null);

    internal static DatabaseRowInclusionHostResult Reject(string code, string description) =>
        new(false, 0, new IpcFailure(code, description));
}
