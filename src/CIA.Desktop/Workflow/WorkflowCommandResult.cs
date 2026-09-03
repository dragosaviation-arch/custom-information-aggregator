using CIA.Contracts.Operations;

namespace CIA.Desktop.Workflow;

public enum WorkflowRejectionCode
{
    UnsupportedOperation = 1,
    MissingSourceSelection = 2,
    DiscoveryNotCurrent = 3,
    DatabaseNotCurrent = 4,
    ExtractionNotCurrent = 5,
    ConflictingOperation = 6,
    ProcessingHostUnavailable = 7,
    OperationMismatch = 8,
    InvalidOutcome = 9
}

public sealed record WorkflowRejection(
    WorkflowRejectionCode Code,
    string Reason);

public sealed record WorkflowCommandResult
{
    private WorkflowCommandResult(
        bool accepted,
        OperationCorrelation? operation,
        WorkflowRejection? rejection)
    {
        Accepted = accepted;
        Operation = operation;
        Rejection = rejection;
    }

    public bool Accepted { get; }

    public OperationCorrelation? Operation { get; }

    public WorkflowRejection? Rejection { get; }

    internal static WorkflowCommandResult Accept(OperationCorrelation? operation = null)
    {
        return new WorkflowCommandResult(true, operation, rejection: null);
    }

    internal static WorkflowCommandResult Reject(
        WorkflowRejectionCode code,
        string reason)
    {
        return new WorkflowCommandResult(
            false,
            operation: null,
            new WorkflowRejection(code, reason));
    }
}
