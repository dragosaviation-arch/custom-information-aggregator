using System.IO;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Desktop.Extraction;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Export;

public sealed class WorkbookExportCoordinator(
    ExtractionCoordinator extractionCoordinator,
    IApplicationWorkflowCoordinator workflowCoordinator,
    IWorkbookExportClient exportClient,
    ILogger<WorkbookExportCoordinator> logger)
{
    private readonly object _stateGate = new();
    private WorkbookExportSummary? _lastWorkbook;

    public WorkbookExportSummary? LastWorkbook
    {
        get
        {
            lock (_stateGate)
            {
                return _lastWorkbook;
            }
        }
    }

    public bool CanExport()
    {
        return workflowCoordinator.Current.Extraction == WorkflowArtifactStatus.Current
            && workflowCoordinator.Current.ActiveOperation is null
            && extractionCoordinator.CurrentResult is not null;
    }

    public async Task<WorkflowCommandResult> ExportAsync(
        string targetPath,
        ExportConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(configuration);

        var extractionResult = extractionCoordinator.CurrentResult;
        if (!CanExport() || extractionResult is null)
        {
            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.ExtractionNotCurrent,
                "Export requires the active prepared Extraction Result to be current.");
        }

        var begin = await workflowCoordinator
            .BeginOperationAsync(WorkflowOperationKind.Export, cancellationToken)
            .ConfigureAwait(false);
        if (!begin.Accepted || begin.Operation is null)
        {
            return begin;
        }

        try
        {
            var result = await exportClient
                .ExportAsync(
                    begin.Operation,
                    extractionResult,
                    configuration,
                    targetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Accepted
                && (result.Workbook is null
                    || result.Workbook.OperationId != begin.Operation.OperationId
                    || result.Workbook.ExtractionResultId != extractionResult.OperationId
                    || result.Workbook.ColumnCount
                        != configuration.CreateIncludedOutputColumns().Count
                    || !string.Equals(
                        Path.GetFullPath(result.Workbook.TargetPath),
                        Path.GetFullPath(targetPath),
                        StringComparison.OrdinalIgnoreCase)))
            {
                workflowCoordinator.CompleteOperation(
                    begin.Operation.OperationId,
                    OperationOutcome.Failed);
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationMismatch,
                    "The exported workbook does not match its captured Extraction Result and configuration.");
            }

            var completion = workflowCoordinator.CompleteOperation(result.Completion);
            if (!completion.Accepted)
            {
                return completion;
            }

            if (!result.Accepted || result.Workbook is null)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationFailed,
                    result.FailureDescription ?? "The workbook was not published.");
            }

            lock (_stateGate)
            {
                _lastWorkbook = result.Workbook;
            }

            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Workbook export coordination failed for operation {OperationId}",
                begin.Operation.OperationId);
            return workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
        }
    }
}
