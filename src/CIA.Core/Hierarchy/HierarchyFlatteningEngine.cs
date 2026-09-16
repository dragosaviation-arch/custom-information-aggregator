using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.Core.Hierarchy;

public interface IHierarchyFlatteningEngine
{
    ValueTask<HierarchyFlatteningResult> FlattenAsync(
        HierarchyFlatteningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HierarchyFlatteningEngine : IHierarchyFlatteningEngine
{
    private readonly HierarchyFlatteningOptions options;

    public HierarchyFlatteningEngine(HierarchyFlatteningOptions? options = null)
    {
        this.options = options ?? new HierarchyFlatteningOptions();
    }

    public async ValueTask<HierarchyFlatteningResult> FlattenAsync(
        HierarchyFlatteningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var flattened = new List<(SourceId SourceId, RowFragment Fragment)>();
        var checkpoint = new CancellationCheckpoint();

        foreach (var source in request.Sources.OrderBy(source => source.SourceId.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var root in BuildSourceTrees(source))
            {
                var fragments = await FlattenNodeAsync(
                        root,
                        request.Layout,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (request.Layout == RepeatedDataLayout.AllCombinations
                    && flattened.Count > options.MaximumAllCombinationRows - fragments.Count)
                {
                    throw new AllCombinationsLimitExceededException(
                        (long)flattened.Count + fragments.Count,
                        options.MaximumAllCombinationRows);
                }

                flattened.AddRange(fragments.Select(fragment => (source.SourceId, fragment)));
            }
        }

        var rows = flattened
            .Select((item, index) => new FlattenedHierarchyRow(
                checked(index + 1),
                item.SourceId,
                OrderCells(item.Fragment.Cells)))
            .ToArray();

        return new HierarchyFlatteningResult(request.SourceSetId, request.Layout, rows);
    }

    private async ValueTask<IReadOnlyList<RowFragment>> FlattenNodeAsync(
        SourceTreeNode node,
        RepeatedDataLayout layout,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fixedCells = node.Occurrences
            .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
            .Select(CreateCell)
            .ToList();
        var variableBranches = new List<VariableBranch>();

        foreach (var siblingGroup in node.Children
                     .OrderBy(child => child.Element.SiblingPosition)
                     .ThenBy(child => child.Element.InstanceId)
                     .GroupBy(child => child.Element.ExpandedName, StringComparer.Ordinal))
        {
            var siblings = siblingGroup.ToArray();
            if (IsRepeatedFamily(siblings))
            {
                var repeatedBranch = new List<RowFragment>();
                foreach (var sibling in siblings)
                {
                    var siblingFragments = await FlattenNodeAsync(
                            sibling,
                            layout,
                            checkpoint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    repeatedBranch.AddRange(siblingFragments);
                }

                if (repeatedBranch.Count > 0)
                {
                    variableBranches.Add(new VariableBranch(
                        repeatedBranch,
                        IsDirectRepeatedFamily: true));
                }

                continue;
            }

            foreach (var child in siblings)
            {
                var childFragments = await FlattenNodeAsync(
                        child,
                        layout,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (childFragments.Count == 1
                    && childFragments[0].Cells.All(
                        cell => cell.ColumnIdentity.RepeatOrdinal is null))
                {
                    fixedCells.AddRange(childFragments[0].Cells);
                }
                else if (childFragments.Count > 0)
                {
                    variableBranches.Add(new VariableBranch(
                        childFragments,
                        IsDirectRepeatedFamily: false));
                }
            }
        }

        if (variableBranches.Count == 0)
        {
            return fixedCells.Count == 0
                ? Array.Empty<RowFragment>()
                : [new RowFragment(fixedCells)];
        }

        if (variableBranches.Count == 1
            && !variableBranches[0].IsDirectRepeatedFamily)
        {
            return variableBranches[0].Rows
                .Select(fragment => Merge(fixedCells, fragment.Cells))
                .ToArray();
        }

        var canAssociateBranches = variableBranches.Count == 1
                                   || (node.Parent is not null
                                       && variableBranches.All(branch =>
                                           branch.IsDirectRepeatedFamily
                                           || branch.IsSingleFieldSequence));
        var branchRows = variableBranches.Select(branch => branch.Rows).ToArray();
        if (!canAssociateBranches)
        {
            return await KeepIndependentFamiliesAsync(
                    fixedCells,
                    branchRows,
                    layout,
                    checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return layout switch
        {
            RepeatedDataLayout.AlignRepeatedGroupsByPosition =>
                await AlignByPositionAsync(
                        fixedCells,
                        branchRows,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.StructuralRows =>
                await CreateStructuralRowsAsync(
                        fixedCells,
                        branchRows,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.AllCombinations =>
                await CreateAllCombinationsAsync(
                        fixedCells,
                        branchRows,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns =>
                await NumberIntoColumnsAsync(
                        fixedCells,
                        branchRows,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null)
        };
    }

    private async ValueTask<IReadOnlyList<RowFragment>> KeepIndependentFamiliesAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        RepeatedDataLayout layout,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var rows = new List<RowFragment>();
        foreach (var branch in branches)
        {
            await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (layout == RepeatedDataLayout.NumberRepeatedValuesIntoColumns)
            {
                var numbered = await NumberIntoColumnsAsync(
                        fixedCells,
                        [branch],
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                rows.AddRange(numbered);
            }
            else
            {
                rows.AddRange(branch.Select(fragment => Merge(fixedCells, fragment.Cells)));
            }
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<RowFragment>> AlignByPositionAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var rowCount = branches.Max(branch => branch.Count);
        var rows = new List<RowFragment>(rowCount);
        for (var index = 0; index < rowCount; index++)
        {
            await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
            var cells = new List<FlattenedHierarchyCell>(fixedCells);
            foreach (var branch in branches)
            {
                if (index < branch.Count)
                {
                    cells.AddRange(branch[index].Cells);
                }
            }

            rows.Add(new RowFragment(cells));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<RowFragment>> CreateStructuralRowsAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var rows = new List<RowFragment>();
        foreach (var branch in branches)
        {
            foreach (var fragment in branch)
            {
                await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
                rows.Add(Merge(fixedCells, fragment.Cells));
            }
        }

        return rows;
    }

    private async ValueTask<IReadOnlyList<RowFragment>> CreateAllCombinationsAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        long projectedRows = 1;
        foreach (var branch in branches)
        {
            await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (branch.Count == 0)
            {
                return Array.Empty<RowFragment>();
            }

            if (projectedRows > long.MaxValue / branch.Count)
            {
                throw new AllCombinationsLimitExceededException(
                    projectedRows: null,
                    options.MaximumAllCombinationRows);
            }

            projectedRows *= branch.Count;
            if (projectedRows > options.MaximumAllCombinationRows)
            {
                throw new AllCombinationsLimitExceededException(
                    projectedRows,
                    options.MaximumAllCombinationRows);
            }
        }

        var combinations = new List<RowFragment> { new(fixedCells) };
        foreach (var branch in branches)
        {
            var expanded = new List<RowFragment>(checked(combinations.Count * branch.Count));
            foreach (var combination in combinations)
            {
                foreach (var fragment in branch)
                {
                    await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
                    expanded.Add(Merge(combination.Cells, fragment.Cells));
                }
            }

            combinations = expanded;
        }

        return combinations;
    }

    private static async ValueTask<IReadOnlyList<RowFragment>> NumberIntoColumnsAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var cells = new List<FlattenedHierarchyCell>(fixedCells);
        foreach (var branch in branches)
        {
            for (var index = 0; index < branch.Count; index++)
            {
                await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
                var repeatOrdinal = checked(index + 1);
                foreach (var cell in branch[index].Cells)
                {
                    cells.Add(cell.ColumnIdentity.RepeatOrdinal is null
                        ? WithRepeatOrdinal(cell, repeatOrdinal)
                        : cell);
                }
            }
        }

        return [new RowFragment(cells)];
    }

    private static bool IsRepeatedFamily(IReadOnlyList<SourceTreeNode> siblings)
    {
        if (siblings.Count < 2)
        {
            return false;
        }

        var nonAttributeIdentities = siblings
            .SelectMany(sibling => sibling.DescendantIdentities)
            .Where(identity => identity.CandidateKind != SourceValueCandidateKind.Attribute)
            .ToHashSet();
        var representedBySibling = siblings
            .Select(sibling => sibling.DescendantIdentities
                .Where(identity => nonAttributeIdentities.Count == 0
                                   || identity.CandidateKind
                                   != SourceValueCandidateKind.Attribute)
                .ToHashSet())
            .ToArray();
        return representedBySibling
            .SelectMany(identities => identities)
            .Distinct()
            .Any(identity => representedBySibling.Count(identities => identities.Contains(identity)) > 1);
    }

    private static IReadOnlyList<SourceTreeNode> BuildSourceTrees(HierarchySourceInput source)
    {
        var nodesById = new Dictionary<long, SourceTreeNode>();
        var roots = new Dictionary<long, SourceTreeNode>();

        foreach (var occurrence in source.Occurrences
                     .OrderBy(occurrence => occurrence.Lineage.TraversalOrder))
        {
            SourceTreeNode? parent = null;
            foreach (var element in occurrence.Lineage.ElementPath)
            {
                if (!nodesById.TryGetValue(element.InstanceId, out var node))
                {
                    node = new SourceTreeNode(element, parent);
                    nodesById.Add(element.InstanceId, node);
                    if (parent is null)
                    {
                        roots.Add(element.InstanceId, node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }
                else if (!HasSameElementIdentity(node.Element, element)
                         || node.Parent?.Element.InstanceId != parent?.Element.InstanceId)
                {
                    throw new ArgumentException(
                        "Selected source lineage contains inconsistent element instances.",
                        nameof(source));
                }

                parent = node;
            }

            parent!.Occurrences.Add(occurrence);
        }

        return roots.Values
            .OrderBy(root => root.Element.SiblingPosition)
            .ThenBy(root => root.Element.InstanceId)
            .ToArray();
    }

    private static bool HasSameElementIdentity(
        SourceElementInstance first,
        SourceElementInstance second)
    {
        return first.InstanceId == second.InstanceId
               && first.SiblingPosition == second.SiblingPosition
               && string.Equals(first.ExpandedName, second.ExpandedName, StringComparison.Ordinal);
    }

    private static FlattenedHierarchyCell CreateCell(HierarchySourceOccurrence occurrence)
    {
        return new FlattenedHierarchyCell(
            new FlattenedColumnIdentity(occurrence.Identity),
            occurrence.Value,
            occurrence.SourceId,
            occurrence.Lineage);
    }

    private static FlattenedHierarchyCell WithRepeatOrdinal(
        FlattenedHierarchyCell cell,
        int repeatOrdinal)
    {
        return new FlattenedHierarchyCell(
            new FlattenedColumnIdentity(cell.DetailedIdentity, repeatOrdinal),
            cell.Value,
            cell.SourceId,
            cell.Lineage);
    }

    private static RowFragment Merge(
        IReadOnlyList<FlattenedHierarchyCell> first,
        IReadOnlyList<FlattenedHierarchyCell> second)
    {
        return new RowFragment(first.Concat(second).ToArray());
    }

    private static IEnumerable<FlattenedHierarchyCell> OrderCells(
        IEnumerable<FlattenedHierarchyCell> cells)
    {
        return cells
            .OrderBy(cell => cell.Lineage.TraversalOrder)
            .ThenBy(cell => cell.DetailedIdentity.StructuralPath, StringComparer.Ordinal)
            .ThenBy(cell => cell.DetailedIdentity.StructuralIdentity, StringComparer.Ordinal)
            .ThenBy(cell => cell.DetailedIdentity.CandidateKind)
            .ThenBy(cell => cell.ColumnIdentity.RepeatOrdinal ?? 0);
    }

    private sealed record RowFragment(IReadOnlyList<FlattenedHierarchyCell> Cells);

    private sealed record VariableBranch(
        IReadOnlyList<RowFragment> Rows,
        bool IsDirectRepeatedFamily)
    {
        public bool IsSingleFieldSequence => Rows.Count > 0
            && Rows
                .SelectMany(row => row.Cells)
                .Select(cell => cell.DetailedIdentity)
                .Distinct()
                .Count() == 1;
    }

    private sealed class SourceTreeNode(
        SourceElementInstance element,
        SourceTreeNode? parent)
    {
        private HashSet<DiscoveryInformationIdentity>? descendantIdentities;

        public SourceElementInstance Element { get; } = element;

        public SourceTreeNode? Parent { get; } = parent;

        public List<SourceTreeNode> Children { get; } = [];

        public List<HierarchySourceOccurrence> Occurrences { get; } = [];

        public HashSet<DiscoveryInformationIdentity> DescendantIdentities =>
            descendantIdentities ??= Occurrences
                .Select(occurrence => occurrence.Identity)
                .Concat(Children.SelectMany(child => child.DescendantIdentities))
                .ToHashSet();
    }

    private sealed class CancellationCheckpoint
    {
        private int operationCount;

        public async ValueTask CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationCount = operationCount == int.MaxValue ? 1 : operationCount + 1;
            if (operationCount % 256 != 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
