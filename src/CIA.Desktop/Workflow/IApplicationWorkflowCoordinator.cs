using CIA.Contracts.Operations;

namespace CIA.Desktop.Workflow;

public interface IApplicationWorkflowCoordinator
{
    WorkflowStateSnapshot Current { get; }

    event EventHandler<WorkflowStateSnapshot>? StateChanged;

    WorkflowCommandResult RecordSourceSelectionChanged(bool hasValidSourceSelection);

    WorkflowCommandResult RecordDiscoveryConfigurationChanged();

    Task<WorkflowCommandResult> BeginOperationAsync(
        WorkflowOperationKind operationKind,
        CancellationToken cancellationToken = default);

    Task<WorkflowCommandResult> RequestCancellationAsync(
        CancellationToken cancellationToken = default);

    WorkflowCommandResult CompleteOperation(
        OperationId operationId,
        OperationOutcome outcome);

    WorkflowCommandResult CompleteOperation(OperationCompletion completion);
}
