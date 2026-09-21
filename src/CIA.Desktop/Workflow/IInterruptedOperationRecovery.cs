using CIA.Contracts.Operations;

namespace CIA.Desktop.Workflow;

public sealed record InterruptedOperationRecoveryAvailability(
    OperationCorrelation OriginalCorrelation,
    string OperationName,
    WorkflowOperationKind? OperationKind,
    DateTimeOffset InterruptedAtUtc,
    string InterruptionContext,
    bool ReinitiationPrerequisitesSatisfied,
    string? UnavailableReason)
{
    public OperationId OriginalOperationId => OriginalCorrelation.OperationId;
}

public interface IInterruptedOperationRecovery
{
    InterruptedOperationRecoveryAvailability? Current { get; }

    event EventHandler<InterruptedOperationRecoveryAvailability?>? AvailabilityChanged;
}
