using CIA.Contracts.Database;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Database;

public interface IDatabaseReviewClient
{
    Task<DatabaseReviewClientResult> ReadPageAsync(
        OperationId generationId,
        int startRowOrdinal,
        int rowCount,
        CancellationToken cancellationToken = default);
}

public sealed record DatabaseReviewClientResult(
    bool Accepted,
    DatabaseReviewPage? Page,
    string? FailureCode,
    string? FailureDescription);
