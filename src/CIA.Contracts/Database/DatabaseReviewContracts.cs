using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Database;

public static class DatabaseReviewLimits
{
    public const int MaximumRowsPerPage = 100;
}

public enum DatabaseRowInclusionFilter
{
    All = 0,
    Included = 1,
    Excluded = 2
}

public enum DatabaseMetadataVisibilityMode
{
    None = 0,
    Useful = 1,
    All = 2,
    Custom = 3
}

public enum DatabaseMetadataField
{
    SourceSet = 0,
    SourceFile = 1,
    FullSourcePath = 2,
    SourceId = 3,
    FileModified = 4,
    SourceKind = 5,
    ContainerProvenance = 6,
    RecordHierarchy = 7,
    ValuePath = 8,
    TraversalOrdinal = 9,
    CandidateKind = 10,
    StructuralIdentity = 11
}

public sealed record DatabaseReviewQuery
{
    [JsonConstructor]
    public DatabaseReviewQuery(
        OperationId generationId,
        SourceSetId sourceSetId,
        int startRowOrdinal,
        int rowCount,
        string? searchText,
        DatabaseRowInclusionFilter inclusionFilter)
    {
        if (!OperationId.IsValid(generationId.Value))
        {
            throw new ArgumentException("A Database review query requires a generation ID.", nameof(generationId));
        }

        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A Database review query requires a Source Set ID.", nameof(sourceSetId));
        }

        if (startRowOrdinal < 1
            || startRowOrdinal > int.MaxValue - DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(startRowOrdinal));
        }

        if (rowCount is < 1 or > DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        }

        if (!Enum.IsDefined(inclusionFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(inclusionFilter));
        }

        GenerationId = generationId;
        SourceSetId = sourceSetId;
        StartRowOrdinal = startRowOrdinal;
        RowCount = rowCount;
        SearchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
        InclusionFilter = inclusionFilter;
    }

    public OperationId GenerationId { get; }

    public SourceSetId SourceSetId { get; }

    public int StartRowOrdinal { get; }

    public int RowCount { get; }

    public string? SearchText { get; }

    public DatabaseRowInclusionFilter InclusionFilter { get; }
}

public sealed record DatabaseSourceMetadata(
    SourceSetId SourceSetId,
    string SourceSetName,
    SourceId SourceId,
    string SourceFileName,
    string FullSourcePath,
    LoadedSourceKind SourceKind,
    string? ArchivePath,
    string? ArchiveMemberPath,
    DateTimeOffset? FileModifiedUtc);

public sealed record DatabaseSourceElementEvidence(
    string LocalName,
    string NamespaceUri,
    string QualifiedName,
    long InstanceId,
    int SiblingPosition);

public sealed record DatabaseLineageEvidence
{
    [JsonConstructor]
    public DatabaseLineageEvidence(
        string structuralPath,
        long nodeInstanceId,
        long? parentInstanceId,
        IReadOnlyList<long> ancestorInstanceIds,
        long traversalOrder,
        IReadOnlyList<DatabaseSourceElementEvidence> elementPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        ArgumentNullException.ThrowIfNull(ancestorInstanceIds);
        ArgumentNullException.ThrowIfNull(elementPath);
        if (nodeInstanceId < 1
            || parentInstanceId is < 1
            || traversalOrder < 1
            || ancestorInstanceIds.Any(id => id < 1)
            || elementPath.Count == 0
            || elementPath.Any(element => element is null))
        {
            throw new ArgumentException("Database lineage evidence is invalid.");
        }

        StructuralPath = structuralPath;
        NodeInstanceId = nodeInstanceId;
        ParentInstanceId = parentInstanceId;
        AncestorInstanceIds = new ReadOnlyCollection<long>(ancestorInstanceIds.ToArray());
        TraversalOrder = traversalOrder;
        ElementPath = new ReadOnlyCollection<DatabaseSourceElementEvidence>(elementPath.ToArray());
    }

    public string StructuralPath { get; }

    public long NodeInstanceId { get; }

    public long? ParentInstanceId { get; }

    public IReadOnlyList<long> AncestorInstanceIds { get; }

    public long TraversalOrder { get; }

    public IReadOnlyList<DatabaseSourceElementEvidence> ElementPath { get; }
}

public sealed record DatabaseReviewValue
{
    public DatabaseReviewValue(
        int columnOrdinal,
        string value,
        string sourceInformationType,
        SourceId sourceId)
        : this(
            value,
            new DiscoveryInformationIdentity(
                SourceSetId.From(sourceId.Value),
                $"/{sourceInformationType}",
                sourceInformationType,
                SourceValueCandidateKind.Element,
                $"/{sourceInformationType}"),
            sourceId,
            new DatabaseLineageEvidence(
                $"/{sourceInformationType}",
                1,
                null,
                [],
                Math.Max(1, columnOrdinal),
                [new DatabaseSourceElementEvidence(sourceInformationType, string.Empty, sourceInformationType, 1, 1)]),
            DatabaseRepeatCoordinatePath.Empty)
    {
        ColumnOrdinal = columnOrdinal;
    }

