using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Core.Database;

public static class DatabaseTagMapper
{
    public static DatabaseMappingSnapshot CreateMapping(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(overrides);
        var columns = new List<(string Name, List<string> Types)>();
        foreach (var item in configuration.Items
                     .Where(item => item.Disposition == DiscoveryInformationDisposition.Selected)
                     .OrderBy(item => item.InformationType, StringComparer.Ordinal))
        {
            var name = overrides.GetValueOrDefault(item.InformationType, item.InformationType);
            var index = columns.FindIndex(column => string.Equals(column.Name, name, StringComparison.Ordinal));
            if (index < 0)
            {
                columns.Add((name, [item.InformationType]));
            }
            else if (!columns[index].Types.Contains(item.InformationType, StringComparer.Ordinal))
            {
                columns[index].Types.Add(item.InformationType);
            }
        }
        return new DatabaseMappingSnapshot(columns
            .Select(column => new DatabaseColumnMapping(column.Name, column.Types)).ToArray());
    }

    public static MappedDatabaseValue? MapValue(
        DatabaseMappingSnapshot mapping,
        string informationType,
        string value,
        SourceId sourceId)
    {
        var column = mapping.Columns.SingleOrDefault(candidate =>
            candidate.SourceInformationTypes.Contains(informationType, StringComparer.Ordinal));
        return column is null
            ? null
            : new MappedDatabaseValue(column.DatabaseTagName, informationType, value, sourceId);
    }

    public static IReadOnlyList<DatabaseFieldMapping> CreateFieldMappings(
        SourceSetId sourceSetId,
        IEnumerable<DiscoveryConfigurationItem> configurationItems,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overridesByIdentity)
    {
        ArgumentNullException.ThrowIfNull(configurationItems);
        ArgumentNullException.ThrowIfNull(overridesByIdentity);

        var selected = configurationItems
            .Where(item => item.Disposition == DiscoveryInformationDisposition.Selected
                && item.Identity.SourceSetId == sourceSetId)
            .GroupBy(item => DatabaseLogicalFieldIdentity.Create(item.Identity))
            .OrderBy(group => group.Key.CandidateKind)
            .ThenBy(group => group.Key.CanonicalFieldIdentity, StringComparer.Ordinal)
            .ToArray();

        var mappings = new List<DatabaseFieldMapping>(selected.Length);
        foreach (var group in selected)
        {
            var identities = group.Select(item => item.Identity)
                .OrderBy(identity => identity.StructuralPath, StringComparer.Ordinal)
                .ThenBy(identity => identity.StructuralIdentity, StringComparer.Ordinal)
                .ThenBy(identity => identity.InformationType, StringComparer.Ordinal)
                .ToArray();
            var explicitNames = identities
                .Where(overridesByIdentity.ContainsKey)
                .Select(identity => overridesByIdentity[identity])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (explicitNames.Length > 1
                || (explicitNames.Length == 1
                    && identities.Any(identity => !overridesByIdentity.ContainsKey(identity))))
            {
                throw new ArgumentException(
                    "Detailed path variants of one logical field require one consistent Database Tag mapping.",
                    nameof(overridesByIdentity));
            }

            var isExplicitOverride = explicitNames.Length == 1;
            var effectiveName = isExplicitOverride
                ? explicitNames[0]
                : identities.Select(identity => identity.InformationType)
                    .Distinct(StringComparer.Ordinal)
                    .Single();
            mappings.Add(new DatabaseFieldMapping(
                group.Key,
                effectiveName,
                isExplicitOverride,
                identities));
        }

        return mappings;
    }

    public static DatabaseFieldMapping FindMapping(
        IReadOnlyList<DatabaseFieldMapping> mappings,
        DiscoveryInformationIdentity detailedIdentity)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(detailedIdentity);
        return mappings.Single(mapping => mapping.DetailedIdentities.Contains(detailedIdentity));
    }
}
