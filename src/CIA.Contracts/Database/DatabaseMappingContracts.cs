using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Database;

public sealed record DatabaseLogicalFieldIdentity(
    SourceSetId SourceSetId,
    SourceValueCandidateKind CandidateKind,
    string CanonicalFieldIdentity)
{
    public static DatabaseLogicalFieldIdentity Create(DiscoveryInformationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var canonicalFieldIdentity = identity.CandidateKind switch
        {
            SourceValueCandidateKind.Element => GetFinalStructuralSegment(
                identity.StructuralPath),
            SourceValueCandidateKind.Attribute => GetFinalStructuralSegment(
                identity.StructuralIdentity),
            _ => identity.StructuralIdentity
        };
        return new DatabaseLogicalFieldIdentity(
            identity.SourceSetId,
            identity.CandidateKind,
            canonicalFieldIdentity);
    }

    private static string GetFinalStructuralSegment(string path)
    {
        var namespaceDepth = 0;
        var finalSeparator = -1;
        for (var index = 0; index < path.Length; index++)
        {
            switch (path[index])
            {
                case '{':
                    namespaceDepth++;
                    break;
                case '}' when namespaceDepth > 0:
                    namespaceDepth--;
                    break;
                case '/' when namespaceDepth == 0:
                    finalSeparator = index;
                    break;
            }
        }

        return finalSeparator < 0 ? path : path[(finalSeparator + 1)..];
    }
}

public sealed record DatabaseFieldMapping
{
    public DatabaseFieldMapping(
        DatabaseLogicalFieldIdentity logicalIdentity,
        string effectiveName,
        bool isExplicitOverride,
        IReadOnlyList<DiscoveryInformationIdentity> detailedIdentities)
        : this(
            new RowFamilyId(logicalIdentity.SourceSetId, RowFamilyKind.Root, "/legacy"),
            effectiveName,
            isExplicitOverride,
            detailedIdentities)
    {
    }

    [JsonConstructor]
    public DatabaseFieldMapping(
        RowFamilyId rowFamilyId,
        string effectiveName,
        bool isExplicitOverride,
        IReadOnlyList<DiscoveryInformationIdentity> detailedIdentities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveName);
        ArgumentNullException.ThrowIfNull(detailedIdentities);
        var details = detailedIdentities.ToArray();
        if (details.Length == 0
            || details.Any(identity => identity is null
                || identity.SourceSetId != rowFamilyId.SourceSetId)
            || details.Distinct().Count() != details.Length)
        {
            throw new ArgumentException(
                "A Database field mapping requires unique detailed identities from one Row Family Source Set.",
                nameof(detailedIdentities));
        }

        RowFamilyId = rowFamilyId;
        LogicalIdentity = DatabaseLogicalFieldIdentity.Create(details[0]);
        EffectiveName = effectiveName.Trim();
        IsExplicitOverride = isExplicitOverride;
        DetailedIdentities = new ReadOnlyCollection<DiscoveryInformationIdentity>(details);
    }

    public RowFamilyId RowFamilyId { get; }

    // Retained for legacy migration and presentation consumers. Family-aware mapping
    // authority is RowFamilyId + EffectiveName + DetailedIdentities.
    public DatabaseLogicalFieldIdentity LogicalIdentity { get; }

    public string EffectiveName { get; }

    public bool IsExplicitOverride { get; }

    public IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities { get; }

    [JsonIgnore]
    public string FieldKey => string.Equals(RowFamilyId.AnchorPath, "/legacy", StringComparison.Ordinal)
        ? IsExplicitOverride
            ? $"mapped:{EffectiveName}"
            : $"logical:{(int)LogicalIdentity.CandidateKind}:{LogicalIdentity.CanonicalFieldIdentity}"
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"family:{(int)RowFamilyId.Kind}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RowFamilyId.AnchorPath))}:{EffectiveName}");
}

public sealed record DatabaseDatasetBuildSpecification
{
    public DatabaseDatasetBuildSpecification(
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<RowFamilyDefinition> rowFamilies,
        IReadOnlyList<DatabaseFieldMapping> fields)
        : this(
            parentSourceSetId,
            displayName,
            ordinal,
            repeatedDataLayout,
            sources,
            rowFamilies,
            fields,
            outputSetId)
    {
    }

    public DatabaseDatasetBuildSpecification(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<DatabaseFieldMapping> fields)
        : this(
            sourceSetId,
            displayName,
            ordinal,
            repeatedDataLayout,
            sources,
            fields.Select(field => new RowFamilyDefinition(field.RowFamilyId, null))
                .DistinctBy(family => family.Id)
                .ToArray(),
            fields,
            outputSetId: null)
    {
    }

