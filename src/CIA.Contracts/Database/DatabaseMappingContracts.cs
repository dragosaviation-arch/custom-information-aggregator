using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
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
    [JsonConstructor]
    public DatabaseFieldMapping(
        DatabaseLogicalFieldIdentity logicalIdentity,
        string effectiveName,
        bool isExplicitOverride,
        IReadOnlyList<DiscoveryInformationIdentity> detailedIdentities)
    {
        ArgumentNullException.ThrowIfNull(logicalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveName);
        ArgumentNullException.ThrowIfNull(detailedIdentities);
        var details = detailedIdentities.ToArray();
        if (details.Length == 0
            || details.Any(identity => identity is null
                || identity.SourceSetId != logicalIdentity.SourceSetId
                || DatabaseLogicalFieldIdentity.Create(identity) != logicalIdentity)
            || details.Distinct().Count() != details.Length)
        {
            throw new ArgumentException(
                "A Database field mapping requires unique detailed identities for one logical field.",
                nameof(detailedIdentities));
        }

        LogicalIdentity = logicalIdentity;
        EffectiveName = effectiveName;
        IsExplicitOverride = isExplicitOverride;
        DetailedIdentities = new ReadOnlyCollection<DiscoveryInformationIdentity>(details);
    }

    public DatabaseLogicalFieldIdentity LogicalIdentity { get; }

    public string EffectiveName { get; }

    public bool IsExplicitOverride { get; }

    public IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities { get; }

    [JsonIgnore]
    public string FieldKey => IsExplicitOverride
        ? $"mapped:{EffectiveName}"
        : $"logical:{(int)LogicalIdentity.CandidateKind}:{LogicalIdentity.CanonicalFieldIdentity}";
}

public sealed record DatabaseDatasetBuildSpecification
{
    [JsonConstructor]
    public DatabaseDatasetBuildSpecification(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        RepeatedDataLayout repeatedDataLayout,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<DatabaseFieldMapping> fields)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A Database dataset requires a Source Set ID.", nameof(sourceSetId));
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
        ArgumentNullException.ThrowIfNull(fields);
        var sourceArray = sources.ToArray();
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

        if (fieldArray.Length == 0
            || fieldArray.Any(field => field is null
                || field.LogicalIdentity.SourceSetId != sourceSetId)
            || fieldArray.Select(field => field.LogicalIdentity).Distinct().Count()
                != fieldArray.Length
            || fieldArray.SelectMany(field => field.DetailedIdentities).Distinct().Count()
                != fieldArray.Sum(field => field.DetailedIdentities.Count))
        {
            throw new ArgumentException(
                "A Database dataset requires unique selected field mappings from its Source Set.",
                nameof(fields));
        }

        SourceSetId = sourceSetId;
        DisplayName = displayName.Trim();
        Ordinal = ordinal;
        RepeatedDataLayout = repeatedDataLayout;
        Sources = new ReadOnlyCollection<LoadedSourceContract>(sourceArray);
        Fields = new ReadOnlyCollection<DatabaseFieldMapping>(fieldArray);
    }

    public SourceSetId SourceSetId { get; }

    public string DisplayName { get; }

    public int Ordinal { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }

    public IReadOnlyList<LoadedSourceContract> Sources { get; }

    public IReadOnlyList<DatabaseFieldMapping> Fields { get; }
}

public sealed record DatabaseBuildSpecification
{
    [JsonConstructor]
    public DatabaseBuildSpecification(IReadOnlyList<DatabaseDatasetBuildSpecification> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var datasetArray = datasets.ToArray();
        if (datasetArray.Length == 0
            || datasetArray.Any(dataset => dataset is null)
            || datasetArray.Select(dataset => dataset.SourceSetId).Distinct().Count()
                != datasetArray.Length
            || !datasetArray.Select(dataset => dataset.Ordinal)
                .SequenceEqual(Enumerable.Range(1, datasetArray.Length)))
        {
            throw new ArgumentException(
                "A Database build requires uniquely ordered Source Set datasets.",
                nameof(datasets));
        }

        Datasets = new ReadOnlyCollection<DatabaseDatasetBuildSpecification>(datasetArray);
    }

    public IReadOnlyList<DatabaseDatasetBuildSpecification> Datasets { get; }
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
    [JsonConstructor]
    public DatabaseColumnIdentity(
        SourceSetId sourceSetId,
        string fieldKey,
        DatabaseRepeatCoordinatePath repeatCoordinates)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A Database column requires a Source Set ID.", nameof(sourceSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);
        ArgumentNullException.ThrowIfNull(repeatCoordinates);
        SourceSetId = sourceSetId;
        FieldKey = fieldKey;
        RepeatCoordinates = repeatCoordinates;
    }

    public SourceSetId SourceSetId { get; }

    public string FieldKey { get; }

    public DatabaseRepeatCoordinatePath RepeatCoordinates { get; }
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
