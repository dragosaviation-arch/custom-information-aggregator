using CIA.Contracts.Operations;

namespace CIA.Desktop.Workflow;

public sealed record InterruptedOperationRecoveryAvailability(
    OperationCorrelation OriginalCorrelation,
    string OperationName,
    WorkflowOperationKind? OperationKind,
    DateTimeOffset InterruptedAtUtc,
    string InterruptionContext,
    bool WorkflowPrerequisitesSatisfied,
    bool NormalOperationReady,
    bool AdditionalUserInputRequired,
    string? UnavailableReason)
{
    public OperationId OriginalOperationId => OriginalCorrelation.OperationId;

    public bool ReinitiationPrerequisitesSatisfied => NormalOperationReady;
}

public interface IInterruptedOperationRecovery
{
    InterruptedOperationRecoveryAvailability? Current { get; }

    event EventHandler<InterruptedOperationRecoveryAvailability?>? AvailabilityChanged;
}
