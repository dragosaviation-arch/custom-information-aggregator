using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Export;

public static class ExcelWorkbookLimits
{
    public const int MaximumColumns = 16_384;
    public const int MaximumRows = 1_048_576;
    public const int MaximumDataRows = MaximumRows - 1;
    public const int MaximumCellTextLength = 32_767;
}

public enum WorkbookPublicationDisposition
{
    CreateNew = 1,
    OverwriteExisting = 2
}

public sealed record WorkbookPublicationTarget
{
    [JsonConstructor]
    public WorkbookPublicationTarget(
        WorkbookDefinitionId workbookDefinitionId,
        string finalPath,
        WorkbookPublicationDisposition disposition)
    {
        if (!WorkbookDefinitionId.IsValid(workbookDefinitionId.Value))
        {
            throw new ArgumentException(
                "A workbook publication target requires a stable workbook identity.",
                nameof(workbookDefinitionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (!Path.IsPathFullyQualified(finalPath)
            || !string.Equals(
                Path.GetExtension(finalPath),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase)
            || !Enum.IsDefined(disposition))
        {
            throw new ArgumentException("A workbook publication target is invalid.");
        }

        WorkbookDefinitionId = workbookDefinitionId;
        FinalPath = Path.GetFullPath(finalPath);
        Disposition = disposition;
    }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public string FinalPath { get; }

    public WorkbookPublicationDisposition Disposition { get; }
}

public sealed record WorkbookPublicationPlan
{
    [JsonConstructor]
    public WorkbookPublicationPlan(
        string outputDirectory,
        IReadOnlyList<WorkbookPublicationTarget> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(targets);
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException(
                "A workbook publication plan requires a fully qualified output directory.",
                nameof(outputDirectory));
        }

        var resolvedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(outputDirectory));
        var targetArray = targets.ToArray();
        if (targetArray.Length == 0
            || targetArray.Any(target => target is null
                || !string.Equals(
                    Path.GetDirectoryName(target.FinalPath),
                    resolvedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            || targetArray.Select(target => target.WorkbookDefinitionId).Distinct().Count()
                != targetArray.Length
            || targetArray.Select(target => target.FinalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetArray.Length)
        {
            throw new ArgumentException(
                "A workbook publication plan requires unique workbook identities and targets in one output directory.");
        }

        OutputDirectory = resolvedDirectory;
        Targets = new ReadOnlyCollection<WorkbookPublicationTarget>(targetArray);
    }

    public string OutputDirectory { get; }

    public IReadOnlyList<WorkbookPublicationTarget> Targets { get; }
}

public sealed record WorkbookExportWorksheetSummary
{
    [JsonConstructor]
    public WorkbookExportWorksheetSummary(
        WorksheetDefinitionId worksheetDefinitionId,
        SourceSetId sourceSetId,
        string name,
        int order,
        int rowCount,
        int columnCount)
    {
        if (!WorksheetDefinitionId.IsValid(worksheetDefinitionId.Value)
            || !SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "An exported worksheet requires stable worksheet and Source Set identities.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ExportConfigurationValidator.IsValidWorksheetName(name)
            || order < 1
            || rowCount is < 0 or > ExcelWorkbookLimits.MaximumDataRows
            || columnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            throw new ArgumentException("An exported worksheet summary is invalid.");
        }

        WorksheetDefinitionId = worksheetDefinitionId;
        SourceSetId = sourceSetId;
        Name = name;
        Order = order;
        RowCount = rowCount;
        ColumnCount = columnCount;
    }

    public WorksheetDefinitionId WorksheetDefinitionId { get; }
    public SourceSetId SourceSetId { get; }
    public string Name { get; }
    public int Order { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }
}

public sealed record WorkbookExportFileSummary
{
    [JsonConstructor]
    public WorkbookExportFileSummary(
        WorkbookDefinitionId workbookDefinitionId,
        string finalPath,
        int order,
        IReadOnlyList<WorkbookExportWorksheetSummary> worksheets)
    {
        if (!WorkbookDefinitionId.IsValid(workbookDefinitionId.Value))
        {
            throw new ArgumentException(
                "An exported workbook requires a stable workbook identity.",
                nameof(workbookDefinitionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(worksheets);
        var worksheetArray = worksheets.ToArray();
        if (!Path.IsPathFullyQualified(finalPath)
            || !string.Equals(Path.GetExtension(finalPath), ".xlsx", StringComparison.OrdinalIgnoreCase)
            || order < 1
            || worksheetArray.Length == 0
            || worksheetArray.Any(worksheet => worksheet is null)
            || worksheetArray.Select(worksheet => worksheet.WorksheetDefinitionId).Distinct().Count()
                != worksheetArray.Length
            || worksheetArray.Select(worksheet => worksheet.Order).Distinct().Count()
                != worksheetArray.Length
            || !worksheetArray.Select(worksheet => worksheet.Order)
                .SequenceEqual(worksheetArray.Select(worksheet => worksheet.Order).Order()))
        {
            throw new ArgumentException("An exported workbook summary is invalid.");
        }

        WorkbookDefinitionId = workbookDefinitionId;
        FinalPath = Path.GetFullPath(finalPath);
        Order = order;
        Worksheets = new ReadOnlyCollection<WorkbookExportWorksheetSummary>(worksheetArray);
    }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }
    public string FinalPath { get; }
    public int Order { get; }
    public IReadOnlyList<WorkbookExportWorksheetSummary> Worksheets { get; }
}

public sealed record WorkbookExportBatchSummary
{
    [JsonConstructor]
    public WorkbookExportBatchSummary(
        OperationId operationId,
        OperationId extractionResultId,
        string outputDirectory,
        IReadOnlyList<WorkbookExportFileSummary> workbooks)
    {
        if (!OperationId.IsValid(operationId.Value)
            || !OperationId.IsValid(extractionResultId.Value)
            || operationId == extractionResultId)
        {
            throw new ArgumentException(
                "A workbook export batch requires distinct UUIDv7 operation identities.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(workbooks);
        var workbookArray = workbooks.ToArray();
        var resolvedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(outputDirectory));
        if (!Path.IsPathFullyQualified(outputDirectory)
            || workbookArray.Length == 0
            || workbookArray.Any(workbook => workbook is null
                || !string.Equals(
                    Path.GetDirectoryName(workbook.FinalPath),
                    resolvedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            || workbookArray.Select(workbook => workbook.WorkbookDefinitionId).Distinct().Count()
                != workbookArray.Length
            || workbookArray.Select(workbook => workbook.FinalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != workbookArray.Length
            || workbookArray.Select(workbook => workbook.Order).Distinct().Count()
                != workbookArray.Length
            || !workbookArray.Select(workbook => workbook.Order)
                .SequenceEqual(workbookArray.Select(workbook => workbook.Order).Order()))
        {
            throw new ArgumentException("A workbook export batch summary is invalid.");
        }

        OperationId = operationId;
        ExtractionResultId = extractionResultId;
        OutputDirectory = resolvedDirectory;
        Workbooks = new ReadOnlyCollection<WorkbookExportFileSummary>(workbookArray);
    }

    public OperationId OperationId { get; }
    public OperationId ExtractionResultId { get; }
    public string OutputDirectory { get; }
    public IReadOnlyList<WorkbookExportFileSummary> Workbooks { get; }
}
