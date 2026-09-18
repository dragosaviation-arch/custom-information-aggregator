using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;

namespace CIA.ProcessingHost.Export;

public sealed class ExcelWorkbookExportService
{
    private const string PublicationItemId = "workbook-publication";

    public Task<WorkbookExportHostResult> ExportAsync(
        OperationCorrelation correlation,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(extractionResult);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            [OperationItemStatus.Unprocessed(
                PublicationItemId,
                "set-aware-workbook-generation-not-supported")]);
        return Task.FromResult(WorkbookExportHostResult.Reject(
            completion,
            "set-aware-workbook-generation-not-supported",
            "SPR-140 configures Source-Set workbook routing; SPR-141 owns workbook generation."));
    }
}

public sealed record WorkbookExportHostResult(
    bool Accepted,
    OperationCompletion Completion,
    WorkbookExportSummary? Workbook,
    IpcFailure? Failure)
{
    internal static WorkbookExportHostResult Accept(
        WorkbookExportSummary workbook,
        OperationCompletion completion)
    {
        return new WorkbookExportHostResult(true, completion, workbook, Failure: null);
    }

    internal static WorkbookExportHostResult Reject(
        OperationCompletion completion,
        string failureCode,
        string failureDescription)
    {
        return new WorkbookExportHostResult(
            false,
            completion,
            Workbook: null,
            new IpcFailure(failureCode, failureDescription));
    }
}
