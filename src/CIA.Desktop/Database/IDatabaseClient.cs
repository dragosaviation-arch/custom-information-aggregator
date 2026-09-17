using CIA.Contracts.Database;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Database;

public interface IDatabaseClient
{
    Task<DatabaseClientResult> BuildAsync(
        OperationCorrelation correlation,
        IReadOnlyList<CIA.Contracts.Sources.LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Legacy flat Database builds are no longer supported.");

    Task<DatabaseClientResult> BuildAsync(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default)
    {
        var columns = specification.Datasets.SelectMany(dataset => dataset.Fields)
            .GroupBy(field => field.EffectiveName, StringComparer.Ordinal)
            .Select(group => new DatabaseColumnMapping(
                group.Key,
                group.SelectMany(field => field.DetailedIdentities)
                    .Select(identity => identity.InformationType)
                    .Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
        return BuildAsync(
            correlation,
            specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray(),
            new DatabaseMappingSnapshot(columns),
            cancellationToken);
    }
}

public sealed record DatabaseClientResult(
    bool Accepted,
    OperationCompletion Completion,
    DatabaseGenerationSummary? PublishedGeneration,
    string? FailureCode,
    string? FailureDescription);
