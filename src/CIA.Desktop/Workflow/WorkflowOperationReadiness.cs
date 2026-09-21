using CIA.Desktop.Database;
using CIA.Desktop.Export;
using CIA.Desktop.Extraction;
using CIA.Desktop.WorkingState;

namespace CIA.Desktop.Workflow;

public sealed record WorkflowOperationReadiness(
    bool WorkflowPrerequisitesSatisfied,
    bool NormalOperationReady,
    bool AdditionalUserInputRequired,
    WorkflowRejectionCode? RejectionCode,
    string? UnavailableReason)
{
    public static WorkflowOperationReadiness FromWorkflow(
        WorkflowCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Accepted
            ? Ready()
            : Unavailable(
                workflowPrerequisitesSatisfied: false,
                result.Rejection?.Code ?? WorkflowRejectionCode.OperationFailed,
                result.Rejection?.Reason ?? "The workflow operation is not currently available.");
    }

    public static WorkflowOperationReadiness Ready() => new(
        WorkflowPrerequisitesSatisfied: true,
        NormalOperationReady: true,
        AdditionalUserInputRequired: false,
        RejectionCode: null,
        UnavailableReason: null);

    public static WorkflowOperationReadiness Unavailable(
        bool workflowPrerequisitesSatisfied,
        WorkflowRejectionCode rejectionCode,
        string reason) => new(
            workflowPrerequisitesSatisfied,
            NormalOperationReady: false,
            AdditionalUserInputRequired: false,
            rejectionCode,
            reason);

    public static WorkflowOperationReadiness RequiresUserInput(string reason) => new(
        WorkflowPrerequisitesSatisfied: true,
        NormalOperationReady: false,
        AdditionalUserInputRequired: true,
        WorkflowRejectionCode.OperationFailed,
        reason);

    internal WorkflowCommandResult ToCommandResult()
    {
        return NormalOperationReady
            ? WorkflowCommandResult.Accept()
            : WorkflowCommandResult.Reject(
                RejectionCode ?? WorkflowRejectionCode.OperationFailed,
                UnavailableReason ?? "The operation is not currently ready.");
    }
}

public interface IWorkflowOperationReadiness
{
    WorkflowOperationReadiness Evaluate(WorkflowOperationKind operationKind);

    event EventHandler? ReadinessChanged;
}

public sealed class WorkflowOperationReadinessProvider :
    IWorkflowOperationReadiness,
    IDisposable
{
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly DatabaseBuildCoordinator _databaseBuildCoordinator;
    private readonly ExtractionCoordinator _extractionCoordinator;
    private readonly WorkbookExportCoordinator _workbookExportCoordinator;
    private readonly WorkingStateCoordinator _workingStateCoordinator;
    private int _disposed;

    public WorkflowOperationReadinessProvider(
        IApplicationWorkflowCoordinator workflowCoordinator,
        DatabaseBuildCoordinator databaseBuildCoordinator,
        ExtractionCoordinator extractionCoordinator,
        WorkbookExportCoordinator workbookExportCoordinator,
        WorkingStateCoordinator workingStateCoordinator)
    {
        ArgumentNullException.ThrowIfNull(workflowCoordinator);
        ArgumentNullException.ThrowIfNull(databaseBuildCoordinator);
        ArgumentNullException.ThrowIfNull(extractionCoordinator);
        ArgumentNullException.ThrowIfNull(workbookExportCoordinator);
        ArgumentNullException.ThrowIfNull(workingStateCoordinator);

        _workflowCoordinator = workflowCoordinator;
        _databaseBuildCoordinator = databaseBuildCoordinator;
        _extractionCoordinator = extractionCoordinator;
        _workbookExportCoordinator = workbookExportCoordinator;
        _workingStateCoordinator = workingStateCoordinator;
        _databaseBuildCoordinator.PublishedGenerationChanged += OnReadinessSourceChanged;
        _extractionCoordinator.PublishedResultChanged += OnReadinessSourceChanged;
        _workbookExportCoordinator.ReadinessChanged += OnReadinessSourceChanged;
    }

    public event EventHandler? ReadinessChanged;

    public WorkflowOperationReadiness Evaluate(WorkflowOperationKind operationKind)
    {
        return operationKind switch
        {
            WorkflowOperationKind.Discovery => WorkflowOperationReadiness.FromWorkflow(
                _workflowCoordinator.EvaluateOperationPrerequisites(operationKind)),
            WorkflowOperationKind.DatabaseBuild =>
                _databaseBuildCoordinator.EvaluateReadiness(),
            WorkflowOperationKind.Extraction => _extractionCoordinator.EvaluateReadiness(),
            WorkflowOperationKind.Export => _workbookExportCoordinator.EvaluateReadiness(),
            WorkflowOperationKind.WorkingStateSave =>
                _workingStateCoordinator.EvaluateSaveReadiness(),
            WorkflowOperationKind.WorkingStateRestore =>
                _workingStateCoordinator.EvaluateRestoreReadiness(packagePath: null),
            _ => WorkflowOperationReadiness.FromWorkflow(
                _workflowCoordinator.EvaluateOperationPrerequisites(operationKind))
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _databaseBuildCoordinator.PublishedGenerationChanged -= OnReadinessSourceChanged;
        _extractionCoordinator.PublishedResultChanged -= OnReadinessSourceChanged;
        _workbookExportCoordinator.ReadinessChanged -= OnReadinessSourceChanged;
    }

    private void OnReadinessSourceChanged<T>(object? sender, T value)
    {
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnReadinessSourceChanged(object? sender, EventArgs e)
    {
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }
}