    [JsonConstructor]
    public DatabaseDatasetBuildSpecification(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<RowFamilyDefinition> rowFamilies,
        IReadOnlyList<DatabaseFieldMapping> fields,
        OutputSetId? outputSetId = null)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A Database dataset requires a Source Set ID.", nameof(sourceSetId));
        }

        if (outputSetId is { Value: var value } && value == Guid.Empty)
        {
            throw new ArgumentException("A Database Output Set requires an identity.", nameof(outputSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        if (!Enum.IsDefined(repeatedDataLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(repeatedDataLayout));
        }

        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rowFamilies);
        ArgumentNullException.ThrowIfNull(fields);
        var sourceArray = sources.ToArray();
        var familyArray = rowFamilies.ToArray();
        var fieldArray = fields.ToArray();
        if (sourceArray.Length == 0
            || sourceArray.Any(source => source is null
                || source.SourceSetId != sourceSetId
                || !source.IsIncluded
                || source.Status != LoadedSourceStatus.Ready)
            || sourceArray.Select(source => source.SourceId).Distinct().Count() != sourceArray.Length)
        {
            throw new ArgumentException(
                "A Database dataset requires unique included ready sources from its Source Set.",
                nameof(sources));
        }

        var familyIds = familyArray.Select(family => family.Id).ToHashSet();
        if (familyArray.Length == 0
            || familyArray.Any(family => family is null
                || family.Id.SourceSetId != sourceSetId)
            || familyIds.Count != familyArray.Length
            || fieldArray.Length == 0
            || fieldArray.Any(field => field is null
                || field.RowFamilyId.SourceSetId != sourceSetId
                || !familyIds.Contains(field.RowFamilyId))
            || (!familyArray.All(family => string.Equals(
                    family.Id.AnchorPath,
                    "/legacy",
                    StringComparison.Ordinal))
                && fieldArray.Select(field => (field.RowFamilyId, field.EffectiveName))
                    .Distinct().Count() != fieldArray.Length)
            || fieldArray.SelectMany(field => field.DetailedIdentities).Distinct().Count()
                != fieldArray.Sum(field => field.DetailedIdentities.Count))
        {
            throw new ArgumentException(
                "A Database dataset requires unique selected field mappings from its Source Set.",
                nameof(fields));
        }

        SourceSetId = sourceSetId;
        OutputSetId = outputSetId;
        DisplayName = displayName.Trim();
        Ordinal = ordinal;
        RepeatedDataLayout = repeatedDataLayout;
        Sources = new ReadOnlyCollection<LoadedSourceContract>(sourceArray);
        RowFamilies = new ReadOnlyCollection<RowFamilyDefinition>(familyArray);
        Fields = new ReadOnlyCollection<DatabaseFieldMapping>(fieldArray);
    }

    public SourceSetId SourceSetId { get; }

    public OutputSetId? OutputSetId { get; }

    [JsonIgnore]
    public SourceSetId ParentSourceSetId => SourceSetId;

    public string DisplayName { get; }

    public int Ordinal { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }

    public IReadOnlyList<LoadedSourceContract> Sources { get; }

    public IReadOnlyList<RowFamilyDefinition> RowFamilies { get; }

    public IReadOnlyList<DatabaseFieldMapping> Fields { get; }
}

public sealed record DatabaseBuildSpecification
{
    [JsonConstructor]
    public DatabaseBuildSpecification(IReadOnlyList<DatabaseDatasetBuildSpecification> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var datasetArray = datasets.ToArray();
        var outputSetBacked = datasetArray.Length > 0
            && datasetArray.All(dataset => dataset?.OutputSetId is not null);
        var legacySourceSetBacked = datasetArray.Length > 0
            && datasetArray.All(dataset => dataset?.OutputSetId is null);
        if (datasetArray.Length == 0
            || datasetArray.Any(dataset => dataset is null)
            || (!outputSetBacked && !legacySourceSetBacked)
            || (outputSetBacked
                ? datasetArray.Select(dataset => dataset.OutputSetId).Distinct().Count()
                    != datasetArray.Length
                : datasetArray.Select(dataset => dataset.SourceSetId).Distinct().Count()
                    != datasetArray.Length)
            || !datasetArray.Select(dataset => dataset.Ordinal)
                .SequenceEqual(Enumerable.Range(1, datasetArray.Length))
            || datasetArray.GroupBy(dataset => dataset.SourceSetId).Any(group =>
                group.Skip(1).Any(dataset => !SourceBoundariesEqual(
                    dataset.Sources,
                    group.First().Sources))))
        {
            throw new ArgumentException(
                "A Database build requires uniquely ordered datasets with one source boundary per parent Source Set.",
                nameof(datasets));
        }

        Datasets = new ReadOnlyCollection<DatabaseDatasetBuildSpecification>(datasetArray);
    }

