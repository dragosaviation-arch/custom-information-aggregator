using CIA.Contracts.Database;
using CIA.Contracts.WorkingState;
using CIA.Core.Database;

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
        return DatabaseBuildSpecificationFactory.Create(
            snapshot.DiscoveryConfiguration,
            overrides,
            snapshot.Sources,
            sourceSetNames);
    }
}
