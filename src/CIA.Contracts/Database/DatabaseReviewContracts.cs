using System.Collections.ObjectModel;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Database;

public static class DatabaseReviewLimits
{
    public const int MaximumRowsPerPage = 100;
}

public sealed record DatabaseReviewValue
{
    public DatabaseReviewValue(
        int columnOrdinal,
        string value,
        string sourceInformationType,
        SourceId sourceId)
    {
        if (columnOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columnOrdinal),
                "A Database review value requires a positive column ordinal.");
        }

        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInformationType);
        if (!SourceId.IsValid(sourceId.Value))
        {
            throw new ArgumentException(
                "A Database review value requires a valid Source ID.",
                nameof(sourceId));
        }

        ColumnOrdinal = columnOrdinal;
        Value = value;
        SourceInformationType = sourceInformationType;
        SourceId = sourceId;
    }

    public int ColumnOrdinal { get; }

    public string Value { get; }

    public string SourceInformationType { get; }

    public SourceId SourceId { get; }
}

public sealed record DatabaseReviewColumn
{
    public DatabaseReviewColumn(
        string databaseTagName,
        int totalValueCount,
        IReadOnlyList<DatabaseReviewValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTagName);
        ArgumentNullException.ThrowIfNull(values);

        var valueArray = values.ToArray();
        if (totalValueCount < 0
            || valueArray.Length > DatabaseReviewLimits.MaximumRowsPerPage
            || valueArray.Any(value => value is null)
            || valueArray.Any(value => value.ColumnOrdinal > totalValueCount)
            || valueArray.Select(value => value.ColumnOrdinal).Distinct().Count()
                != valueArray.Length
            || !valueArray
                .Select(value => value.ColumnOrdinal)
                .SequenceEqual(valueArray.Select(value => value.ColumnOrdinal).Order()))
        {
            throw new ArgumentException(
                "A Database review column contains invalid or unordered values.",
                nameof(values));
        }

        DatabaseTagName = databaseTagName;
        TotalValueCount = totalValueCount;
        Values = new ReadOnlyCollection<DatabaseReviewValue>(valueArray);
    }

    public string DatabaseTagName { get; }

    public int TotalValueCount { get; }

    public IReadOnlyList<DatabaseReviewValue> Values { get; }
}

public sealed record DatabaseReviewPage
{
    public DatabaseReviewPage(
        OperationId generationId,
        int startRowOrdinal,
        int requestedRowCount,
        int totalMappedValueCount,
        IReadOnlyList<DatabaseReviewColumn> columns)
    {
        if (!OperationId.IsValid(generationId.Value))
        {
            throw new ArgumentException(
                "A Database review page requires a UUIDv7 generation ID.",
                nameof(generationId));
        }

        if (startRowOrdinal < 1
            || startRowOrdinal > int.MaxValue - DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startRowOrdinal),
                "A Database review page requires a positive starting row ordinal.");
        }

        if (requestedRowCount is < 1 or > DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedRowCount),
                $"A Database review page may request 1 to {DatabaseReviewLimits.MaximumRowsPerPage} rows.");
        }

        ArgumentNullException.ThrowIfNull(columns);
        var columnArray = columns.ToArray();
        if (columnArray.Length == 0 || columnArray.Any(column => column is null))
        {
            throw new ArgumentException(
                "A Database review page requires valid dynamic columns.",
                nameof(columns));
        }

        var mappedValueCount = columnArray.Sum(column => (long)column.TotalValueCount);
        if (totalMappedValueCount < 1
            || columnArray.Select(column => column.DatabaseTagName)
                .Distinct(StringComparer.Ordinal).Count() != columnArray.Length
            || mappedValueCount != totalMappedValueCount
            || columnArray.SelectMany(column => column.Values).Any(value =>
                value.ColumnOrdinal < startRowOrdinal
                || value.ColumnOrdinal >= startRowOrdinal + requestedRowCount))
        {
            throw new ArgumentException(
                "A Database review page is inconsistent with its published generation.",
                nameof(columns));
        }

        GenerationId = generationId;
        StartRowOrdinal = startRowOrdinal;
        RequestedRowCount = requestedRowCount;
        TotalMappedValueCount = totalMappedValueCount;
        Columns = new ReadOnlyCollection<DatabaseReviewColumn>(columnArray);
    }

    public OperationId GenerationId { get; }

    public int StartRowOrdinal { get; }

    public int RequestedRowCount { get; }

    public int TotalMappedValueCount { get; }

    public IReadOnlyList<DatabaseReviewColumn> Columns { get; }

    public int TotalPresentationRowCount => Columns.Max(column => column.TotalValueCount);
}
