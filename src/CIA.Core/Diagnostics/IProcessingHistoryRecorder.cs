using CIA.Contracts.Diagnostics;

namespace CIA.Core.Diagnostics;

public interface IProcessingHistoryRecorder
{
    void RecordStart(ProcessingOperationStartRecord record)
    {
    }

    void RecordAttempt(ProcessingAttemptRecord record);

    void RecordDiagnostic(ProcessingDiagnosticRecord record);
}
