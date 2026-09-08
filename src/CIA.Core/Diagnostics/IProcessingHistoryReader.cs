using CIA.Contracts.Diagnostics;

namespace CIA.Core.Diagnostics;

public interface IProcessingHistoryReader
{
    ProcessingHistorySnapshot Read();
}

public sealed record ProcessingHistorySnapshot(
    IReadOnlyList<ProcessingAttemptRecord> Attempts,
    IReadOnlyList<ProcessingDiagnosticRecord> Diagnostics,
    string? ReadProblem);
