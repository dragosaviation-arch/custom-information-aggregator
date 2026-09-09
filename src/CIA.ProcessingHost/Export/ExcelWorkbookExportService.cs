using System.Xml;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Export;

public sealed class ExcelWorkbookExportService(
    StructuredInformationRepository repository,
    CooperativeOperationCancellation operationCancellation,
    ILogger<ExcelWorkbookExportService> logger)
{
    private const string PublicationItemId = "workbook-publication";

    public async Task<WorkbookExportHostResult> ExportAsync(
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

        var operation = operationCancellation.BeginOperation(
            correlation,
            "Export",
            "Excel workbook publication",
            [new ProcessingItemPlan(PublicationItemId)]);
        if (!operation.TryStartItem(PublicationItemId, out var execution))
        {
            return WorkbookExportHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "export-not-started",
                "Export could not enter its processing boundary.");
        }

        using (execution)
        using (var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   execution!.CancellationToken))
        {
            try
            {
                var summary = await WriteWorkbookAsync(
                        correlation,
                        extractionResult,
                        configuration,
                        targetPath,
                        exportCancellation.Token,
                        operation.TryEnterNonCancellableCommitBoundary)
                    .ConfigureAwait(false);
                execution.CommitCompletedResult();
                return WorkbookExportHostResult.Accept(summary, operation.Complete());
            }
            catch (OperationCanceledException)
                when (exportCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    operationCancellation.RequestCancellation(correlation.OperationId);
                }

                execution.StopBeforeCommit();
                return WorkbookExportHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "export-cancelled",
                    "Export was cancelled before workbook publication.");
            }
            catch (WorkbookExportException exception)
            {
                logger.LogWarning(
                    exception,
                    "Export {OperationId} failed without publishing a workbook",
                    correlation.OperationId);
                execution.RecordFailure(exception.Code);
                return WorkbookExportHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    exception.Code,
                    exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Export {OperationId} failed without publishing a workbook",
                    correlation.OperationId);
                execution.RecordFailure("export-failed");
                return WorkbookExportHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "export-failed",
                    "The Excel workbook could not be generated.");
            }
        }
    }

    private async Task<WorkbookExportSummary> WriteWorkbookAsync(
        OperationCorrelation correlation,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string targetPath,
        CancellationToken cancellationToken,
        Func<bool> tryEnterPublicationBoundary)
    {
        var resolvedTargetPath = ValidateRequest(extractionResult, configuration, targetPath);
        var publishedExtraction = await repository
            .ReadPublishedExtractionResultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!ExtractionResultsEqual(extractionResult, publishedExtraction))
        {
            throw new WorkbookExportException(
                "extraction-not-current",
                "Export requires the captured Extraction Result to remain current.");
        }

        if (File.Exists(resolvedTargetPath))
        {
            throw new WorkbookExportException(
                "export-target-exists",
                "The target workbook already exists and was not overwritten.");
        }

        var targetDirectory = Path.GetDirectoryName(resolvedTargetPath)!;
        if (!Directory.Exists(targetDirectory))
        {
            throw new WorkbookExportException(
                "export-target-unavailable",
                "The target workbook directory does not exist.");
        }

        var outputColumns = configuration.CreateIncludedOutputColumns();
        var includedFields = configuration.Fields
            .Where(field => field.IsValueIncluded)
            .Select(field => field.DatabaseFieldIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".cia-export-{Guid.NewGuid():N}.incomplete");
        var published = false;

        try
        {
            var dataRowCount = await WriteTemporaryWorkbookAsync(
                    temporaryPath,
                    extractionResult.OperationId,
                    outputColumns,
                    includedFields,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!tryEnterPublicationBoundary())
            {
                throw new OperationCanceledException(
                    "Export was cancelled before its atomic publication boundary.",
                    cancellationToken);
            }

            try
            {
                File.Move(temporaryPath, resolvedTargetPath, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(resolvedTargetPath))
            {
                throw new WorkbookExportException(
                    "export-target-exists",
                    "The target workbook already exists and was not overwritten.",
                    exception);
            }

            published = true;
            return new WorkbookExportSummary(
                correlation.OperationId,
                extractionResult.OperationId,
                resolvedTargetPath,
                outputColumns.Count,
                dataRowCount);
        }
        finally
        {
            if (!published && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<int> WriteTemporaryWorkbookAsync(
        string temporaryPath,
        OperationId extractionResultId,
        IReadOnlyList<ExportOutputColumn> outputColumns,
        HashSet<string> includedFields,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Create(
            temporaryPath,
            SpreadsheetDocumentType.Workbook,
            autoSave: false);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var dataRowCount = 0;

        using (var worksheetWriter = OpenXmlWriter.Create(worksheetPart))
        {
            worksheetWriter.WriteStartElement(new Worksheet());
            worksheetWriter.WriteStartElement(new SheetData());
            WriteHeaderRow(worksheetWriter, outputColumns);

            var currentValues = new Dictionary<string, ExtractionResultExportValue>(
                StringComparer.Ordinal);
            await foreach (var value in repository
                               .StreamPublishedExtractionValuesForExportAsync(
                                   extractionResultId,
                                   cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!includedFields.Contains(value.DatabaseFieldName))
                {
                    continue;
                }

                if (value.FieldValueOrdinal > ExcelWorkbookLimits.MaximumDataRows)
                {
                    throw new WorkbookExportException(
                        "export-row-limit-exceeded",
                        $"The workbook exceeds Excel's {ExcelWorkbookLimits.MaximumRows:N0}-row worksheet limit.");
                }

                if (dataRowCount != 0 && value.FieldValueOrdinal != dataRowCount)
                {
                    WriteDataRow(
                        worksheetWriter,
                        dataRowCount + 1,
                        outputColumns,
                        currentValues);
                    currentValues.Clear();
                }

                dataRowCount = value.FieldValueOrdinal;
                if (!currentValues.TryAdd(value.DatabaseFieldName, value))
                {
                    throw new WorkbookExportException(
                        "invalid-extraction-result",
                        "The Extraction Result contains duplicate field values at one export ordinal.");
                }
            }

            if (dataRowCount > 0)
            {
                WriteDataRow(
                    worksheetWriter,
                    dataRowCount + 1,
                    outputColumns,
                    currentValues);
            }

            worksheetWriter.WriteEndElement();
            worksheetWriter.WriteEndElement();
        }

        if (dataRowCount == 0)
        {
            throw new WorkbookExportException(
                "empty-export",
                "The selected export fields contain no values.");
        }

        using var workbookWriter = OpenXmlWriter.Create(workbookPart);
        workbookWriter.WriteStartElement(new Workbook());
        workbookWriter.WriteStartElement(new Sheets());
        workbookWriter.WriteElement(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1U,
                Name = "Results"
            });
        workbookWriter.WriteEndElement();
        workbookWriter.WriteEndElement();

        return dataRowCount;
    }

    private static string ValidateRequest(
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string targetPath)
    {
        if (!Path.IsPathFullyQualified(targetPath)
            || !string.Equals(Path.GetExtension(targetPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbookExportException(
                "invalid-export-target",
                "The export target must be a fully qualified .xlsx path.");
        }

        var configuredFieldIdentities = configuration.Fields
            .Select(field => field.DatabaseFieldIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var extractionFieldIdentities = extractionResult.DatabaseGeneration.Mapping.Columns
            .Select(column => column.DatabaseTagName)
            .ToHashSet(StringComparer.Ordinal);
        if (!configuredFieldIdentities.SetEquals(extractionFieldIdentities))
        {
            throw new WorkbookExportException(
                "export-configuration-mismatch",
                "The export configuration does not match the captured Extraction Result.");
        }

        var outputColumns = configuration.CreateIncludedOutputColumns();
        if (outputColumns.Count == 0)
        {
            throw new WorkbookExportException(
                "empty-export-configuration",
                "At least one value field must be included in the workbook.");
        }

        if (outputColumns.Count > ExcelWorkbookLimits.MaximumColumns)
        {
            throw new WorkbookExportException(
                "export-column-limit-exceeded",
                $"The workbook exceeds Excel's {ExcelWorkbookLimits.MaximumColumns:N0}-column worksheet limit.");
        }

        foreach (var column in outputColumns)
        {
            ValidateCellText(column.Header, "header");
        }

        return Path.GetFullPath(targetPath);
    }

    private static bool ExtractionResultsEqual(
        ExtractionResultSummary expected,
        ExtractionResultSummary? actual)
    {
        return actual is not null
            && expected.OperationId == actual.OperationId
            && expected.ValueCount == actual.ValueCount
            && expected.DatabaseGeneration.OperationId == actual.DatabaseGeneration.OperationId
            && expected.DatabaseGeneration.Mapping.Columns.Count
                == actual.DatabaseGeneration.Mapping.Columns.Count
            && expected.DatabaseGeneration.Mapping.Columns
                .Zip(actual.DatabaseGeneration.Mapping.Columns)
                .All(pair => string.Equals(
                        pair.First.DatabaseTagName,
                        pair.Second.DatabaseTagName,
                        StringComparison.Ordinal)
                    && pair.First.SourceInformationTypes.SequenceEqual(
                        pair.Second.SourceInformationTypes,
                        StringComparer.Ordinal));
    }

    private static void WriteHeaderRow(
        OpenXmlWriter writer,
        IReadOnlyList<ExportOutputColumn> columns)
    {
        writer.WriteStartElement(new Row { RowIndex = 1U });
        for (var index = 0; index < columns.Count; index++)
        {
            WriteTextCell(writer, index + 1, 1, columns[index].Header);
        }

        writer.WriteEndElement();
    }

    private static void WriteDataRow(
        OpenXmlWriter writer,
        int worksheetRow,
        IReadOnlyList<ExportOutputColumn> columns,
        IReadOnlyDictionary<string, ExtractionResultExportValue> values)
    {
        writer.WriteStartElement(new Row { RowIndex = (uint)worksheetRow });
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (!values.TryGetValue(column.OwningDatabaseFieldIdentity, out var value))
            {
                writer.WriteElement(
                    new Cell
                    {
                        CellReference = CreateCellReference(index + 1, worksheetRow)
                    });
                continue;
            }

            var text = column.Kind == ExportOutputColumnKind.Value
                ? value.Value
                : value.SourceId.ToString();
            WriteTextCell(writer, index + 1, worksheetRow, text);
        }

        writer.WriteEndElement();
    }

    private static void WriteTextCell(
        OpenXmlWriter writer,
        int column,
        int row,
        string text)
    {
        ValidateCellText(text, "value");
        writer.WriteElement(
            new Cell(
                new InlineString(
                    new Text(text)
                    {
                        Space = SpaceProcessingModeValues.Preserve
                    }))
            {
                CellReference = CreateCellReference(column, row),
                DataType = CellValues.InlineString
            });
    }

    private static void ValidateCellText(string text, string role)
    {
        if (text.Length > ExcelWorkbookLimits.MaximumCellTextLength)
        {
            throw new WorkbookExportException(
                "export-cell-limit-exceeded",
                $"An export {role} exceeds Excel's {ExcelWorkbookLimits.MaximumCellTextLength:N0}-character cell limit.");
        }

        try
        {
            XmlConvert.VerifyXmlChars(text);
        }
        catch (XmlException exception)
        {
            throw new WorkbookExportException(
                "invalid-export-text",
                $"An export {role} contains text that cannot be represented in an Excel workbook.",
                exception);
        }
    }

    private static string CreateCellReference(int column, int row)
    {
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        var remaining = column;
        while (remaining > 0)
        {
            remaining--;
            buffer[--index] = (char)('A' + remaining % 26);
            remaining /= 26;
        }

        return string.Concat(buffer[index..], row.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

internal sealed class WorkbookExportException : Exception
{
    public WorkbookExportException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public WorkbookExportException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
