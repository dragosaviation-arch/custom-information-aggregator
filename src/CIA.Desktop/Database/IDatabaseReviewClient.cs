using CIA.Contracts.Database;

namespace CIA.Desktop.Database;

public interface IDatabaseReviewClient
{
    Task<DatabaseReviewClientResult> ReadPageAsync(
        DatabaseReviewQuery query,
        CancellationToken cancellationToken = default);

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