    [JsonConstructor]
    public DatabaseReviewValue(
        string value,
        DiscoveryInformationIdentity detailedIdentity,
        SourceId sourceId,
        DatabaseLineageEvidence lineage,
        DatabaseRepeatCoordinatePath repeatCoordinates)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(detailedIdentity);
        if (!SourceId.IsValid(sourceId.Value))
        {
            throw new ArgumentException("A Database review value requires a Source ID.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(repeatCoordinates);
        Value = value;
        DetailedIdentity = detailedIdentity;
        SourceId = sourceId;
        Lineage = lineage;
        RepeatCoordinates = repeatCoordinates;
    }

    public string Value { get; }

    public DiscoveryInformationIdentity DetailedIdentity { get; }

    public SourceId SourceId { get; }

    public DatabaseLineageEvidence Lineage { get; }

    public DatabaseRepeatCoordinatePath RepeatCoordinates { get; }

    public int ColumnOrdinal { get; }

    [JsonIgnore]
    public string SourceInformationType => DetailedIdentity.InformationType;
}

public sealed record DatabaseReviewColumn
{
    public DatabaseReviewColumn(
        string databaseTagName,
        int totalValueCount,
        IReadOnlyList<DatabaseReviewValue> values)
    {
        DatabaseTagName = databaseTagName;
        TotalValueCount = totalValueCount;
        Values = values;
    }

    public string DatabaseTagName { get; }

    public int TotalValueCount { get; }

    public IReadOnlyList<DatabaseReviewValue> Values { get; }
}

public sealed record DatabaseReviewCell
{
    [JsonConstructor]
    public DatabaseReviewCell(
        DatabaseColumnIdentity columnIdentity,
        bool hasConflict,
        IReadOnlyList<DatabaseReviewValue> values)
    {
        ArgumentNullException.ThrowIfNull(columnIdentity);
        ArgumentNullException.ThrowIfNull(values);
        var valueArray = values.ToArray();
        if (valueArray.Length == 0 || valueArray.Any(value => value is null))
        {
            throw new ArgumentException("A Database review cell requires underlying values.", nameof(values));
        }

        var expectedConflict = valueArray.Select(value => value.Value)
            .Distinct(StringComparer.Ordinal).Skip(1).Any();
        if (hasConflict != expectedConflict)
        {
            throw new ArgumentException("The Database cell conflict state is inconsistent.", nameof(hasConflict));
        }

        ColumnIdentity = columnIdentity;
        HasConflict = hasConflict;
        Values = new ReadOnlyCollection<DatabaseReviewValue>(valueArray);
    }

    public DatabaseColumnIdentity ColumnIdentity { get; }

    public bool HasConflict { get; }

    public IReadOnlyList<DatabaseReviewValue> Values { get; }
}

public sealed record DatabaseReviewRow
{
    [JsonConstructor]
    public DatabaseReviewRow(
        int ordinal,
        bool isIncluded,
        string recordIdentity,
        DatabaseSourceMetadata source,
        IReadOnlyList<DatabaseReviewCell> cells)
    {
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(recordIdentity);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cells);
        var cellArray = cells.ToArray();
        if (cellArray.Any(cell => cell is null
            || cell.Values.Any(value => value.SourceId != source.SourceId)))
        {
            throw new ArgumentException(
                "Every Database row value must belong to the row SourceId.",
                nameof(cells));
        }

        Ordinal = ordinal;
        IsIncluded = isIncluded;
        RecordIdentity = recordIdentity;
        Source = source;
        Cells = new ReadOnlyCollection<DatabaseReviewCell>(cellArray);
    }

    public int Ordinal { get; }

    public bool IsIncluded { get; }

    public string RecordIdentity { get; }

    public DatabaseSourceMetadata Source { get; }

    public IReadOnlyList<DatabaseReviewCell> Cells { get; }

    [JsonIgnore]
    public bool HasConflict => Cells.Any(cell => cell.HasConflict);

    [JsonIgnore]
    public string RecordHierarchy => CreateRecordHierarchy();

    private string CreateRecordHierarchy()
    {
        var values = Cells.SelectMany(cell => cell.Values).ToArray();
        if (values.Length == 0)
        {
            return string.Empty;
        }

        var firstPath = values[0].Lineage.ElementPath;
        var commonLength = firstPath.Count;
        foreach (var value in values.Skip(1))
        {
            commonLength = Math.Min(commonLength, value.Lineage.ElementPath.Count);
            var index = 0;
            while (index < commonLength
                && SameElementInstance(firstPath[index], value.Lineage.ElementPath[index]))
            {
                index++;
            }

            commonLength = index;
        }

        if (values.Length == 1 && commonLength == firstPath.Count && commonLength > 1)
        {
            commonLength--;
        }

        return string.Concat(firstPath.Take(commonLength).Select(FormatElementContext));
    }

