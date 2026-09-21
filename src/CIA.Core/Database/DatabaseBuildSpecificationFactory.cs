using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Core.Database;

public static class DatabaseBuildSpecificationFactory
{
    public static DatabaseBuildSpecification Create(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceSetNames);

        var includedReadySources = sources
            .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>();
        foreach (var setConfiguration in configuration.SourceSets)
        {
            var fields = DatabaseTagMapper.CreateFieldMappings(
                setConfiguration.SourceSetId,
                configuration.Items,
                overrides);
            var datasetSources = includedReadySources
                .Where(source => source.SourceSetId == setConfiguration.SourceSetId)
                .ToArray();
            if (fields.Count == 0 || datasetSources.Length == 0)
            {
                continue;
            }

            datasets.Add(new DatabaseDatasetBuildSpecification(
                setConfiguration.SourceSetId,
                sourceSetNames.GetValueOrDefault(
                    setConfiguration.SourceSetId,
                    $"Set {datasets.Count + 1}"),
                datasets.Count + 1,
                setConfiguration.RepeatedDataLayout,
                datasetSources,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }
}
