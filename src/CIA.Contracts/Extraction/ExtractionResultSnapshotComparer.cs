using CIA.Contracts.Database;

namespace CIA.Contracts.Extraction;

public static class ExtractionResultSnapshotComparer
{
    public static bool AreEquivalent(
        ExtractionResultSummary? left,
        ExtractionResultSummary? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null
            || left.OperationId != right.OperationId
            || !DatabaseGenerationSnapshotComparer.AreEquivalent(
                left.DatabaseGeneration,
                right.DatabaseGeneration)
            || left.Datasets.Count != right.Datasets.Count)
        {
            return false;
        }

        return left.Datasets.Zip(right.Datasets).All(pair =>
            pair.First.SourceSetId == pair.Second.SourceSetId
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
            && pair.First.Ordinal == pair.Second.Ordinal
            && pair.First.RepeatedDataLayout == pair.Second.RepeatedDataLayout
            && pair.First.RowCount == pair.Second.RowCount
            && pair.First.ValueCount == pair.Second.ValueCount
            && DatabaseGenerationSnapshotComparer.ColumnsEqual(
                pair.First.Columns,
                pair.Second.Columns)
            && FamiliesEqual(pair.First.RowFamilies, pair.Second.RowFamilies));
    }

    private static bool FamiliesEqual(
        IReadOnlyList<ExtractionRowFamilySummary> left,
        IReadOnlyList<ExtractionRowFamilySummary> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.ParentRowFamilyId == pair.Second.ParentRowFamilyId
            && pair.First.Ordinal == pair.Second.Ordinal
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal)
            && pair.First.DatabaseRowCount == pair.Second.DatabaseRowCount
            && pair.First.RowCount == pair.Second.RowCount
            && pair.First.ExcludedRowCount == pair.Second.ExcludedRowCount
            && pair.First.ValueCount == pair.Second.ValueCount
            && DatabaseGenerationSnapshotComparer.ColumnsEqual(
                pair.First.Columns,
                pair.Second.Columns));
}
