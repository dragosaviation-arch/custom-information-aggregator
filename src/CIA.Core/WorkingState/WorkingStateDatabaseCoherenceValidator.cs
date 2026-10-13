using CIA.Contracts.Database;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;
using CIA.Core.Database;
using CIA.Contracts.Hierarchy;

namespace CIA.Core.WorkingState;

public static class WorkingStateDatabaseCoherenceValidator
{
    public static void Validate(WorkingStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WorkingStateContractValidator.Validate(snapshot);

        if (snapshot.DatabaseGeneration is not { } generation)
        {
            return;
        }

        DatabaseBuildSpecification specification;
        try
        {
            specification = CreateBuildSpecification(snapshot);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The working-state configuration cannot reproduce its published Database context.",
                exception);
        }

        if (!DatabaseGenerationContextComparer.Matches(specification, generation))
        {
            throw new InvalidDataException(
                "The working-state Source Set and Discovery configuration does not match its published Database.");
        }
    }

    private static DatabaseBuildSpecification CreateBuildSpecification(WorkingStateSnapshot snapshot)
    {
        var sourceSetNames = snapshot.SourceSets.ToDictionary(
            sourceSet => sourceSet.SourceSetId,
            sourceSet => sourceSet.Name);
        var overrides = snapshot.DatabaseTagOverrides.ToDictionary(
            item => item.Identity,
            item => item.DatabaseTagName);
        if (snapshot.DatabaseGeneration is { IsRowFamilyAware: true } generation)
        {
            var assignments = generation.Datasets
                .SelectMany(dataset => dataset.Mappings)
                .SelectMany(mapping => mapping.DetailedIdentities.Select(identity =>
                    new KeyValuePair<CIA.Contracts.Discovery.DiscoveryInformationIdentity, RowFamilyId>(
                        identity,
                        mapping.RowFamilyId)))
                .GroupBy(pair => pair.Key)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.Value).Distinct().Single());

            if (generation.Datasets.All(dataset => dataset.OutputSetId is not null))
            {
                var outputPlan = snapshot.DiscoveryOutputPlan
                    ?? throw new ArgumentException(
                        "An Output Set-backed Database requires its saved Output Plan.",
                        nameof(snapshot));
                var publishedIdentities = assignments.Keys.ToHashSet();
                var publishedItems = snapshot.DiscoveryConfiguration.Items
                    .Where(item => publishedIdentities.Contains(item.Identity))
                    .ToArray();
                var layouts = snapshot.DiscoveryConfiguration.SourceSets.ToDictionary(
                    configuration => configuration.SourceSetId,
                    configuration => configuration.RepeatedDataLayout);
                var sources = snapshot.Sources
                    .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
                    .GroupBy(source => source.SourceSetId)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                var datasets = outputPlan.OutputSets
                    .Where(outputSet => outputSet.IsEnabled)
                    .OrderBy(outputSet => outputSet.SetNumber)
                    .Select((outputSet, index) =>
                    {
                        var actual = generation.Datasets[index];
                        var selectedFamilies = outputSet.RowFamilyIds.ToHashSet();
                        var fields = DatabaseTagMapper.CreateFieldMappings(
                                outputSet.ParentSourceSetId,
                                publishedItems,
                                overrides,
                                assignments)
                            .Where(field => selectedFamilies.Contains(field.RowFamilyId))
                            .ToArray();
                        return new DatabaseDatasetBuildSpecification(
                            outputSet.OutputSetId,
                            outputSet.ParentSourceSetId,
                            outputSet.Name,
                            index + 1,
                            layouts[outputSet.ParentSourceSetId],
                            sources[outputSet.ParentSourceSetId],
                            actual.RowFamilies.Select(family => new RowFamilyDefinition(
                                family.Id,
                                family.ParentRowFamilyId)).ToArray(),
                            fields);
                    })
                    .ToArray();
                return new DatabaseBuildSpecification(datasets);
            }

            var families = generation.Datasets
                .SelectMany(dataset => dataset.RowFamilies)
                .Select(family => new RowFamilyDefinition(family.Id, family.ParentRowFamilyId))
                .ToArray();
            return DatabaseBuildSpecificationFactory.Create(
                snapshot.DiscoveryConfiguration,
                overrides,
                snapshot.Sources,
                sourceSetNames,
                new CIA.Contracts.Discovery.DiscoveryFamilyCatalog(families),
                assignments);
        }

        return DatabaseBuildSpecificationFactory.Create(
            snapshot.DiscoveryConfiguration,
            overrides,
            snapshot.Sources,
            sourceSetNames);
    }
}
