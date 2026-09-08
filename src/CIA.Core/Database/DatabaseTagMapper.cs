using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Core.Database;

public static class DatabaseTagMapper
{
    public static DatabaseMappingSnapshot CreateMapping(
        DiscoveryConfigurationSnapshot discoveryConfiguration,
        IReadOnlyDictionary<string, string> databaseTagOverrides)
    {
        ArgumentNullException.ThrowIfNull(discoveryConfiguration);
        ArgumentNullException.ThrowIfNull(databaseTagOverrides);

        var columns = new List<MutableColumnMapping>();
        var columnsByName = new Dictionary<string, MutableColumnMapping>(
            StringComparer.Ordinal);

        foreach (var item in discoveryConfiguration.Items
                     .Where(item => item.Disposition == DiscoveryInformationDisposition.Selected)
                     .OrderBy(item => item.InformationType, StringComparer.Ordinal))
        {
            var databaseTagName = ResolveDatabaseTagName(
                item.InformationType,
                databaseTagOverrides);
            if (!columnsByName.TryGetValue(databaseTagName, out var column))
            {
                column = new MutableColumnMapping(databaseTagName);
                columns.Add(column);
                columnsByName.Add(databaseTagName, column);
            }

            column.SourceInformationTypes.Add(item.InformationType);
        }

        return new DatabaseMappingSnapshot(
            columns
                .Select(column => new DatabaseColumnMapping(
                    column.DatabaseTagName,
                    column.SourceInformationTypes))
                .ToArray());
    }

    public static MappedDatabaseValue? MapValue(
        DatabaseMappingSnapshot mapping,
        string sourceInformationType,
        string value,
        SourceId sourceId)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInformationType);
        ArgumentNullException.ThrowIfNull(value);

        var column = mapping.Columns.SingleOrDefault(
            candidate => candidate.SourceInformationTypes.Contains(
                sourceInformationType,
                StringComparer.Ordinal));
        return column is null
            ? null
            : new MappedDatabaseValue(
                column.DatabaseTagName,
                sourceInformationType,
                value,
                sourceId);
    }

    private static string ResolveDatabaseTagName(
        string informationType,
        IReadOnlyDictionary<string, string> databaseTagOverrides)
    {
        if (!databaseTagOverrides.TryGetValue(informationType, out var databaseTagName))
        {
            return informationType;
        }

        if (string.IsNullOrWhiteSpace(databaseTagName))
        {
            throw new ArgumentException(
                $"The Database Tag Name override for '{informationType}' is invalid.",
                nameof(databaseTagOverrides));
        }

        return databaseTagName;
    }

    private sealed class MutableColumnMapping(string databaseTagName)
    {
        public string DatabaseTagName { get; } = databaseTagName;

        public List<string> SourceInformationTypes { get; } = [];
    }
}
