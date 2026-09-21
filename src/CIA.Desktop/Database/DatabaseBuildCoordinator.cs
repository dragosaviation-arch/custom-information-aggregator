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

    public event EventHandler<DatabaseGenerationSummary?>? PublishedGenerationChanged;

    public void AdoptRestoredGeneration(DatabaseGenerationSummary? restoredGeneration)
    {
        if (restoredGeneration is { IsHierarchyAware: false })
        {
            throw new ArgumentException(
                "Only hierarchy-aware Database generations can be restored.",
                nameof(restoredGeneration));
        }

        lock (_stateGate)
        {
            _currentGeneration = restoredGeneration;
        }

        PublishedGenerationChanged?.Invoke(this, restoredGeneration);
    }

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
                    || !DatabaseGenerationContextComparer.Matches(
                        specification,
                        result.PublishedGeneration)))
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
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.OperationFailed,
                "Database generation failed unexpectedly.");
        }
    }

    public DatabaseBuildSpecification CreateBuildSpecification()
    {
        var sourceSetNames = sourceSet.SourceSets.ToDictionary(
            definition => definition.SourceSetId,
            definition => definition.Name);
        return DatabaseBuildSpecificationFactory.Create(
            discoveryConfiguration.Current,
            discoveryConfiguration.DatabaseTagOverridesByIdentity,
            sourceSet.CreateIncludedReadySnapshot(),
            sourceSetNames);
    }

}
