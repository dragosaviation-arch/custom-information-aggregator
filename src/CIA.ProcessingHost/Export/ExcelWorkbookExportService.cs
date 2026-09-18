using CIA.Contracts.Database;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Export;

public sealed class ExcelWorkbookExportService
{
    private const string PublicationItemId = "workbook-batch-publication";
    private readonly IExtractionExportRowSource _rowSource;
    private readonly CooperativeOperationCancellation _operationCancellation;
    private readonly ILogger<ExcelWorkbookExportService> _logger;

    public ExcelWorkbookExportService(
        StructuredInformationRepository repository,
        CooperativeOperationCancellation operationCancellation,
        ILogger<ExcelWorkbookExportService> logger)
        : this(
            new RepositoryExtractionExportRowSource(repository),
            operationCancellation,
            logger)
    {
    }

    internal ExcelWorkbookExportService(
        IExtractionExportRowSource rowSource,
        CooperativeOperationCancellation operationCancellation,
        ILogger<ExcelWorkbookExportService> logger)
    {
        _rowSource = rowSource ?? throw new ArgumentNullException(nameof(rowSource));
        _operationCancellation = operationCancellation
            ?? throw new ArgumentNullException(nameof(operationCancellation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkbookExportHostResult> ExportAsync(
        OperationCorrelation correlation,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(extractionResult);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var operation = _operationCancellation.BeginOperation(
            correlation,
            "Workbook export",
            "Routed workbook generation",
            [new ProcessingItemPlan(PublicationItemId)]);
        if (!operation.TryStartItem(PublicationItemId, out var execution))
        {
            return WorkbookExportHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "workbook-export-not-started",
                "Workbook export could not enter its processing boundary.");
        }

        using (execution)
        using (var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   execution!.CancellationToken))
        {
            try
            {
                var resolvedOutputDirectory = ValidateOutputDirectory(outputDirectory);
                var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
                if (!validation.IsValid)
                {
                    throw new WorkbookExportException(
                        validation.Failures[0].Code,
                        validation.Failures[0].Description);
                }

                if (validation.RunnableWorkbooks.Count == 0)
                {
                    throw new WorkbookExportException(
                        "empty-export-batch",
                        "The export configuration has no enabled Source Sets to publish.");
                }

                var publishedResult = await _rowSource.ReadPublishedResultAsync(
                        exportCancellation.Token)
                    .ConfigureAwait(false);
                if (!ExtractionResultsMatch(extractionResult, publishedResult))
                {
                    throw new WorkbookExportException(
                        "extraction-result-not-current",
                        "The captured Extraction Result is no longer the active published result.");
                }

                var targets = validation.RunnableWorkbooks.Select(workbook => (
                    workbook.Workbook.WorkbookDefinitionId,
                    FinalPath: Path.Combine(
                        resolvedOutputDirectory,
                        workbook.Workbook.FileName))).ToArray();
                using var publication = WorkbookBatchPublicationScope.Create(targets);
                var workbookSummaries = new List<WorkbookExportFileSummary>(
                    validation.RunnableWorkbooks.Count);

                foreach (var runnableWorkbook in validation.RunnableWorkbooks)
                {
                    exportCancellation.Token.ThrowIfCancellationRequested();
                    var candidate = publication.Candidates.Single(candidate =>
                        candidate.WorkbookDefinitionId
                            == runnableWorkbook.Workbook.WorkbookDefinitionId);
                    var worksheetCounters = runnableWorkbook.Worksheets.Select(worksheet =>
                        new WorksheetWriteCounter(worksheet)).ToArray();
                    var plans = worksheetCounters.Select(counter => new ExcelWorksheetWritePlan(
                        counter.Definition.Worksheet.Name,
                        (writer, token) => WriteWorksheetAsync(
                            writer,
                            extractionResult.OperationId,
                            configuration.CreateIncludedOutputColumns(
                                counter.Definition.Worksheet.SourceSetId),
                            counter,
                            token))).ToArray();

                    await ExcelWorkbookExportFoundation.WriteTemporaryWorkbookAsync(
                            candidate.TemporaryPath,
                            plans,
                            exportCancellation.Token)
                        .ConfigureAwait(false);
                    workbookSummaries.Add(new WorkbookExportFileSummary(
                        runnableWorkbook.Workbook.WorkbookDefinitionId,
                        candidate.FinalPath,
                        runnableWorkbook.Workbook.Order,
                        worksheetCounters.Select(counter =>
                            new WorkbookExportWorksheetSummary(
                                counter.Definition.Worksheet.WorksheetDefinitionId,
                                counter.Definition.Worksheet.SourceSetId,
                                counter.Definition.Worksheet.Name,
                                counter.Definition.Worksheet.Order,
                                counter.RowCount,
                                counter.ColumnCount)).ToArray()));
                }

                var batch = new WorkbookExportBatchSummary(
                    correlation.OperationId,
                    extractionResult.OperationId,
                    resolvedOutputDirectory,
                    workbookSummaries);
                publication.Publish(
                    operation.TryEnterNonCancellableCommitBoundary,
                    exportCancellation.Token);
                execution.CommitCompletedResult();
                return WorkbookExportHostResult.Accept(batch, operation.Complete());
            }
            catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    _operationCancellation.RequestCancellation(correlation.OperationId);
                }

                execution.StopBeforeCommit();
                return WorkbookExportHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "workbook-export-cancelled",
                    "Workbook export was cancelled before batch publication.");
            }
            catch (WorkbookExportException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Workbook export {OperationId} failed safely with {FailureCode}",
                    correlation.OperationId,
                    exception.Code);
                execution.RecordFailure(exception.Code);
                return WorkbookExportHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    exception.Code,
                    exception.Message);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Workbook export {OperationId} failed without publishing a batch",
                    correlation.OperationId);
                execution.RecordFailure("workbook-export-failed");
                return WorkbookExportHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "workbook-export-failed",
                    "Workbook export failed and no workbook batch was published.");
            }
        }
    }

    private async ValueTask WriteWorksheetAsync(
        OpenXmlWriter writer,
        OperationId extractionResultId,
        IReadOnlyList<ExportOutputColumn> outputColumns,
        WorksheetWriteCounter counter,
        CancellationToken cancellationToken)
    {
        counter.ColumnCount = outputColumns.Count;
        writer.WriteStartElement(new Row { RowIndex = 1U });
        for (var columnIndex = 0; columnIndex < outputColumns.Count; columnIndex++)
        {
            ExcelWorkbookExportFoundation.WriteTextCell(
                writer,
                columnIndex + 1,
                1,
                outputColumns[columnIndex].Header);
        }

        writer.WriteEndElement();
        var rowIndex = 1;
        await foreach (var row in _rowSource.StreamRowsAsync(
                           extractionResultId,
                           counter.Definition.Worksheet.SourceSetId,
                           cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rowIndex >= ExcelWorkbookLimits.MaximumRows)
            {
                throw new WorkbookExportException(
                    "export-row-limit-exceeded",
                    $"Worksheet '{counter.Definition.Worksheet.Name}' exceeds Excel's row limit.");
            }

            rowIndex++;
            writer.WriteStartElement(new Row { RowIndex = checked((uint)rowIndex) });
            for (var columnIndex = 0; columnIndex < outputColumns.Count; columnIndex++)
            {
                var value = ProjectCell(row, outputColumns[columnIndex]);
                if (value is null)
                {
                    ExcelWorkbookExportFoundation.WriteBlankCell(
                        writer,
                        columnIndex + 1,
                        rowIndex);
                }
                else
                {
                    ExcelWorkbookExportFoundation.WriteTextCell(
                        writer,
                        columnIndex + 1,
                        rowIndex,
                        value);
                }
            }

            writer.WriteEndElement();
            counter.RowCount++;
        }
    }

    private static string? ProjectCell(ExtractionResultRow row, ExportOutputColumn column)
    {
        if (column.Kind == ExportOutputColumnKind.Metadata)
        {
            var values = row.Cells.SelectMany(cell => cell.Values).ToArray();
            return DatabaseRowMetadataProjection.GetValue(
                row.Source,
                DatabaseRowMetadataProjection.CreateRecordHierarchy(
                    values.Select(value => value.Lineage)),
                values.Select(value => new DatabaseRowMetadataValue(
                    value.DetailedIdentity,
                    value.Lineage)),
                column.MetadataField!.Value);
        }

        var cell = row.Cells.SingleOrDefault(candidate =>
            candidate.ColumnIdentity == column.OwningDatabaseColumnIdentity);
        if (cell is null)
        {
            return null;
        }

        return column.Kind == ExportOutputColumnKind.SourceId
            ? row.Source.SourceId.ToString()
            : string.Join(
                " | ",
                cell.Values.Select(value => value.Value).Distinct(StringComparer.Ordinal));
    }

    private static string ValidateOutputDirectory(string outputDirectory)
    {
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new WorkbookExportException(
                "invalid-export-directory",
                "The workbook output directory must be fully qualified.");
        }

        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (!Directory.Exists(resolved))
        {
            throw new WorkbookExportException(
                "export-directory-unavailable",
                "The workbook output directory does not exist.");
        }

        return resolved;
    }

    private static bool ExtractionResultsMatch(
        ExtractionResultSummary expected,
        ExtractionResultSummary? actual) =>
        actual is not null
        && expected.OperationId == actual.OperationId
        && DatabaseGenerationSnapshotComparer.AreEquivalent(
            expected.DatabaseGeneration,
            actual.DatabaseGeneration)
        && expected.Datasets.Count == actual.Datasets.Count
        && expected.Datasets.Zip(actual.Datasets).All(pair =>
            pair.First.SourceSetId == pair.Second.SourceSetId
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
            && pair.First.Ordinal == pair.Second.Ordinal
            && pair.First.RepeatedDataLayout == pair.Second.RepeatedDataLayout
            && pair.First.RowCount == pair.Second.RowCount
            && pair.First.ValueCount == pair.Second.ValueCount
            && DatabaseGenerationSnapshotComparer.ColumnsEqual(
                pair.First.Columns,
                pair.Second.Columns));

    private sealed class WorksheetWriteCounter(RunnableWorksheetDefinition definition)
    {
        internal RunnableWorksheetDefinition Definition { get; } = definition;
        internal int RowCount { get; set; }
        internal int ColumnCount { get; set; }
    }
}

