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
    private ExportConfigurationSnapshot? _readinessConfiguration;
    private string? _readinessOutputDirectory;

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

    public event EventHandler? ReadinessChanged;

    public void UpdateReadinessContext(
        ExportConfigurationSnapshot? configuration,
        string? outputDirectory)
    {
        lock (_stateGate)
        {
            _readinessConfiguration = configuration;
            _readinessOutputDirectory = outputDirectory;
        }

        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanExport()
    {
        return EvaluateReadiness().NormalOperationReady;
    }

    public WorkflowOperationReadiness EvaluateReadiness()
    {
        ExportConfigurationSnapshot? configuration;
        string? outputDirectory;
        lock (_stateGate)
        {
            configuration = _readinessConfiguration;
            outputDirectory = _readinessOutputDirectory;
        }

        return EvaluateReadiness(configuration, outputDirectory);
    }

    public WorkflowOperationReadiness EvaluateReadiness(
        ExportConfigurationSnapshot? configuration,
        string? outputDirectory)
    {
        return EvaluateReadiness(configuration, outputDirectory, publicationPlan: null);
    }

    private WorkflowOperationReadiness EvaluateReadiness(
        ExportConfigurationSnapshot? configuration,
        string? outputDirectory,
        WorkbookPublicationPlan? publicationPlan)
    {
        var workflowReadiness = WorkflowOperationReadiness.FromWorkflow(
            workflowCoordinator.EvaluateOperationPrerequisites(
                WorkflowOperationKind.Export));
        if (!workflowReadiness.NormalOperationReady)
        {
            return workflowReadiness;
        }

        if (extractionCoordinator.CurrentResult is not { IsHierarchyAware: true } extractionResult)
        {
            return WorkflowOperationReadiness.Unavailable(
                workflowPrerequisitesSatisfied: true,
                WorkflowRejectionCode.OperationFailed,
                "Export requires the active prepared Extraction Result to be current.");
        }

        if (configuration is null)
        {
            return WorkflowOperationReadiness.Unavailable(
                workflowPrerequisitesSatisfied: true,
                WorkflowRejectionCode.OperationFailed,
                "Configure workbook routing before exporting.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory)
            || !Path.IsPathFullyQualified(outputDirectory)
            || !Directory.Exists(outputDirectory))
        {
            return WorkflowOperationReadiness.Unavailable(
                workflowPrerequisitesSatisfied: true,
                WorkflowRejectionCode.OperationFailed,
                "Choose a valid export destination folder before exporting.");
        }

        var validation = ExportConfigurationValidator.Validate(
            configuration,
            extractionResult);
        if (!validation.IsValid
            || validation.RunnableWorkbooks.Count == 0
            || !configuration.SourceSets.Any(set =>
                set.IsEnabled && set.Fields.Any(field => field.IsValueIncluded)))
        {
            return WorkflowOperationReadiness.Unavailable(
                workflowPrerequisitesSatisfied: true,
                WorkflowRejectionCode.OperationFailed,
                validation.Failures.FirstOrDefault()?.Description
                    ?? "Enable at least one valid value field and workbook route before exporting.");
        }

        var conflictingWorkbook = validation.RunnableWorkbooks.FirstOrDefault(workbook =>
        {
            var finalPath = Path.Combine(outputDirectory, workbook.Workbook.FileName);
            if (!File.Exists(finalPath) && !Directory.Exists(finalPath))
            {
                return false;
            }

            return publicationPlan?.Targets.SingleOrDefault(target =>
                    target.WorkbookDefinitionId == workbook.Workbook.WorkbookDefinitionId)
                is not
                {
                    Disposition: WorkbookPublicationDisposition.OverwriteExisting
                } target
                || !string.Equals(target.FinalPath, finalPath, StringComparison.OrdinalIgnoreCase);
        });
        return conflictingWorkbook is null
            ? WorkflowOperationReadiness.Ready()
            : WorkflowOperationReadiness.RequiresUserInput(
                $"Resolve the existing workbook '{conflictingWorkbook.Workbook.FileName}' before exporting.");
    }

    public async Task<WorkflowCommandResult> ExportAsync(
        WorkbookPublicationPlan publicationPlan,
        ExportConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicationPlan);
        ArgumentNullException.ThrowIfNull(configuration);

        var extractionResult = extractionCoordinator.CurrentResult;
        var readiness = EvaluateReadiness(
            configuration,
            publicationPlan.OutputDirectory,
            publicationPlan);
        if (!readiness.NormalOperationReady || extractionResult is null)
        {
            return readiness.ToCommandResult();
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
                    publicationPlan,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Accepted
                && (result.Batch is null
                    || !MatchesRequest(
                        result.Batch,
                        begin.Operation.OperationId,
                        extractionResult,
                        configuration,
                        publicationPlan)))
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
        WorkbookPublicationPlan publicationPlan)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
        if (!validation.IsValid
            || batch.OperationId != operationId
            || batch.ExtractionResultId != extractionResult.OperationId
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(batch.OutputDirectory)),
                publicationPlan.OutputDirectory,
                StringComparison.OrdinalIgnoreCase)
            || batch.Workbooks.Count != validation.RunnableWorkbooks.Count)
        {
            return false;
        }

        var targetsById = publicationPlan.Targets.ToDictionary(target =>
            target.WorkbookDefinitionId);
        return validation.RunnableWorkbooks.Zip(batch.Workbooks).All(pair =>
            pair.First.Workbook.WorkbookDefinitionId == pair.Second.WorkbookDefinitionId
            && pair.First.Workbook.Order == pair.Second.Order
            && targetsById.TryGetValue(pair.First.Workbook.WorkbookDefinitionId, out var target)
            && string.Equals(pair.Second.FinalPath, target.FinalPath, StringComparison.OrdinalIgnoreCase)
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
