using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Core.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Database;

public sealed class DatabaseBuildCoordinator(
    ActiveDiscoveryConfiguration discoveryConfiguration,
    ActiveLoadedSourceSet sourceSet,
    IApplicationWorkflowCoordinator workflowCoordinator,
    IDatabaseClient databaseClient,
    ILogger<DatabaseBuildCoordinator> logger)
{
    private readonly object _stateGate = new();
    private DatabaseGenerationSummary? _currentGeneration;

    public DatabaseGenerationSummary? CurrentGeneration
    {
        get
        {
            lock (_stateGate)
            {
                return _currentGeneration;
            }
        }
    }

    public event EventHandler<DatabaseGenerationSummary>? PublishedGenerationChanged;

    public bool CanBuild()
    {
        if (workflowCoordinator.Current.Discovery != WorkflowArtifactStatus.Current
            || workflowCoordinator.Current.ActiveOperation is not null
            || sourceSet.CreateIncludedReadySnapshot().Count == 0)
        {
            return false;
        }

        try
        {
            return CreateBuildSpecification().Datasets.Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<WorkflowCommandResult> BuildAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanBuild())
        {
            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.DiscoveryNotCurrent,
                "Database generation requires current Discovery results, selected information, and ready sources.");
        }

        var specification = CreateBuildSpecification();
        var begin = await workflowCoordinator
            .BeginOperationAsync(WorkflowOperationKind.DatabaseBuild, cancellationToken)
            .ConfigureAwait(false);
        if (!begin.Accepted || begin.Operation is null)
        {
            return begin;
        }

        try
        {
            var result = await databaseClient
                .BuildAsync(begin.Operation, specification, cancellationToken)
                .ConfigureAwait(false);

            if (result.Accepted
                && (result.PublishedGeneration is null
                    || result.PublishedGeneration.OperationId != begin.Operation.OperationId
                    || !SpecificationMatches(specification, result.PublishedGeneration)))
            {
                workflowCoordinator.CompleteOperation(
                    begin.Operation.OperationId,
                    OperationOutcome.Failed);
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationMismatch,
                    "The published Database does not match the initiating operation snapshot.");
            }

            var completion = workflowCoordinator.CompleteOperation(result.Completion);
            if (!completion.Accepted)
            {
                return completion;
            }

            if (!result.Accepted || result.PublishedGeneration is null)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationFailed,
                    result.FailureDescription ?? "Database generation did not publish a result.");
            }

            lock (_stateGate)
            {
                _currentGeneration = result.PublishedGeneration;
            }

            PublishedGenerationChanged?.Invoke(this, result.PublishedGeneration);
            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Database build coordination failed for operation {OperationId}",
                begin.Operation.OperationId);
            return workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
        }
    }

    public DatabaseBuildSpecification CreateBuildSpecification()
    {
        var configuration = discoveryConfiguration.Current;
        var overrides = discoveryConfiguration.DatabaseTagOverridesByIdentity;
        var includedSources = sourceSet.CreateIncludedReadySnapshot();
        var sourceSetNames = sourceSet.SourceSets.ToDictionary(
            definition => definition.SourceSetId,
            definition => definition.Name);
        var datasets = new List<DatabaseDatasetBuildSpecification>();

        foreach (var setConfiguration in configuration.SourceSets)
        {
            var fields = DatabaseTagMapper.CreateFieldMappings(
                setConfiguration.SourceSetId,
                configuration.Items,
                overrides);
            var sources = includedSources
                .Where(source => source.SourceSetId == setConfiguration.SourceSetId)
                .ToArray();
            if (fields.Count == 0 || sources.Length == 0)
            {
                continue;
            }

            datasets.Add(new DatabaseDatasetBuildSpecification(
                setConfiguration.SourceSetId,
                sourceSetNames.GetValueOrDefault(
                    setConfiguration.SourceSetId,
                    $"Set {datasets.Count + 1}"),
                datasets.Count + 1,
                setConfiguration.RepeatedDataLayout,
                sources,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }

    private static bool SpecificationMatches(
        DatabaseBuildSpecification expected,
        DatabaseGenerationSummary actual)
    {
        return actual.IsHierarchyAware
            && expected.Datasets.Count == actual.Datasets.Count
            && expected.Datasets.Zip(actual.Datasets).All(pair =>
                pair.First.SourceSetId == pair.Second.SourceSetId
                && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
                && pair.First.Ordinal == pair.Second.Ordinal
                && pair.First.RepeatedDataLayout == pair.Second.RepeatedDataLayout
                && FieldMappingsMatch(pair.First.Fields, pair.Second.Mappings));
    }

    private static bool FieldMappingsMatch(
        IReadOnlyList<DatabaseFieldMapping> expected,
        IReadOnlyList<DatabaseFieldMapping> actual)
    {
        return expected.Count == actual.Count
            && expected.Zip(actual).All(pair =>
                pair.First.LogicalIdentity == pair.Second.LogicalIdentity
                && string.Equals(
                    pair.First.EffectiveName,
                    pair.Second.EffectiveName,
                    StringComparison.Ordinal)
                && pair.First.IsExplicitOverride == pair.Second.IsExplicitOverride
                && pair.First.DetailedIdentities.SequenceEqual(pair.Second.DetailedIdentities));
    }
}
