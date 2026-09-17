using CIA.Contracts.Database;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Database;

public interface IDatabaseReviewClient
{
    Task<DatabaseReviewClientResult> ReadPageAsync(
        OperationId generationId,
        int startRowOrdinal,
        int rowCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DatabaseReviewClientResult(
            false, null, "legacy-database-review-unavailable",
            "Legacy independent-column Database review is no longer available."));

    Task<DatabaseReviewClientResult> ReadPageAsync(
        DatabaseReviewQuery query,
        CancellationToken cancellationToken = default) =>
        ReadPageAsync(
            query.GenerationId,
            query.StartRowOrdinal,
            query.RowCount,
            cancellationToken);

    Task<DatabaseRowInclusionClientResult> SetRowsIncludedAsync(
        DatabaseRowInclusionChange change,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DatabaseRowInclusionClientResult(
            false, 0, "database-row-inclusion-unavailable",
            "Database row inclusion is unavailable."));
}

public sealed record DatabaseReviewClientResult(
    bool Accepted,
    DatabaseReviewPage? Page,
    string? FailureCode,
    string? FailureDescription);

public sealed record DatabaseRowInclusionClientResult(
    bool Accepted,
    int ChangedRowCount,
    string? FailureCode,
    string? FailureDescription);
