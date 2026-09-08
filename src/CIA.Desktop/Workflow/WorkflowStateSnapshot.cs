using CIA.Contracts.Operations;

namespace CIA.Desktop.Workflow;

public enum WorkflowArtifactStatus
{
    Unavailable = 0,
    Current = 1,
    Stale = 2
}

public enum WorkflowOperationKind
{
    Discovery = 1,
    DatabaseBuild = 2,
    Extraction = 3,
    Export = 4
}

public enum WorkflowOperationState
{
    Active = 1,
    CompletedSuccessfully = 2,
    CompletedWithIssues = 3,
    Failed = 4,
    Cancelled = 5,
    InterruptedIncomplete = 6,
    Cancelling = 7
}

public sealed record ActiveWorkflowOperation(
    WorkflowOperationKind Kind,
    OperationCorrelation Correlation);

public sealed record WorkflowOperationStatus(
    WorkflowOperationKind Kind,
    OperationCorrelation Correlation,
    WorkflowOperationState State,
    string? Detail,
    OperationCompletion? Completion = null);

public sealed record WorkflowStateSnapshot(
    bool HasValidSourceSelection,
    WorkflowArtifactStatus Discovery,
    WorkflowArtifactStatus Database,
    WorkflowArtifactStatus Extraction,
    ActiveWorkflowOperation? ActiveOperation,
    WorkflowOperationStatus? LatestOperation)
{
    public static WorkflowStateSnapshot Empty { get; } = new(
        HasValidSourceSelection: false,
        WorkflowArtifactStatus.Unavailable,
        WorkflowArtifactStatus.Unavailable,
        WorkflowArtifactStatus.Unavailable,
        ActiveOperation: null,
        LatestOperation: null);
}
