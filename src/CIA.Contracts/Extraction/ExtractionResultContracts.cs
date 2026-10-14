using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Extraction;

public sealed record ExtractionResultSummary
{
    [JsonConstructor]
    public ExtractionResultSummary(
        OperationId operationId,
        DatabaseGenerationSummary databaseGeneration,
        IReadOnlyList<ExtractionDatasetSummary> datasets)
    {
        if (!OperationId.IsValid(operationId.Value)
            || operationId == databaseGeneration?.OperationId)
        {
            throw new ArgumentException(
                "An Extraction Result requires a distinct UUIDv7 Operation ID.",
                nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(databaseGeneration);
        ArgumentNullException.ThrowIfNull(datasets);
        var datasetArray = datasets.ToArray();
        var outputSetBacked = databaseGeneration.Datasets.All(dataset => dataset.OutputSetId is not null);
        if (!databaseGeneration.IsHierarchyAware
            || datasetArray.Length != databaseGeneration.Datasets.Count
            || datasetArray.Any(dataset => dataset is null)
            || !datasetArray.Select(dataset => dataset.Ordinal)
                .SequenceEqual(Enumerable.Range(1, datasetArray.Length))
            || (outputSetBacked
                ? datasetArray.Any(dataset => dataset.OutputSetId is null)
                    || datasetArray.Select(dataset => dataset.OutputSetId).Distinct().Count()
                        != datasetArray.Length
                : datasetArray.Any(dataset => dataset.OutputSetId is not null)
                    || datasetArray.Select(dataset => dataset.SourceSetId).Distinct().Count()
                        != datasetArray.Length))
        {
            throw new ArgumentException(
                "An Extraction Result requires every hierarchy-aware Database dataset.",
                nameof(datasets));
        }

        foreach (var dataset in datasetArray)
        {
            var basis = outputSetBacked
                ? databaseGeneration.Datasets.SingleOrDefault(candidate =>
                    candidate.OutputSetId == dataset.OutputSetId)
                : databaseGeneration.Datasets.SingleOrDefault(candidate =>
                    candidate.SourceSetId == dataset.SourceSetId);
            if (basis is null
                || dataset.ParentSourceSetId != basis.ParentSourceSetId
                || dataset.Ordinal != basis.Ordinal
                || !string.Equals(dataset.DisplayName, basis.DisplayName, StringComparison.Ordinal)
                || dataset.RepeatedDataLayout != basis.RepeatedDataLayout
                || dataset.RowCount > basis.RowCount
                || dataset.ValueCount > basis.ValueCount
                || !DatabaseGenerationSnapshotComparer.ColumnsEqual(
                    dataset.Columns,
                    basis.Columns)
                || !FamiliesMatchBasis(dataset.RowFamilies, basis.RowFamilies))
            {
                throw new ArgumentException(
                    "An Extraction Result dataset does not match its Database basis.",
                    nameof(datasets));
            }
        }

        OperationId = operationId;
        DatabaseGeneration = databaseGeneration;
        Datasets = new ReadOnlyCollection<ExtractionDatasetSummary>(datasetArray);
    }

    public OperationId OperationId { get; }

    public DatabaseGenerationSummary DatabaseGeneration { get; }

    public IReadOnlyList<ExtractionDatasetSummary> Datasets { get; }

    public int RowCount => checked(Datasets.Sum(dataset => dataset.RowCount));

    public int ValueCount => checked(Datasets.Sum(dataset => dataset.ValueCount));

    public bool IsHierarchyAware => true;

    [JsonIgnore]
    public bool IsRowFamilyAware => Datasets.SelectMany(dataset => dataset.RowFamilies)
        .Any(family => !string.Equals(family.AnchorPath, "/legacy", StringComparison.Ordinal));

    private static bool FamiliesMatchBasis(
        IReadOnlyList<ExtractionRowFamilySummary> extracted,
        IReadOnlyList<DatabaseRowFamilySummary> database)
    {
        return extracted.Count == database.Count
            && extracted.Zip(database).All(pair =>
                pair.First.Id == pair.Second.Id
                && pair.First.ParentRowFamilyId == pair.Second.ParentRowFamilyId
                && pair.First.Ordinal == pair.Second.Ordinal
                && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
                && pair.First.DatabaseRowCount == pair.Second.RowCount
                && pair.First.RowCount <= pair.Second.RowCount
                && pair.First.ExcludedRowCount == pair.Second.RowCount - pair.First.RowCount
                && pair.First.ValueCount <= pair.Second.ValueCount
                && DatabaseGenerationSnapshotComparer.ColumnsEqual(
                    pair.First.Columns,
                    pair.Second.Columns));
    }
}

public sealed record ExtractionDatasetSummary
{
    public ExtractionDatasetSummary(
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        int rowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns,
        IReadOnlyList<ExtractionRowFamilySummary> rowFamilies)
        : this(
            parentSourceSetId,
            displayName,
            ordinal,
            repeatedDataLayout,
            rowCount,
            valueCount,
            columns,
            rowFamilies,
            outputSetId)
    {
    }

    public ExtractionDatasetSummary(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        int rowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns)
        : this(
            sourceSetId,
            displayName,
            ordinal,
            repeatedDataLayout,
            rowCount,
            valueCount,
            columns,
            [CreateLegacyFamily(sourceSetId, rowCount, valueCount, columns)])
    {
    }

    [JsonConstructor]
    public ExtractionDatasetSummary(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        int rowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns,
        IReadOnlyList<ExtractionRowFamilySummary> rowFamilies,
        OutputSetId? outputSetId = null)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "An Extraction dataset requires a Source Set ID.",
                nameof(sourceSetId));
        }

