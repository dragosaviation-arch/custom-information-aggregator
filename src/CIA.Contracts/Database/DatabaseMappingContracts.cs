using System.Collections.ObjectModel;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Database;

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