    public IReadOnlyList<DatabaseDatasetBuildSpecification> Datasets { get; }

    private static bool SourceBoundariesEqual(
        IReadOnlyList<LoadedSourceContract> left,
        IReadOnlyList<LoadedSourceContract> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!SourcesEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SourcesEqual(LoadedSourceContract left, LoadedSourceContract right)
    {
        return left.SourceId == right.SourceId
            && left.SourceSetId == right.SourceSetId
            && string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && left.IsIncluded == right.IsIncluded
            && left.Status == right.Status
            && left.Kind == right.Kind
            && ArchiveProvenanceEqual(left.ArchiveProvenance, right.ArchiveProvenance);
    }

    private static bool ArchiveProvenanceEqual(
        ArchiveSourceProvenance? left,
        ArchiveSourceProvenance? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.OriginalArchiveSourceId == right.OriginalArchiveSourceId
            && string.Equals(
                left.OriginalArchivePath,
                right.OriginalArchivePath,
                StringComparison.Ordinal)
            && ArchiveLineagesEqual(left.ArchiveLineage, right.ArchiveLineage)
            && left.ArchiveNestingLevel == right.ArchiveNestingLevel
            && string.Equals(
                left.ArchiveMemberPath,
                right.ArchiveMemberPath,
                StringComparison.Ordinal)
            && string.Equals(left.ExtractionRoot, right.ExtractionRoot, StringComparison.Ordinal)
            && left.Retention == right.Retention
            && left.MaximumArchiveNestingDepth == right.MaximumArchiveNestingDepth
            && string.Equals(
                left.PersistentExtractionDirectory,
                right.PersistentExtractionDirectory,
                StringComparison.Ordinal);
    }

    private static bool ArchiveLineagesEqual(
        IReadOnlyList<ArchiveLineageItem?>? left,
        IReadOnlyList<ArchiveLineageItem?>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftItem = left[index];
            var rightItem = right[index];
            if (leftItem is null || rightItem is null)
            {
                if (leftItem is not null || rightItem is not null)
                {
                    return false;
                }

                continue;
            }

            if (leftItem.ArchiveSourceId != rightItem.ArchiveSourceId
                || !string.Equals(leftItem.Path, rightItem.Path, StringComparison.Ordinal)
                || leftItem.NestingLevel != rightItem.NestingLevel)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class DatabaseRepeatCoordinatePath :
    IEquatable<DatabaseRepeatCoordinatePath>,
    IComparable<DatabaseRepeatCoordinatePath>
{
    private readonly int[] _coordinates;

    [JsonConstructor]
    public DatabaseRepeatCoordinatePath(IReadOnlyList<int> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        _coordinates = coordinates.ToArray();
        if (_coordinates.Any(coordinate => coordinate < 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinates),
                "Database repeat coordinates must be positive.");
        }

        Coordinates = new ReadOnlyCollection<int>(_coordinates);
    }

    public static DatabaseRepeatCoordinatePath Empty { get; } = new([]);

    public IReadOnlyList<int> Coordinates { get; }

    public bool Equals(DatabaseRepeatCoordinatePath? other) =>
        other is not null && _coordinates.SequenceEqual(other._coordinates);

    public override bool Equals(object? obj) =>
        obj is DatabaseRepeatCoordinatePath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var coordinate in _coordinates)
        {
            hash.Add(coordinate);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(DatabaseRepeatCoordinatePath? other)
    {
        if (other is null)
        {
            return 1;
        }

        var sharedLength = Math.Min(_coordinates.Length, other._coordinates.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = _coordinates[index].CompareTo(other._coordinates[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _coordinates.Length.CompareTo(other._coordinates.Length);
    }

    public override string ToString() => string.Join(
        "/",
        _coordinates.Select(coordinate => $"[{coordinate}]"));
}

public sealed record DatabaseColumnIdentity
{
    public DatabaseColumnIdentity(
        SourceSetId sourceSetId,
        string fieldKey,
        DatabaseRepeatCoordinatePath repeatCoordinates)
        : this(
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/legacy"),
            fieldKey,
            repeatCoordinates)
    {
    }

    [JsonConstructor]
    public DatabaseColumnIdentity(
        RowFamilyId rowFamilyId,
        string fieldKey,
        DatabaseRepeatCoordinatePath repeatCoordinates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);
        ArgumentNullException.ThrowIfNull(repeatCoordinates);
        RowFamilyId = rowFamilyId;
        FieldKey = fieldKey;
        RepeatCoordinates = repeatCoordinates;
    }

    public RowFamilyId RowFamilyId { get; }

    [JsonIgnore]
    public SourceSetId SourceSetId => RowFamilyId.SourceSetId;

    public string FieldKey { get; }

    public DatabaseRepeatCoordinatePath RepeatCoordinates { get; }
}

public sealed record DatabaseRecordIdentity
{
    [JsonConstructor]
    public DatabaseRecordIdentity(
        RowFamilyId rowFamilyId,
        SourceId sourceId,
        long anchorInstanceId,
        int projectionOrdinal)
    {
        if (!SourceId.IsValid(sourceId.Value))
        {
            throw new ArgumentException("A Database record requires a Source ID.", nameof(sourceId));
        }

        if (anchorInstanceId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorInstanceId));
        }

        if (projectionOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionOrdinal));
        }

        RowFamilyId = rowFamilyId;
        SourceId = sourceId;
        AnchorInstanceId = anchorInstanceId;
        ProjectionOrdinal = projectionOrdinal;
    }

