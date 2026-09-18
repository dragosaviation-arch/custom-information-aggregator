using System.Globalization;
using System.Xml;
using CIA.Contracts.Export;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CIA.ProcessingHost.Export;

internal sealed record ExcelWorksheetWritePlan(
    string Name,
    Func<OpenXmlWriter, CancellationToken, ValueTask> WriteRowsAsync);

internal static class ExcelWorkbookExportFoundation
{
    internal static async Task WriteTemporaryWorkbookAsync(
        string temporaryPath,
        IReadOnlyList<ExcelWorksheetWritePlan> worksheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(worksheets);
        var plans = worksheets.ToArray();
        if (plans.Length == 0)
        {
            throw new WorkbookExportException(
                "empty-workbook",
                "A workbook requires at least one worksheet definition.");
        }

        if (plans.Any(plan => plan is null
                || !ExportConfigurationValidator.IsValidWorksheetName(plan.Name))
            || plans.Select(plan => plan.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != plans.Length)
        {
            throw new WorkbookExportException(
                "invalid-worksheet-definition",
                "Workbook worksheet names must be valid and unique case-insensitively.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var document = SpreadsheetDocument.Create(
            temporaryPath,
            SpreadsheetDocumentType.Workbook,
            autoSave: false);
        var workbookPart = document.AddWorkbookPart();
        var worksheetParts = new List<(WorksheetPart Part, string Name)>(plans.Length);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());
                await plan.WriteRowsAsync(writer, cancellationToken).ConfigureAwait(false);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            worksheetParts.Add((worksheetPart, plan.Name));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var workbookWriter = OpenXmlWriter.Create(workbookPart);
        workbookWriter.WriteStartElement(new Workbook());
        workbookWriter.WriteStartElement(new Sheets());
        for (var index = 0; index < worksheetParts.Count; index++)
        {
            workbookWriter.WriteElement(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetParts[index].Part),
                SheetId = checked((uint)index + 1U),
                Name = worksheetParts[index].Name
            });
        }

        workbookWriter.WriteEndElement();
        workbookWriter.WriteEndElement();
    }

    internal static void WriteTextCell(
        OpenXmlWriter writer,
        int column,
        int row,
        string text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(text);
        ValidateCellCoordinate(column, row);
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

    internal static void WriteBlankCell(OpenXmlWriter writer, int column, int row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValidateCellCoordinate(column, row);
        writer.WriteElement(new Cell
        {
            CellReference = CreateCellReference(column, row)
        });
    }

    internal static void ValidateCellText(string text, string role)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
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

    internal static string CreateCellReference(int column, int row)
    {
        ValidateCellCoordinate(column, row);
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        var remaining = column;
        while (remaining > 0)
        {
            remaining--;
            buffer[--index] = (char)('A' + remaining % 26);
            remaining /= 26;
        }

        return string.Concat(buffer[index..], row.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateCellCoordinate(int column, int row)
    {
        if (column is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            throw new WorkbookExportException(
                "export-column-limit-exceeded",
                $"An export column must be between 1 and {ExcelWorkbookLimits.MaximumColumns:N0}.");
        }

        if (row is < 1 or > ExcelWorkbookLimits.MaximumRows)
        {
            throw new WorkbookExportException(
                "export-row-limit-exceeded",
                $"An export row must be between 1 and {ExcelWorkbookLimits.MaximumRows:N0}.");
        }
    }
}

internal sealed class WorkbookPublicationScope : IDisposable
{
    private bool _published;
    private bool _disposed;

    private WorkbookPublicationScope(string finalPath)
    {
        FinalPath = ValidateTarget(finalPath);
        var targetDirectory = Path.GetDirectoryName(FinalPath)!;
        TemporaryPath = Path.Combine(
            targetDirectory,
            $".cia-export-{Guid.NewGuid():N}.incomplete");
    }

    internal string FinalPath { get; }

    internal string TemporaryPath { get; }

    internal static WorkbookPublicationScope Create(string finalPath) => new(finalPath);

    internal void Publish(
        Func<bool> tryEnterNonCancellablePublicationBoundary,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tryEnterNonCancellablePublicationBoundary);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(TemporaryPath))
        {
            throw new WorkbookExportException(
                "export-temporary-file-missing",
                "The temporary workbook is unavailable for publication.");
        }

        if (!tryEnterNonCancellablePublicationBoundary())
        {
            throw new OperationCanceledException(
                "Export was cancelled before its atomic publication boundary.",
                cancellationToken);
        }

        try
        {
            File.Move(TemporaryPath, FinalPath, overwrite: false);
        }
        catch (IOException exception) when (File.Exists(FinalPath))
        {
            throw new WorkbookExportException(
                "export-target-exists",
                "The target workbook already exists and was not overwritten.",
                exception);
        }

        _published = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_published && File.Exists(TemporaryPath))
        {
            File.Delete(TemporaryPath);
        }
    }

    private static string ValidateTarget(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (!Path.IsPathFullyQualified(finalPath)
            || !string.Equals(
                Path.GetExtension(finalPath),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbookExportException(
                "invalid-export-target",
                "The export target must be a fully qualified .xlsx path.");
        }

        var resolvedPath = Path.GetFullPath(finalPath);
        if (File.Exists(resolvedPath))
        {
            throw new WorkbookExportException(
                "export-target-exists",
                "The target workbook already exists and was not overwritten.");
        }

        var targetDirectory = Path.GetDirectoryName(resolvedPath)!;
        if (!Directory.Exists(targetDirectory))
        {
            throw new WorkbookExportException(
                "export-target-unavailable",
                "The target workbook directory does not exist.");
        }

        return resolvedPath;
    }
}

internal sealed class WorkbookExportException : Exception
{
    internal WorkbookExportException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal WorkbookExportException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}
