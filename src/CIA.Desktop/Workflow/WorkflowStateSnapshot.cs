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

public sealed record ActiveWorkflowOperation(
    WorkflowOperationKind Kind,
    OperationCorrelation Correlation);

public sealed record WorkflowStateSnapshot(
    bool HasValidSourceSelection,
    WorkflowArtifactStatus Discovery,
    WorkflowArtifactStatus Database,
    WorkflowArtifactStatus Extraction,
    ActiveWorkflowOperation? ActiveOperation)
{
    public static WorkflowStateSnapshot Empty { get; } = new(
        HasValidSourceSelection: false,
        WorkflowArtifactStatus.Unavailable,
        WorkflowArtifactStatus.Unavailable,
        WorkflowArtifactStatus.Unavailable,
        ActiveOperation: null);
}
