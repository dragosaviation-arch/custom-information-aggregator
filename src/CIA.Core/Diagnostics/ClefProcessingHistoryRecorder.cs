using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using Microsoft.Extensions.Logging;

namespace CIA.Core.Diagnostics;

public sealed class ClefProcessingHistoryRecorder(
    ILogger<ClefProcessingHistoryRecorder> logger) : IProcessingHistoryRecorder
{
    private readonly ILogger<ClefProcessingHistoryRecorder> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public void RecordAttempt(ProcessingAttemptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var completedItems = SelectItems(record, OperationItemState.ProcessedSuccessfully);
        var failedItems = SelectItems(record, OperationItemState.Failed);
        var unprocessedItems = SelectItems(record, OperationItemState.Unprocessed);

        using (_logger.BeginScope(CreateAttemptScope(record)))
        {
            _logger.LogInformation(
                "Processing attempt {OperationName} retained with terminal outcome {TerminalOutcome}; completed {CompletedItemCount}, failed {FailedItemCount}, unprocessed {UnprocessedItemCount}. {@CompletedItems} {@FailedItems} {@UnprocessedItems}",
                record.OperationName,
                record.TerminalOutcome,
                completedItems.Length,
                failedItems.Length,
                unprocessedItems.Length,
                completedItems,
                failedItems,
                unprocessedItems);
        }
    }

    public void RecordDiagnostic(ProcessingDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using (_logger.BeginScope(CreateDiagnosticScope(record)))
        {
            _logger.LogWarning(
                "Processing diagnostic {DiagnosticId} retained for {OperationName}",
                record.DiagnosticId.ToString(),
                record.OperationName);
        }
    }

    private static OperationItemStatus[] SelectItems(
        ProcessingAttemptRecord record,
        OperationItemState state)
    {
        return record.Items.Where(item => item.State == state).ToArray();
    }

    private static Dictionary<string, object?> CreateAttemptScope(
        ProcessingAttemptRecord record)
    {
        return new Dictionary<string, object?>
        {
            ["RecordType"] = "ProcessingAttempt",
            ["OperationId"] = record.Correlation.OperationId.ToString(),
            ["OperationInitiatedAtUtc"] = record.Correlation.InitiatedAtUtc,
            ["HistoryRecordedAtUtc"] = record.RecordedAtUtc,
            ["ProcessingStage"] = record.FinalStage
        };
    }

    private static Dictionary<string, object?> CreateDiagnosticScope(
        ProcessingDiagnosticRecord record)
    {
        return new Dictionary<string, object?>
        {
            ["RecordType"] = "ProcessingDiagnostic",
            ["OperationId"] = record.Correlation.OperationId.ToString(),
            ["OperationInitiatedAtUtc"] = record.Correlation.InitiatedAtUtc,
            ["DiagnosticRecordedAtUtc"] = record.RecordedAtUtc,
            ["ProcessingStage"] = record.ProcessingStage,
            ["SourceId"] = record.SourceId,
            ["ItemId"] = record.ItemId,
            ["ItemState"] = record.ItemState,
            ["TerminalOutcome"] = record.TerminalOutcome,
            ["UserFacingDescription"] = record.UserFacingDescription,
            ["FailureCode"] = record.FailureCode,
            ["TechnicalDetail"] = record.TechnicalDetail
        };
    }
}