        if (outputSetId is { } id && id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An Extraction Output Set requires an identity.",
                nameof(outputSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (ordinal < 1 || rowCount < 0 || valueCount < 0 || !Enum.IsDefined(repeatedDataLayout))
        {
            throw new ArgumentException("An Extraction dataset summary is invalid.");
        }

        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rowFamilies);
        var columnArray = columns.ToArray();
        var familyArray = rowFamilies.ToArray();
        if (columnArray.Length == 0
            || columnArray.Any(column => column is null
                || column.Identity.SourceSetId != sourceSetId)
            || columnArray.Select(column => column.Identity).Distinct().Count()
                != columnArray.Length
            || !columnArray.Select(column => column.Ordinal)
                .SequenceEqual(Enumerable.Range(1, columnArray.Length))
            || familyArray.Length == 0
            || familyArray.Any(family => family is null || family.Id.SourceSetId != sourceSetId)
            || familyArray.Select(family => family.Id).Distinct().Count() != familyArray.Length
            || !familyArray.Select(family => family.Ordinal)
                .SequenceEqual(Enumerable.Range(1, familyArray.Length))
            || familyArray.Sum(family => family.RowCount) != rowCount
            || familyArray.Sum(family => family.ValueCount) != valueCount)
        {
            throw new ArgumentException(
                "An Extraction dataset requires its typed Database columns.",
                nameof(columns));
        }

        SourceSetId = sourceSetId;
        OutputSetId = outputSetId;
        DisplayName = displayName;
        Ordinal = ordinal;
        RepeatedDataLayout = repeatedDataLayout;
        RowCount = rowCount;
        ValueCount = valueCount;
        Columns = new ReadOnlyCollection<DatabaseColumnDefinition>(columnArray);
        RowFamilies = new ReadOnlyCollection<ExtractionRowFamilySummary>(familyArray);
    }

    public SourceSetId SourceSetId { get; }

    public OutputSetId? OutputSetId { get; }

    [JsonIgnore]
    public SourceSetId ParentSourceSetId => SourceSetId;

    public string DisplayName { get; }

    public int Ordinal { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }

    public int RowCount { get; }

    public int ValueCount { get; }

    public IReadOnlyList<DatabaseColumnDefinition> Columns { get; }

    public IReadOnlyList<ExtractionRowFamilySummary> RowFamilies { get; }

    private static ExtractionRowFamilySummary CreateLegacyFamily(
        SourceSetId sourceSetId,
        int rowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns) =>
        new(
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/legacy"),
            parentRowFamilyId: null,
            ordinal: 1,
            displayName: "Root",
            databaseRowCount: rowCount,
            rowCount,
            excludedRowCount: 0,
            valueCount,
            columns);
}

