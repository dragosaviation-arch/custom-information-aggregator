using CIA.Contracts.Database;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Database;

public interface IDatabaseClient
{
    Task<DatabaseClientResult> BuildAsync(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default);
}

public sealed record DatabaseClientResult(
    bool Accepted,
    OperationCompletion Completion,
    DatabaseGenerationSummary? PublishedGeneration,
    string? FailureCode,
    string? FailureDescription);
