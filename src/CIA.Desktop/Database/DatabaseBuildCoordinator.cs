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

        return CreateMapping().Columns.Count > 0;
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

        var sources = sourceSet.CreateIncludedReadySnapshot();
        var mapping = CreateMapping();
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
                .BuildAsync(begin.Operation, sources, mapping, cancellationToken)
                .ConfigureAwait(false);

            if (result.Accepted
                && (result.PublishedGeneration is null
                    || result.PublishedGeneration.OperationId != begin.Operation.OperationId
                    || !MappingsEqual(mapping, result.PublishedGeneration.Mapping)))
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

    private DatabaseMappingSnapshot CreateMapping()
    {
        return DatabaseTagMapper.CreateMapping(
            discoveryConfiguration.Current,
            discoveryConfiguration.DatabaseTagOverrides);
    }

    private static bool MappingsEqual(
        DatabaseMappingSnapshot expected,
        DatabaseMappingSnapshot actual)
    {
        return expected.Columns.Count == actual.Columns.Count
            && expected.Columns.Zip(actual.Columns).All(pair =>
                string.Equals(
                    pair.First.DatabaseTagName,
                    pair.Second.DatabaseTagName,
                    StringComparison.Ordinal)
                && pair.First.SourceInformationTypes.SequenceEqual(
                    pair.Second.SourceInformationTypes,
                    StringComparer.Ordinal));
    }
}
