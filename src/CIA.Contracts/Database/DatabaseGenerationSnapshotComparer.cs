namespace CIA.Contracts.Database;

public static class DatabaseGenerationSnapshotComparer
{
    public static bool AreEquivalent(
        DatabaseGenerationSummary? expected,
        DatabaseGenerationSummary? actual)
    {
        if (expected is null || actual is null
            || expected.OperationId != actual.OperationId
            || expected.IsHierarchyAware != actual.IsHierarchyAware
            || expected.RowCount != actual.RowCount
            || expected.ValueCount != actual.ValueCount)
        {
            return false;
        }

        if (!expected.IsHierarchyAware)
        {
            return LegacyMappingsEqual(expected.Mapping, actual.Mapping);
        }

        return expected.Datasets.Count == actual.Datasets.Count
            && expected.Datasets.Zip(actual.Datasets).All(pair =>
                pair.First.SourceSetId == pair.Second.SourceSetId
                && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
                && pair.First.Ordinal == pair.Second.Ordinal
                && pair.First.RepeatedDataLayout == pair.Second.RepeatedDataLayout
                && pair.First.RowCount == pair.Second.RowCount
                && pair.First.ValueCount == pair.Second.ValueCount
                && ColumnsEqual(pair.First.Columns, pair.Second.Columns)
                && MappingsEqual(pair.First.Mappings, pair.Second.Mappings));
    }

    public static bool ColumnsEqual(
        IReadOnlyList<DatabaseColumnDefinition> expected,
        IReadOnlyList<DatabaseColumnDefinition> actual) =>
        expected.Count == actual.Count
        && expected.Zip(actual).All(pair =>
            pair.First.Identity == pair.Second.Identity
            && string.Equals(pair.First.EffectiveName, pair.Second.EffectiveName, StringComparison.Ordinal)
            && pair.First.Ordinal == pair.Second.Ordinal);

    private static bool MappingsEqual(
        IReadOnlyList<DatabaseFieldMapping> expected,
        IReadOnlyList<DatabaseFieldMapping> actual) =>
        expected.Count == actual.Count
        && expected.Zip(actual).All(pair =>
            pair.First.LogicalIdentity == pair.Second.LogicalIdentity
            && string.Equals(pair.First.EffectiveName, pair.Second.EffectiveName, StringComparison.Ordinal)
            && pair.First.IsExplicitOverride == pair.Second.IsExplicitOverride
            && pair.First.DetailedIdentities.SequenceEqual(pair.Second.DetailedIdentities));

    private static bool LegacyMappingsEqual(
        DatabaseMappingSnapshot expected,
        DatabaseMappingSnapshot actual) =>
        expected.Columns.Count == actual.Columns.Count
        && expected.Columns.Zip(actual.Columns).All(pair =>
            string.Equals(
                pair.First.DatabaseTagName,
                pair.Second.DatabaseTagName,
                StringComparison.Ordinal)
            && pair.First.SourceInformationTypes.SequenceEqual(
                pair.Second.SourceInformationTypes,
                StringComparer.Ordinal));
}
