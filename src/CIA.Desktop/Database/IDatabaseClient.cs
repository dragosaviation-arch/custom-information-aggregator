using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Desktop.Database;

public interface IDatabaseClient
{
    Task<DatabaseClientResult> BuildAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping,
        CancellationToken cancellationToken = default);
}

public sealed record DatabaseClientResult(
    bool Accepted,
    OperationCompletion Completion,
    DatabaseGenerationSummary? PublishedGeneration,
    string? FailureCode,
    string? FailureDescription);
