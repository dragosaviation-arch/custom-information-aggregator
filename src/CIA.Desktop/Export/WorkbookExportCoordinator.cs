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
    private WorkbookExportBatchSummary? _lastBatch;

    public WorkbookExportBatchSummary? LastBatch
    {
        get
        {
            lock (_stateGate)
            {
                return _lastBatch;
            }
        }
    }

    public bool CanExport()
    {
        return workflowCoordinator.Current.Extraction == WorkflowArtifactStatus.Current
            && workflowCoordinator.Current.ActiveOperation is null
            && extractionCoordinator.CurrentResult is { IsHierarchyAware: true };
    }

    public async Task<WorkflowCommandResult> ExportAsync(
        string outputDirectory,
        ExportConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
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
                    outputDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Accepted
                && (result.Batch is null
                    || !MatchesRequest(
                        result.Batch,
                        begin.Operation.OperationId,
                        extractionResult,
                        configuration,
                        outputDirectory)))
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

            if (!result.Accepted || result.Batch is null)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationFailed,
                    result.FailureDescription ?? "The workbook was not published.");
            }

            lock (_stateGate)
            {
                _lastBatch = result.Batch;
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
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.OperationFailed,
                "Workbook export coordination failed.");
        }
    }

    private static bool MatchesRequest(
        WorkbookExportBatchSummary batch,
        OperationId operationId,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string outputDirectory)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
        if (!validation.IsValid
            || batch.OperationId != operationId
            || batch.ExtractionResultId != extractionResult.OperationId
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(batch.OutputDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)),
                StringComparison.OrdinalIgnoreCase)
            || batch.Workbooks.Count != validation.RunnableWorkbooks.Count)
        {
            return false;
        }

        return validation.RunnableWorkbooks.Zip(batch.Workbooks).All(pair =>
            pair.First.Workbook.WorkbookDefinitionId == pair.Second.WorkbookDefinitionId
            && pair.First.Workbook.Order == pair.Second.Order
            && string.Equals(
                Path.GetFileName(pair.Second.FinalPath),
                pair.First.Workbook.FileName,
                StringComparison.Ordinal)
            && pair.First.Worksheets.Count == pair.Second.Worksheets.Count
            && pair.First.Worksheets.Zip(pair.Second.Worksheets).All(worksheetPair =>
            {
                var dataset = extractionResult.Datasets.Single(dataset =>
                    dataset.SourceSetId == worksheetPair.First.Worksheet.SourceSetId);
                return worksheetPair.First.Worksheet.WorksheetDefinitionId
                        == worksheetPair.Second.WorksheetDefinitionId
                    && worksheetPair.First.Worksheet.SourceSetId
                        == worksheetPair.Second.SourceSetId
                    && string.Equals(
                        worksheetPair.First.Worksheet.Name,
                        worksheetPair.Second.Name,
                        StringComparison.Ordinal)
                    && worksheetPair.First.Worksheet.Order == worksheetPair.Second.Order
                    && worksheetPair.Second.RowCount == dataset.RowCount
                    && worksheetPair.Second.ColumnCount
                        == configuration.CreateIncludedOutputColumns(dataset.SourceSetId).Count;
            }));
    }
}
