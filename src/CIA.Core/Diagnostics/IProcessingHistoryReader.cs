using CIA.Contracts.Diagnostics;

namespace CIA.Core.Diagnostics;

public interface IProcessingHistoryReader
{
    ProcessingHistorySnapshot Read();
}

public sealed record ProcessingHistorySnapshot(
    IReadOnlyList<ProcessingAttemptRecord> Attempts,
    IReadOnlyList<ProcessingDiagnosticRecord> Diagnostics,
    IReadOnlyList<ProcessingOperationStartRecord> Starts,
    string? ReadProblem)
{
    public bool RecoveryEvidenceComplete { get; init; } = true;

    public ProcessingHistorySnapshot(
        IReadOnlyList<ProcessingAttemptRecord> Attempts,
        IReadOnlyList<ProcessingDiagnosticRecord> Diagnostics,
        string? ReadProblem)
        : this(Attempts, Diagnostics, [], ReadProblem)
    {
    }
}
