using CIA.Contracts.Diagnostics;

namespace CIA.Core.Diagnostics;

public interface IProcessingHistoryRecorder
{
    void RecordAttempt(ProcessingAttemptRecord record);

    void RecordDiagnostic(ProcessingDiagnosticRecord record);
}