internal interface IExtractionExportRowSource
{
    Task<ExtractionResultSummary?> ReadPublishedResultAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ExtractionResultRow> StreamRowsAsync(
        OperationId extractionResultId,
        SourceSetId sourceSetId,
        CancellationToken cancellationToken = default);
}

internal sealed class RepositoryExtractionExportRowSource(
    StructuredInformationRepository repository) : IExtractionExportRowSource
{
    public Task<ExtractionResultSummary?> ReadPublishedResultAsync(
        CancellationToken cancellationToken = default) =>
        repository.ReadPublishedExtractionResultAsync(cancellationToken);

    public IAsyncEnumerable<ExtractionResultRow> StreamRowsAsync(
        OperationId extractionResultId,
        SourceSetId sourceSetId,
        CancellationToken cancellationToken = default) =>
        repository.StreamPublishedExtractionRowsAsync(
            extractionResultId,
            sourceSetId,
            cancellationToken);
}

public sealed record WorkbookExportHostResult(
    bool Accepted,
    OperationCompletion Completion,
    WorkbookExportBatchSummary? Batch,
    IpcFailure? Failure)
{
    internal static WorkbookExportHostResult Accept(
        WorkbookExportBatchSummary batch,
        OperationCompletion completion) =>
        new(true, completion, batch, Failure: null);

    internal static WorkbookExportHostResult Reject(
        OperationCompletion completion,
        string failureCode,
        string failureDescription) =>
        new(
            false,
            completion,
            Batch: null,
            new IpcFailure(failureCode, failureDescription));
}