    private static bool SameElementInstance(
        DatabaseSourceElementEvidence first,
        DatabaseSourceElementEvidence second) =>
        first.InstanceId == second.InstanceId
        && string.Equals(first.LocalName, second.LocalName, StringComparison.Ordinal)
        && string.Equals(first.NamespaceUri, second.NamespaceUri, StringComparison.Ordinal);

    private static string FormatElementContext(DatabaseSourceElementEvidence element)
    {
        var name = string.IsNullOrEmpty(element.NamespaceUri)
            ? element.QualifiedName
            : $"{{{element.NamespaceUri}}}{element.LocalName}";
        return $"/{name}[{element.SiblingPosition}]";
    }
}

public sealed record DatabaseReviewPage
{
    public DatabaseReviewPage(
        OperationId generationId,
        int startRowOrdinal,
        int requestedRowCount,
        int totalMappedValueCount,
        IReadOnlyList<DatabaseReviewColumn> columns)
        : this(
            generationId,
            CreateLegacyDataset(generationId, totalMappedValueCount, columns),
            startRowOrdinal,
            requestedRowCount,
            columns.Count == 0 ? 0 : columns.Max(column => column.TotalValueCount),
            [])
    {
        LegacyColumns = columns;
    }

    [JsonConstructor]
    public DatabaseReviewPage(
        OperationId generationId,
        DatabaseDatasetSummary dataset,
        int startRowOrdinal,
        int requestedRowCount,
        int totalRowCount,
        IReadOnlyList<DatabaseReviewRow> rows)
    {
        if (!OperationId.IsValid(generationId.Value))
        {
            throw new ArgumentException("A Database review page requires a generation ID.", nameof(generationId));
        }

        ArgumentNullException.ThrowIfNull(dataset);
        if (startRowOrdinal < 1
            || requestedRowCount is < 1 or > DatabaseReviewLimits.MaximumRowsPerPage
            || totalRowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startRowOrdinal));
        }

        ArgumentNullException.ThrowIfNull(rows);
        var rowArray = rows.ToArray();
        if (rowArray.Length > requestedRowCount
            || rowArray.Any(row => row is null
                || row.Source.SourceSetId != dataset.SourceSetId)
            || !rowArray.Select(row => row.Ordinal).SequenceEqual(
                rowArray.Select(row => row.Ordinal).Order()))
        {
            throw new ArgumentException("A Database review page contains invalid rows.", nameof(rows));
        }

        GenerationId = generationId;
        Dataset = dataset;
        StartRowOrdinal = startRowOrdinal;
        RequestedRowCount = requestedRowCount;
        TotalRowCount = totalRowCount;
        Rows = new ReadOnlyCollection<DatabaseReviewRow>(rowArray);
        LegacyColumns = Array.Empty<DatabaseReviewColumn>();
    }

    public OperationId GenerationId { get; }

    public DatabaseDatasetSummary Dataset { get; }

    public int StartRowOrdinal { get; }

    public int RequestedRowCount { get; }

    public int TotalRowCount { get; }

    public IReadOnlyList<DatabaseReviewRow> Rows { get; }

    [JsonIgnore]
    public IReadOnlyList<DatabaseReviewColumn> LegacyColumns { get; }

    [JsonIgnore]
    public int TotalMappedValueCount => Dataset.ValueCount;

    [JsonIgnore]
    public int TotalPresentationRowCount => TotalRowCount;

    [JsonIgnore]
    public IReadOnlyList<DatabaseColumnDefinition> Columns => Dataset.Columns;

    private static DatabaseDatasetSummary CreateLegacyDataset(
        OperationId generationId,
        int totalMappedValueCount,
        IReadOnlyList<DatabaseReviewColumn> columns)
    {
        var sourceSetId = SourceSetId.From(generationId.Value);
        var mappings = columns.Select(column =>
        {
            var identity = new DiscoveryInformationIdentity(
                sourceSetId,
                $"/{column.DatabaseTagName}",
                column.DatabaseTagName,
                SourceValueCandidateKind.Element,
                $"/{column.DatabaseTagName}");
            return new DatabaseFieldMapping(
                DatabaseLogicalFieldIdentity.Create(identity),
                column.DatabaseTagName,
                false,
                [identity]);
        }).ToArray();
        var definitions = mappings.Select((mapping, index) => new DatabaseColumnDefinition(
            new DatabaseColumnIdentity(sourceSetId, mapping.FieldKey, DatabaseRepeatCoordinatePath.Empty),
            mapping.EffectiveName,
            index + 1)
        {
            Values = columns[index].Values,
            TotalValueCount = columns[index].TotalValueCount
        }).ToArray();
        return new DatabaseDatasetSummary(
            sourceSetId,
            "Legacy Database",
            1,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            Math.Max(1, columns.Count == 0 ? 0 : columns.Max(column => column.TotalValueCount)),
            Math.Max(1, totalMappedValueCount),
            definitions,
            mappings);
    }
}

public sealed record DatabaseRowInclusionChange(
    OperationId GenerationId,
    SourceSetId SourceSetId,
    IReadOnlyList<int> RowOrdinals,
    bool IsIncluded);