public sealed record ExtractionRowFamilySummary
{
    [JsonConstructor]
    public ExtractionRowFamilySummary(
        RowFamilyId id,
        RowFamilyId? parentRowFamilyId,
        int ordinal,
        string displayName,
        int databaseRowCount,
        int rowCount,
        int excludedRowCount,
        int valueCount,
        IReadOnlyList<DatabaseColumnDefinition> columns)
    {
        _ = new RowFamilyDefinition(id, parentRowFamilyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(columns);
        var columnArray = columns.ToArray();
        if (ordinal < 1 || databaseRowCount < 0 || rowCount < 0 || excludedRowCount < 0
            || rowCount + excludedRowCount != databaseRowCount || valueCount < 0
            || (rowCount == 0) != (valueCount == 0)
            || columnArray.Length == 0
            || columnArray.Any(column => column is null || column.Identity.RowFamilyId != id)
            || columnArray.Select(column => column.Identity).Distinct().Count() != columnArray.Length
            || !columnArray.Select(column => column.Ordinal)
                .SequenceEqual(Enumerable.Range(1, columnArray.Length)))
        {
            throw new ArgumentException("An Extraction Row Family summary is inconsistent.");
        }

        Id = id;
        ParentRowFamilyId = parentRowFamilyId;
        Ordinal = ordinal;
        DisplayName = displayName.Trim();
        DatabaseRowCount = databaseRowCount;
        RowCount = rowCount;
        ExcludedRowCount = excludedRowCount;
        ValueCount = valueCount;
        Columns = new ReadOnlyCollection<DatabaseColumnDefinition>(columnArray);
    }

    public RowFamilyId Id { get; }

    public RowFamilyKind Kind => Id.Kind;

    public RowFamilyId? ParentRowFamilyId { get; }

    public string AnchorPath => Id.AnchorPath;

    public int Ordinal { get; }

    public string DisplayName { get; }

    public int DatabaseRowCount { get; }

    public int RowCount { get; }

    public int ExcludedRowCount { get; }

    public int ValueCount { get; }

    public IReadOnlyList<DatabaseColumnDefinition> Columns { get; }
}

public sealed record ExtractionResultValue
{
    [JsonConstructor]
    public ExtractionResultValue(
        int ordinal,
        string value,
        DiscoveryInformationIdentity detailedIdentity,
        SourceId sourceId,
        DatabaseLineageEvidence lineage,
        DatabaseRepeatCoordinatePath repeatCoordinates)
    {
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(detailedIdentity);
        if (!SourceId.IsValid(sourceId.Value))
        {
            throw new ArgumentException(
                "An extracted value requires a valid Source ID.",
                nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(repeatCoordinates);
        Ordinal = ordinal;
        Value = value;
        DetailedIdentity = detailedIdentity;
        SourceId = sourceId;
        Lineage = lineage;
        RepeatCoordinates = repeatCoordinates;
    }

    public ExtractionResultValue(
        int ordinal,
        string value,
        DiscoveryInformationIdentity detailedIdentity,
        SourceId sourceId,
        DatabaseLineageEvidence lineage,
        DatabaseRepeatCoordinatePath repeatCoordinates,
        RowFamilyId originatingRowFamilyId)
        : this(ordinal, value, detailedIdentity, sourceId, lineage, repeatCoordinates)
    {
        if (originatingRowFamilyId.SourceSetId != detailedIdentity.SourceSetId)
        {
            throw new ArgumentException(
                "An extracted value's originating Row Family must belong to its Source Set.",
                nameof(originatingRowFamilyId));
        }

        OriginatingRowFamilyId = originatingRowFamilyId;
    }

    public int Ordinal { get; }

    public string Value { get; }

    public DiscoveryInformationIdentity DetailedIdentity { get; }

    public SourceId SourceId { get; }

    public DatabaseLineageEvidence Lineage { get; }

    public DatabaseRepeatCoordinatePath RepeatCoordinates { get; }

    public RowFamilyId? OriginatingRowFamilyId { get; init; }

    [JsonIgnore]
    public string SourceInformationType => DetailedIdentity.InformationType;
}

public sealed record ExtractionResultCell
{
    [JsonConstructor]
    public ExtractionResultCell(
        DatabaseColumnIdentity columnIdentity,
        string effectiveName,
        bool hasConflict,
        IReadOnlyList<ExtractionResultValue> values)
    {
        ArgumentNullException.ThrowIfNull(columnIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveName);
        ArgumentNullException.ThrowIfNull(values);
        var valueArray = values.ToArray();
        if (valueArray.Length == 0
            || valueArray.Any(value => value is null
                || value.DetailedIdentity.SourceSetId != columnIdentity.SourceSetId
                || !value.RepeatCoordinates.Equals(columnIdentity.RepeatCoordinates))
            || !valueArray.Select(value => value.Ordinal)
                .SequenceEqual(Enumerable.Range(1, valueArray.Length))
            || hasConflict != valueArray.Select(value => value.Value)
                .Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            throw new ArgumentException(
                "An Extraction cell requires ordered, consistent underlying values.",
                nameof(values));
        }

        ColumnIdentity = columnIdentity;
        EffectiveName = effectiveName;
        HasConflict = hasConflict;
        Values = new ReadOnlyCollection<ExtractionResultValue>(valueArray);
    }

    public DatabaseColumnIdentity ColumnIdentity { get; }

    public string EffectiveName { get; }

    public bool HasConflict { get; }

    public IReadOnlyList<ExtractionResultValue> Values { get; }
}

public sealed record ExtractionResultRow
{
    [JsonConstructor]
    public ExtractionResultRow(
        int ordinal,
        int databaseRowOrdinal,
        int sourceRowOrdinal,
        string recordIdentity,
        DatabaseSourceMetadata source,
        IReadOnlyList<ExtractionResultCell> cells)
    {
        if (ordinal < 1 || databaseRowOrdinal < 1 || sourceRowOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordIdentity);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cells);
        var cellArray = cells.ToArray();
        if (cellArray.Length == 0
            || cellArray.Any(cell => cell is null
                || cell.ColumnIdentity.SourceSetId != source.SourceSetId
                || cell.Values.Any(value => value.SourceId != source.SourceId))
            || cellArray.Select(cell => cell.ColumnIdentity).Distinct().Count()
                != cellArray.Length)
        {
            throw new ArgumentException(
                "An Extraction row must remain within one Source Set and source file.",
                nameof(cells));
        }

        Ordinal = ordinal;
        DatabaseRowOrdinal = databaseRowOrdinal;
        SourceRowOrdinal = sourceRowOrdinal;
        RecordIdentity = recordIdentity;
        Source = source;
        Cells = new ReadOnlyCollection<ExtractionResultCell>(cellArray);
    }

    public ExtractionResultRow(
        int ordinal,
        int databaseRowOrdinal,
        int sourceRowOrdinal,
        DatabaseRecordIdentity semanticIdentity,
        DatabaseSourceMetadata source,
        IReadOnlyList<ExtractionResultCell> cells)
        : this(
            ordinal,
            databaseRowOrdinal,
            sourceRowOrdinal,
            semanticIdentity?.ToString() ?? throw new ArgumentNullException(nameof(semanticIdentity)),
            source,
            cells)
    {
        if (semanticIdentity.SourceId != source.SourceId
            || semanticIdentity.RowFamilyId.SourceSetId != source.SourceSetId
            || cells.Any(cell => cell.ColumnIdentity.RowFamilyId != semanticIdentity.RowFamilyId))
        {
            throw new ArgumentException(
                "An Extraction row identity must match its source and Row Family.",
                nameof(semanticIdentity));
        }

        SemanticIdentity = semanticIdentity;
    }

    public int Ordinal { get; }

    public int DatabaseRowOrdinal { get; }

    public int SourceRowOrdinal { get; }

    public string RecordIdentity { get; }

    public DatabaseRecordIdentity? SemanticIdentity { get; init; }

    public DatabaseSourceMetadata Source { get; }

    public IReadOnlyList<ExtractionResultCell> Cells { get; }

    [JsonIgnore]
    public bool HasConflict => Cells.Any(cell => cell.HasConflict);
}
