using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Core.Database;

public static class DatabaseBuildSpecificationFactory
{
    public static DatabaseBuildSpecification CreateApprovedOutputSets(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames,
        DiscoveryFamilyCatalog familyCatalog,
        IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> familyAssignments,
        DiscoveryOutputPlanSnapshot outputPlan)
    {
        ArgumentNullException.ThrowIfNull(outputPlan);
        var parentDatasets = Create(
                configuration,
                overrides,
                sources,
                sourceSetNames,
                familyCatalog,
                familyAssignments)
            .Datasets
            .ToDictionary(dataset => dataset.SourceSetId);
        var enabledSets = outputPlan.OutputSets
            .Where(outputSet => outputSet.IsEnabled)
            .OrderBy(outputSet => outputSet.SetNumber)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>(enabledSets.Length);
        foreach (var outputSet in enabledSets)
        {
            if (!parentDatasets.TryGetValue(outputSet.ParentSourceSetId, out var parent))
            {
                throw new ArgumentException(
                    "An enabled Output Set has no current parent Source Set dataset.",
                    nameof(outputPlan));
            }

            var selectedFamilies = outputSet.RowFamilyIds.ToHashSet();
            var families = parent.RowFamilies
                .Where(family => selectedFamilies.Contains(family.Id))
                .ToArray();
            var fields = parent.Fields
                .Where(field => selectedFamilies.Contains(field.RowFamilyId))
                .ToArray();
            if (families.Length != selectedFamilies.Count
                || fields.Length == 0
                || fields.Any(field => !selectedFamilies.Contains(field.RowFamilyId)))
            {
                throw new ArgumentException(
                    "An enabled Output Set no longer matches its approved field projection.",
                    nameof(outputPlan));
            }

            datasets.Add(new DatabaseDatasetBuildSpecification(
                outputSet.OutputSetId,
                outputSet.ParentSourceSetId,
                outputSet.Name,
                datasets.Count + 1,
                parent.RepeatedDataLayout,
                parent.Sources,
                families,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }

    public static DatabaseBuildSpecification Create(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames,
        DiscoveryFamilyCatalog familyCatalog,
        IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> familyAssignments)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceSetNames);
        ArgumentNullException.ThrowIfNull(familyCatalog);
        ArgumentNullException.ThrowIfNull(familyAssignments);

        var includedReadySources = sources
            .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>();
        foreach (var setConfiguration in configuration.SourceSets)
        {
            var fields = DatabaseTagMapper.CreateFieldMappings(
                setConfiguration.SourceSetId,
                configuration.Items,
                overrides,
                familyAssignments);
            var datasetSources = includedReadySources
                .Where(source => source.SourceSetId == setConfiguration.SourceSetId)
                .ToArray();
            if (fields.Count == 0 || datasetSources.Length == 0)
            {
                continue;
            }

            var selectedFamilyIds = fields.Select(field => field.RowFamilyId).ToHashSet();
            var families = familyCatalog.Families
                .Where(family => selectedFamilyIds.Contains(family.Id))
                .OrderBy(family => family.Id)
                .ToArray();

            datasets.Add(new DatabaseDatasetBuildSpecification(
                setConfiguration.SourceSetId,
                sourceSetNames.GetValueOrDefault(
                    setConfiguration.SourceSetId,
                    $"Set {datasets.Count + 1}"),
                datasets.Count + 1,
                setConfiguration.RepeatedDataLayout,
                datasetSources,
                families,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }

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
