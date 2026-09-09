using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Export;

public interface IWorkbookExportClient
{
    Task<WorkbookExportClientResult> ExportAsync(
        OperationCorrelation correlation,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string targetPath,
        CancellationToken cancellationToken = default);
}

public sealed record WorkbookExportClientResult(
    bool Accepted,
    OperationCompletion Completion,
    WorkbookExportSummary? Workbook,
    string? FailureCode,
    string? FailureDescription);
