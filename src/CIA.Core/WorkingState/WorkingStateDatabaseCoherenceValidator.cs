using CIA.Contracts.Database;
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
            var families = generation.Datasets
                .SelectMany(dataset => dataset.RowFamilies)
                .Select(family => new RowFamilyDefinition(family.Id, family.ParentRowFamilyId))
                .ToArray();
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
