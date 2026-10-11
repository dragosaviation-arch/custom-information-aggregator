using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
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

        if (request.RowFamilies.Count > 0)
        {
            return await FlattenFamiliesAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var flattened = new List<(SourceId SourceId, RowFragment Fragment)>();
        var checkpoint = new CancellationCheckpoint();
        var tracker = new ProjectionLimitTracker();
        var maximumRows = request.MaximumRowsPerFamily;

        foreach (var source in request.Sources.OrderBy(source => source.SourceId.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = BuildSourceTrees(source);
            var repeatedFamilyPaths = FindRepeatedFamilyPaths(roots);
            foreach (var root in roots)
            {
                var fragments = await FlattenNodeAsync(
                        root,
                        repeatedFamilyPaths,
                        request.Layout,
                        maximumRows,
                        tracker,
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

                var remaining = maximumRows is null
                    ? int.MaxValue
                    : Math.Max(0, maximumRows.Value - flattened.Count);
                if (fragments.Count > remaining)
                {
                    tracker.MarkTruncated();
                }

                flattened.AddRange(fragments.Take(remaining)
                    .Select(fragment => (source.SourceId, fragment)));
                if (maximumRows is not null && flattened.Count >= maximumRows.Value)
                {
                    tracker.MarkTruncated();
                    break;
                }
            }

            if (maximumRows is not null && flattened.Count >= maximumRows.Value)
            {
                break;
            }
        }

        var rows = flattened
            .Select((item, index) => new FlattenedHierarchyRow(
                checked(index + 1),
                item.SourceId,
                OrderCells(item.Fragment.Cells)))
            .ToArray();

        return new HierarchyFlatteningResult(
            request.SourceSetId,
            request.Layout,
            rows,
            tracker.WasTruncated);
    }

    private async ValueTask<HierarchyFlatteningResult> FlattenFamiliesAsync(
        HierarchyFlatteningRequest request,
        CancellationToken cancellationToken)
    {
        var rows = new List<FlattenedHierarchyRow>();
        var familyRowCounts = new Dictionary<RowFamilyId, int>();
        var truncatedFamilies = new HashSet<RowFamilyId>();
        foreach (var source in request.Sources.OrderBy(source => source.SourceId.Value))
        {
            foreach (var family in request.RowFamilies.OrderBy(family => family.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentFamilyCount = familyRowCounts.GetValueOrDefault(family.Id);
                if (request.MaximumRowsPerFamily is { } familyLimit
                    && currentFamilyCount >= familyLimit)
                {
                    truncatedFamilies.Add(family.Id);
                    continue;
                }

                var direct = source.Occurrences
                    .Where(occurrence => request.FamilyAssignments[occurrence.Identity] == family.Id)
                    .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
                    .ToArray();
                var anchorGroups = direct
                    .Select(occurrence => (Occurrence: occurrence, Anchor: FindAnchor(occurrence, family.Id)))
                    .GroupBy(item => item.Anchor.InstanceId)
                    .OrderBy(group => group.Min(item => item.Occurrence.Lineage.TraversalOrder))
                    .ToArray();

                for (var anchorIndex = 0; anchorIndex < anchorGroups.Length; anchorIndex++)
                {
                    if (request.MaximumRowsPerFamily is { } maximumRows
                        && currentFamilyCount >= maximumRows)
                    {
                        truncatedFamilies.Add(family.Id);
                        break;
                    }

                    var anchorGroup = anchorGroups[anchorIndex];
                    var anchor = anchorGroup.First().Anchor;
                    var familyOccurrences = anchorGroup.Select(item => item.Occurrence).ToArray();
                    var ancestorContext = FindAncestorContext(
                        source.Occurrences,
                        request.FamilyAssignments,
                        family,
                        anchorGroup.First().Occurrence)
                        .Select(CreateCell)
                        .ToArray();

                    var legacyResult = await FlattenAsync(
                        new HierarchyFlatteningRequest(
                            request.SourceSetId,
                            request.Layout,
                            [new HierarchySourceInput(source.SourceId, familyOccurrences)],
                            [],
                            new Dictionary<DiscoveryInformationIdentity, RowFamilyId>(),
                            request.MaximumRowsPerFamily is null
                                ? null
                                : request.MaximumRowsPerFamily.Value - currentFamilyCount),
                        cancellationToken).ConfigureAwait(false);
                    if (legacyResult.IsTruncated)
                    {
                        truncatedFamilies.Add(family.Id);
                    }
                    if (request.Layout == RepeatedDataLayout.AllCombinations
                        && rows.Count > options.MaximumAllCombinationRows - legacyResult.Rows.Count)
                    {
                        throw new AllCombinationsLimitExceededException(
                            (long)rows.Count + legacyResult.Rows.Count,
                            options.MaximumAllCombinationRows);
                    }

                    foreach (var projected in legacyResult.Rows)
                    {
                        rows.Add(new FlattenedHierarchyRow(
                            rows.Count + 1,
                            source.SourceId,
                            OrderCells(projected.Cells.Concat(ancestorContext)),
                            family.Id,
                            anchor.InstanceId,
                            projected.Ordinal));
                        currentFamilyCount++;
                        familyRowCounts[family.Id] = currentFamilyCount;
                    }
                }
            }
        }

        return new HierarchyFlatteningResult(
            request.SourceSetId,
            request.Layout,
            rows,
            truncatedFamilies.Count > 0,
            truncatedFamilies);
    }

    private static SourceElementInstance FindAnchor(
        HierarchySourceOccurrence occurrence,
        RowFamilyId familyId)
    {
        var path = string.Empty;
        foreach (var element in occurrence.Lineage.ElementPath)
        {
            path += "/" + element.ExpandedName;
            if (string.Equals(path, familyId.AnchorPath, StringComparison.Ordinal))
            {
                return element;
            }
        }

        throw new InvalidOperationException(
            "A selected occurrence does not contain its assigned Row Family anchor.");
    }

    private static IEnumerable<HierarchySourceOccurrence> FindAncestorContext(
        IReadOnlyList<HierarchySourceOccurrence> occurrences,
        IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> assignments,
        RowFamilyDefinition family,
        HierarchySourceOccurrence anchoredOccurrence)
    {
        if (family.ParentRowFamilyId is null)
        {
            return [];
        }

        var ancestorIds = assignments.Values
            .Where(candidate => candidate.SourceSetId == family.Id.SourceSetId
                && family.Id.AnchorPath.StartsWith(
                    candidate.AnchorPath + "/",
                    StringComparison.Ordinal))
            .ToHashSet();

        var anchorInstances = ancestorIds.ToDictionary(
            ancestor => ancestor,
            ancestor => FindAnchor(anchoredOccurrence, ancestor).InstanceId);
        return occurrences
            .Where(occurrence => assignments.TryGetValue(occurrence.Identity, out var owner)
                && ancestorIds.Contains(owner)
                && FindAnchor(occurrence, owner).InstanceId == anchorInstances[owner])
            .GroupBy(occurrence => occurrence.Identity)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<RowFragment>> FlattenNodeAsync(
        SourceTreeNode node,
        IReadOnlySet<string> repeatedFamilyPaths,
        RepeatedDataLayout layout,
        int? maximumRows,
        ProjectionLimitTracker tracker,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fixedCells = node.Occurrences
            .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
            .Select(CreateCell)
            .ToList();
        var childBranches = new List<VariableBranch>();
        var variableBranches = new List<VariableBranch>();
        var structuralBranchGroup = 0;

        foreach (var siblingGroup in node.Children
                     .OrderBy(child => child.Element.SiblingPosition)
                     .ThenBy(child => child.Element.InstanceId)
                     .GroupBy(child => child.Element.ExpandedName, StringComparer.Ordinal))
        {
            structuralBranchGroup++;
            var siblings = siblingGroup.ToArray();
            var isRepeatedFamily = IsRepeatedFamily(siblings, repeatedFamilyPaths);
            var representsTypedStructuralSlots = isRepeatedFamily
                && ShouldRepresentTypedStructuralSlotsAsRecordFields(
                    node,
                    siblings,
                    repeatedFamilyPaths);
            if (isRepeatedFamily && !representsTypedStructuralSlots)
            {
                var repeatedBranch = new List<RowFragment>();
                for (var siblingIndex = 0; siblingIndex < siblings.Length; siblingIndex++)
                {
                    var sibling = siblings[siblingIndex];
                    var siblingFragments = await FlattenNodeAsync(
                            sibling,
                            repeatedFamilyPaths,
                            layout,
                            maximumRows,
                            tracker,
                            checkpoint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var projectedFragments = layout == RepeatedDataLayout.NumberRepeatedValuesIntoColumns
                        ? siblingFragments.Select(fragment =>
                            PrependRepeatCoordinate(
                                fragment,
                                checked(siblingIndex + 1)))
                        : siblingFragments;
                    AddBounded(repeatedBranch, projectedFragments, maximumRows, tracker);
                    if (maximumRows is { } repeatedLimit
                        && repeatedBranch.Count >= repeatedLimit)
                    {
                        tracker.MarkTruncated();
                        break;
                    }
                }

                if (repeatedBranch.Count > 0)
                {
                    childBranches.Add(new VariableBranch(
                        repeatedBranch,
                        IsDirectRepeatedFamily: true,
                        structuralBranchGroup));
                }

                continue;
            }

            // SPR-148 has already identified typed structural slots as distinct fields
            // within the repeated parent record. Their repetition remains recognized,
            // while their values stay associated with that enclosing record instance.

            foreach (var child in siblings)
            {
                var childFragments = await FlattenNodeAsync(
                        child,
                        repeatedFamilyPaths,
                        layout,
                        maximumRows,
                        tracker,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (childFragments.Count > 0)
                {
                    childBranches.Add(new VariableBranch(
                        childFragments,
                        IsDirectRepeatedFamily: false,
                        structuralBranchGroup));
                }
            }
        }

        // A friendly field can span different ancestry (for example, two distinct pnr
        // branches). Keep those branches as row alternatives while retaining one logical
        // Database field; repeated siblings in the same structural group remain unaffected.
        var structuralAlternativeBranches = FindStructuralAlternativeBranches(childBranches);
        for (var index = 0; index < childBranches.Count; index++)
        {
            var branch = childBranches[index];
            if (!structuralAlternativeBranches.Contains(index)
                && branch.Rows.Count == 1
                && branch.Rows[0].Cells.All(
                    cell => cell.ColumnIdentity.RepeatCoordinates.Count == 0))
            {
                fixedCells.AddRange(branch.Rows[0].Cells);
            }
            else
            {
                variableBranches.Add(branch);
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
            var projected = variableBranches[0].Rows
                .Select(fragment => Merge(fixedCells, fragment.Cells))
                .Take(maximumRows ?? int.MaxValue)
                .ToArray();
            if (projected.Length < variableBranches[0].Rows.Count)
            {
                tracker.MarkTruncated();
            }

            return projected;
        }

        var canAssociateBranches = structuralAlternativeBranches.Count == 0
                                   && (variableBranches.Count == 1
                                   || (node.Parent is not null
                                       && variableBranches.All(branch =>
                                           branch.IsDirectRepeatedFamily
                                           || branch.IsSingleFieldSequence)));
        var branchRows = variableBranches.Select(branch => branch.Rows).ToArray();
        if (!canAssociateBranches)
        {
            return await KeepIndependentFamiliesAsync(
                    fixedCells,
                    branchRows,
                    layout,
                    maximumRows,
                    tracker,
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
                        maximumRows,
                        tracker,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.StructuralRows =>
                await CreateStructuralRowsAsync(
                        fixedCells,
                        branchRows,
                        maximumRows,
                        tracker,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.AllCombinations =>
                await CreateAllCombinationsAsync(
                        fixedCells,
                        branchRows,
                        maximumRows,
                        tracker,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false),
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns =>
                await NumberIntoColumnsAsync(
                        fixedCells,
                        branchRows,
                        maximumRows,
                        tracker,
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
        int? maximumRows,
        ProjectionLimitTracker tracker,
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
                        maximumRows,
                        tracker,
                        checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddBounded(rows, numbered, maximumRows, tracker);
            }
            else
            {
                AddBounded(
                    rows,
                    branch.Select(fragment => Merge(fixedCells, fragment.Cells)),
                    maximumRows,
                    tracker);
            }

            if (maximumRows is { } rowLimit && rows.Count >= rowLimit)
            {
                tracker.MarkTruncated();
                break;
            }
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<RowFragment>> AlignByPositionAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        int? maximumRows,
        ProjectionLimitTracker tracker,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var projectedRowCount = branches.Max(branch => branch.Count);
        var rowCount = Math.Min(projectedRowCount, maximumRows ?? int.MaxValue);
        if (rowCount < projectedRowCount)
        {
            tracker.MarkTruncated();
        }
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
        int? maximumRows,
        ProjectionLimitTracker tracker,
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
                if (maximumRows is { } rowLimit && rows.Count >= rowLimit)
                {
                    tracker.MarkTruncated();
                    return rows;
                }
            }
        }

        return rows;
    }

    private async ValueTask<IReadOnlyList<RowFragment>> CreateAllCombinationsAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        int? maximumRows,
        ProjectionLimitTracker tracker,
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
            var targetCount = checked(combinations.Count * branch.Count);
            var expanded = new List<RowFragment>(Math.Min(
                targetCount,
                maximumRows ?? targetCount));
            foreach (var combination in combinations)
            {
                foreach (var fragment in branch)
                {
                    await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
                    expanded.Add(Merge(combination.Cells, fragment.Cells));
                    if (maximumRows is { } rowLimit && expanded.Count >= rowLimit)
                    {
                        if (targetCount > rowLimit)
                        {
                            tracker.MarkTruncated();
                        }

                        break;
                    }
                }

                if (maximumRows is { } expandedLimit && expanded.Count >= expandedLimit)
                {
                    break;
                }
            }

            combinations = expanded;
        }

        return combinations;
    }

    private static async ValueTask<IReadOnlyList<RowFragment>> NumberIntoColumnsAsync(
        IReadOnlyList<FlattenedHierarchyCell> fixedCells,
        IReadOnlyList<IReadOnlyList<RowFragment>> branches,
        int? maximumRows,
        ProjectionLimitTracker tracker,
        CancellationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var cells = new List<FlattenedHierarchyCell>(fixedCells);
        foreach (var branch in branches)
        {
            foreach (var fragment in branch)
            {
                await checkpoint.CheckAsync(cancellationToken).ConfigureAwait(false);
                cells.AddRange(fragment.Cells);
            }
        }

        return [new RowFragment(cells)];
    }

    private static void AddBounded(
        List<RowFragment> target,
        IEnumerable<RowFragment> values,
        int? maximumRows,
        ProjectionLimitTracker tracker)
    {
        foreach (var value in values)
        {
            if (maximumRows is { } limit && target.Count >= limit)
            {
                tracker.MarkTruncated();
                return;
            }

            target.Add(value);
        }
    }

    private static bool IsRepeatedFamily(
        IReadOnlyList<SourceTreeNode> siblings,
        IReadOnlySet<string> repeatedFamilyPaths)
    {
        return siblings.Count > 0
               && repeatedFamilyPaths.Contains(siblings[0].StructuralPath);
    }

    private static bool ShouldRepresentTypedStructuralSlotsAsRecordFields(
        SourceTreeNode parent,
        IReadOnlyList<SourceTreeNode> siblings,
        IReadOnlySet<string> repeatedFamilyPaths)
    {
        if (siblings.Count < 2 || !repeatedFamilyPaths.Contains(parent.StructuralPath))
        {
            return false;
        }

        var structuralBySibling = siblings
            .Select(sibling => sibling.DescendantIdentities
                .Where(identity => identity.CandidateKind == SourceValueCandidateKind.Structural)
                .ToHashSet())
            .ToArray();
        var structuralIdentities = structuralBySibling
            .SelectMany(identities => identities)
            .Distinct()
            .ToArray();

        return structuralIdentities.Length > 0
               && structuralIdentities.All(identity =>
                   structuralBySibling.Count(identities => identities.Contains(identity)) == 1);
    }

    private static IReadOnlySet<int> FindStructuralAlternativeBranches(
        IReadOnlyList<VariableBranch> branches)
    {
        var logicalIdentities = branches.Select(branch => branch.Rows
                .SelectMany(row => row.Cells)
                .Select(cell => DatabaseLogicalFieldIdentity.Create(cell.DetailedIdentity))
                .ToHashSet())
            .ToArray();
        var alternatives = new HashSet<int>();
        for (var first = 0; first < logicalIdentities.Length; first++)
        {
            for (var second = first + 1; second < logicalIdentities.Length; second++)
            {
                if (branches[first].StructuralBranchGroup == branches[second].StructuralBranchGroup
                    || !logicalIdentities[first].Overlaps(logicalIdentities[second]))
                {
                    continue;
                }

                alternatives.Add(first);
                alternatives.Add(second);
            }
        }

        return alternatives;
    }

    private static IReadOnlySet<string> FindRepeatedFamilyPaths(
        IReadOnlyList<SourceTreeNode> roots)
    {
        var repeatedPaths = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<SourceTreeNode>(roots.Reverse());
        while (pending.TryPop(out var node))
        {
            foreach (var siblingGroup in node.Children.GroupBy(
                         child => child.Element.ExpandedName,
                         StringComparer.Ordinal))
            {
                var siblings = siblingGroup.ToArray();
                if (siblings.Length > 1)
                {
                    repeatedPaths.Add(siblings[0].StructuralPath);
                }
            }

            foreach (var child in node.Children
                         .OrderByDescending(child => child.Element.SiblingPosition)
                         .ThenByDescending(child => child.Element.InstanceId))
            {
                pending.Push(child);
            }
        }

        return repeatedPaths;
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

    private static FlattenedHierarchyCell WithRepeatCoordinate(
        FlattenedHierarchyCell cell,
        int repeatOrdinal)
    {
        return new FlattenedHierarchyCell(
            new FlattenedColumnIdentity(
                cell.DetailedIdentity,
                cell.ColumnIdentity.RepeatCoordinates.Prepend(repeatOrdinal)),
            cell.Value,
            cell.SourceId,
            cell.Lineage);
    }

    private static RowFragment PrependRepeatCoordinate(
        RowFragment fragment,
        int repeatOrdinal)
    {
        return new RowFragment(fragment.Cells
            .Select(cell => WithRepeatCoordinate(cell, repeatOrdinal))
            .ToArray());
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
            .ThenBy(cell => cell.ColumnIdentity.RepeatCoordinates);
    }

    private sealed record RowFragment(IReadOnlyList<FlattenedHierarchyCell> Cells);

    private sealed record VariableBranch(
        IReadOnlyList<RowFragment> Rows,
        bool IsDirectRepeatedFamily,
        int StructuralBranchGroup)
    {
        public bool IsSingleFieldSequence => Rows.Count > 0
            && Rows
                .SelectMany(row => row.Cells)
                .Select(cell => cell.DetailedIdentity)
                .Distinct()
                .Count() == 1;
    }

    private sealed class ProjectionLimitTracker
    {
        public bool WasTruncated { get; private set; }

        public void MarkTruncated() => WasTruncated = true;
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

        public string StructuralPath { get; } = parent is null
            ? $"/{element.ExpandedName}"
            : $"{parent.StructuralPath}/{element.ExpandedName}";

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