    public RowFamilyId RowFamilyId { get; }

    public SourceId SourceId { get; }

    public long AnchorInstanceId { get; }

    public int ProjectionOrdinal { get; }

    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{RowFamilyId.SourceSetId}|{(int)RowFamilyId.Kind}|{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RowFamilyId.AnchorPath))}|{SourceId}|{AnchorInstanceId}|{ProjectionOrdinal}");
}

public sealed record DatabaseColumnDefinition(
    DatabaseColumnIdentity Identity,
    string EffectiveName,
    int Ordinal)
{
    [JsonIgnore]
    public string DatabaseTagName => EffectiveName;

    public IReadOnlyList<DatabaseReviewValue> Values { get; init; } = [];

    public int TotalValueCount { get; init; }
}

public sealed record DatabaseColumnMapping
{
    public DatabaseColumnMapping(
        string databaseTagName,
        IReadOnlyList<string> sourceInformationTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTagName);
        ArgumentNullException.ThrowIfNull(sourceInformationTypes);

        var informationTypes = sourceInformationTypes.ToArray();
        if (informationTypes.Length == 0
            || informationTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A Database column mapping requires at least one valid source information identity.",
                nameof(sourceInformationTypes));
        }

        if (informationTypes.Distinct(StringComparer.Ordinal).Count() != informationTypes.Length)
        {
            throw new ArgumentException(
                "Source information identities in a Database column mapping must be unique.",
                nameof(sourceInformationTypes));
        }

        DatabaseTagName = databaseTagName;
        SourceInformationTypes = new ReadOnlyCollection<string>(informationTypes);
    }

    public string DatabaseTagName { get; }

    public IReadOnlyList<string> SourceInformationTypes { get; }
}

public sealed record DatabaseMappingSnapshot
{
    public DatabaseMappingSnapshot(IReadOnlyList<DatabaseColumnMapping> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var columnArray = columns.ToArray();
        if (columnArray.Any(column => column is null))
        {
            throw new ArgumentException(
                "A Database mapping cannot contain null columns.",
                nameof(columns));
        }

        if (columnArray
            .Select(column => column.DatabaseTagName)
            .Distinct(StringComparer.Ordinal)
            .Count() != columnArray.Length)
        {
            throw new ArgumentException(
                "Database Tag Names in a mapping must be unique.",
                nameof(columns));
        }

        var sourceInformationTypes = columnArray
            .SelectMany(column => column.SourceInformationTypes)
            .ToArray();
        if (sourceInformationTypes.Distinct(StringComparer.Ordinal).Count()
            != sourceInformationTypes.Length)
        {
            throw new ArgumentException(
                "A source information identity cannot map to multiple Database columns.",
                nameof(columns));
        }

        Columns = new ReadOnlyCollection<DatabaseColumnMapping>(columnArray);
    }

    public IReadOnlyList<DatabaseColumnMapping> Columns { get; }
}

public sealed record MappedDatabaseValue
{
    public MappedDatabaseValue(
        string databaseTagName,
        string sourceInformationType,
        string value,
        SourceId sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInformationType);
        ArgumentNullException.ThrowIfNull(value);

        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A mapped Database value requires a source ID.",
                nameof(sourceId));
        }

        DatabaseTagName = databaseTagName;
        SourceInformationType = sourceInformationType;
        Value = value;
        SourceId = sourceId;
    }

    public string DatabaseTagName { get; }

    public string SourceInformationType { get; }

    public string Value { get; }

    public SourceId SourceId { get; }
}
