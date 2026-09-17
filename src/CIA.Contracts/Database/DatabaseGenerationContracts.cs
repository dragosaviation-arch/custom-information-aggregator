using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Database;

public sealed record DatabaseGenerationSummary
{
    public DatabaseGenerationSummary(
        OperationId operationId,
        IReadOnlyList<DatabaseDatasetSummary> datasets)
        : this(
            operationId,
            new DatabaseMappingSnapshot([]),
            checked(datasets.Sum(dataset => dataset.ValueCount)),
            datasets,
            checked(datasets.Sum(dataset => dataset.RowCount)),
            true)
    {
    }

    public DatabaseGenerationSummary(
        OperationId operationId,
        DatabaseMappingSnapshot mapping,
        int valueCount)
        : this(operationId, mapping, valueCount, [], 0, false)
    {
    }

    [JsonConstructor]
    public DatabaseGenerationSummary(
        OperationId operationId,
        DatabaseMappingSnapshot mapping,
        int valueCount,
        IReadOnlyList<DatabaseDatasetSummary> datasets,
        int rowCount,
        bool isHierarchyAware)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "A Database generation requires a UUIDv7 Operation ID.",
                nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(datasets);
        var datasetArray = datasets.ToArray();
        var validHierarchy = isHierarchyAware
            && datasetArray.Length > 0
            && datasetArray.All(dataset => dataset is not null)
            && datasetArray.Select(dataset => dataset.SourceSetId).Distinct().Count() == datasetArray.Length
            && datasetArray.Select(dataset => dataset.Ordinal)
                .SequenceEqual(Enumerable.Range(1, datasetArray.Length))
            && valueCount == datasetArray.Sum(dataset => dataset.ValueCount)
            && rowCount == datasetArray.Sum(dataset => dataset.RowCount)
            && mapping.Columns.Count == 0;
        var validLegacy = !isHierarchyAware
            && datasetArray.Length == 0
            && rowCount == 0
            && mapping.Columns.Count > 0;
        if (valueCount < 1 || (!validHierarchy && !validLegacy))
        {
            throw new ArgumentException("The Database generation shape is invalid.");
        }

        OperationId = operationId;
        Mapping = mapping;
        ValueCount = valueCount;
        Datasets = new ReadOnlyCollection<DatabaseDatasetSummary>(datasetArray);
        RowCount = rowCount;
        IsHierarchyAware = isHierarchyAware;
    }

    public OperationId OperationId { get; }

    public DatabaseMappingSnapshot Mapping { get; }

    public int ValueCount { get; }

    public IReadOnlyList<DatabaseDatasetSummary> Datasets { get; }

    public int RowCount { get; }

    public bool IsHierarchyAware { get; }
}

public sealed record DatabaseDatasetSummary
{
    [JsonConstructor]
    public DatabaseDatasetSummary(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        int rowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns,
        IReadOnlyList<DatabaseFieldMapping> mappings)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A Database dataset requires a Source Set ID.", nameof(sourceSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (ordinal < 1 || rowCount < 1 || valueCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowCount),
                "A published Database dataset requires positive ordinals, rows, and values.");
        }

        if (!Enum.IsDefined(repeatedDataLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(repeatedDataLayout));
        }

        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(mappings);
        var columnArray = columns.ToArray();
        var mappingArray = mappings.ToArray();
        if (columnArray.Length == 0
            || mappingArray.Length == 0
            || columnArray.Any(column => column is null
                || column.Identity.SourceSetId != sourceSetId)
            || mappingArray.Any(mapping => mapping is null
                || mapping.LogicalIdentity.SourceSetId != sourceSetId)
            || columnArray.Select(column => column.Identity).Distinct().Count()
                != columnArray.Length
            || !columnArray.Select(column => column.Ordinal)
                .SequenceEqual(Enumerable.Range(1, columnArray.Length)))
        {
            throw new ArgumentException(
                "A Database dataset requires valid typed columns and mappings.",
                nameof(columns));
        }

        SourceSetId = sourceSetId;
        DisplayName = displayName;
        Ordinal = ordinal;
        RepeatedDataLayout = repeatedDataLayout;
        RowCount = rowCount;
        ValueCount = valueCount;
        Columns = new ReadOnlyCollection<DatabaseColumnDefinition>(columnArray);
        Mappings = new ReadOnlyCollection<DatabaseFieldMapping>(mappingArray);
    }

    public SourceSetId SourceSetId { get; }

    public string DisplayName { get; }

    public int Ordinal { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }

    public int RowCount { get; }

    public int ValueCount { get; }

    public IReadOnlyList<DatabaseColumnDefinition> Columns { get; }

    public IReadOnlyList<DatabaseFieldMapping> Mappings { get; }
}
