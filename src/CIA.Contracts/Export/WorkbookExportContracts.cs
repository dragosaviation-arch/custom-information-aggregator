using CIA.Contracts.Operations;

namespace CIA.Contracts.Export;

public static class ExcelWorkbookLimits
{
    public const int MaximumColumns = 16_384;
    public const int MaximumRows = 1_048_576;
    public const int MaximumDataRows = MaximumRows - 1;
    public const int MaximumCellTextLength = 32_767;
}

public sealed record WorkbookExportSummary
{
    public WorkbookExportSummary(
        OperationId operationId,
        OperationId extractionResultId,
        string targetPath,
        int columnCount,
        int dataRowCount)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "A workbook export requires a UUIDv7 Operation ID.",
                nameof(operationId));
        }

        if (!OperationId.IsValid(extractionResultId.Value))
        {
            throw new ArgumentException(
                "A workbook export requires a UUIDv7 Extraction Result ID.",
                nameof(extractionResultId));
        }

        if (operationId == extractionResultId)
        {
            throw new ArgumentException(
                "A workbook export and its Extraction Result require distinct identities.",
                nameof(extractionResultId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!Path.IsPathFullyQualified(targetPath))
        {
            throw new ArgumentException(
                "A workbook export target path must be fully qualified.",
                nameof(targetPath));
        }

        if (columnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }

        if (dataRowCount is < 0 or > ExcelWorkbookLimits.MaximumDataRows)
        {
            throw new ArgumentOutOfRangeException(nameof(dataRowCount));
        }

        OperationId = operationId;
        ExtractionResultId = extractionResultId;
        TargetPath = targetPath;
        ColumnCount = columnCount;
        DataRowCount = dataRowCount;
    }

    public OperationId OperationId { get; }

    public OperationId ExtractionResultId { get; }

    public string TargetPath { get; }

    public int ColumnCount { get; }

    public int DataRowCount { get; }
}
