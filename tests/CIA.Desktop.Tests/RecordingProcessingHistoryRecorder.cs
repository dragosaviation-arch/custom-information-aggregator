using CIA.Contracts.Diagnostics;
using CIA.Core.Diagnostics;

namespace CIA.Desktop.Tests;

internal sealed class RecordingProcessingHistoryRecorder : IProcessingHistoryRecorder
{
    public List<ProcessingAttemptRecord> Attempts { get; } = [];

    public List<ProcessingDiagnosticRecord> Diagnostics { get; } = [];

    public void RecordAttempt(ProcessingAttemptRecord record)
    {
        Attempts.Add(record);
    }

    public void RecordDiagnostic(ProcessingDiagnosticRecord record)
    {
        Diagnostics.Add(record);
    }
}
