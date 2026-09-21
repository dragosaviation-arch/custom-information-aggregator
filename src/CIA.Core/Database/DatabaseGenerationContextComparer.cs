using CIA.Contracts.Database;

namespace CIA.Core.Database;

public static class DatabaseGenerationContextComparer
{
    public static bool Matches(
        DatabaseBuildSpecification expected,
        DatabaseGenerationSummary actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        return actual.IsHierarchyAware
            && expected.Datasets.Count == actual.Datasets.Count
            && expected.Datasets.Zip(actual.Datasets).All(pair =>
                pair.First.SourceSetId == pair.Second.SourceSetId
                && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
                && pair.First.Ordinal == pair.Second.Ordinal
                && pair.First.RepeatedDataLayout == pair.Second.RepeatedDataLayout
                && FieldMappingsMatch(pair.First.Fields, pair.Second.Mappings));
    }

    private static bool FieldMappingsMatch(
        IReadOnlyList<DatabaseFieldMapping> expected,
        IReadOnlyList<DatabaseFieldMapping> actual) =>
        expected.Count == actual.Count
        && expected.Zip(actual).All(pair =>
            pair.First.LogicalIdentity == pair.Second.LogicalIdentity
            && string.Equals(
                pair.First.EffectiveName,
                pair.Second.EffectiveName,
                StringComparison.Ordinal)
            && pair.First.IsExplicitOverride == pair.Second.IsExplicitOverride
            && pair.First.DetailedIdentities.SequenceEqual(pair.Second.DetailedIdentities));
}
