using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Hierarchy;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.Database;

/// <summary>
/// The single source projection boundary shared by authoritative Database generation and
/// bounded, ephemeral Discovery output preview.
/// </summary>
public sealed class DatabaseSourceProjectionService(IHierarchyFlatteningEngine hierarchyFlattening)
{
    public async ValueTask<DatabaseSourceProjection> ProjectAsync(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source,
        InterpretedSourceDocument interpretedSource,
        int? maximumRowsPerFamily = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(interpretedSource);
        if (source.SourceSetId != dataset.SourceSetId
            || interpretedSource.OriginatingSourceId != source.SourceId)
        {
            throw new ArgumentException(
                "A projected source must belong to the requested Database dataset.",
                nameof(source));
        }

        if (dataset.ProjectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile)
        {
            return ProjectSourceFile(dataset, source, interpretedSource);
        }

        var selectedIdentities = dataset.Fields
            .SelectMany(field => field.DetailedIdentities)
            .ToArray();
        var input = HierarchySourceInput.FromInterpretedSource(
            dataset.SourceSetId,
            interpretedSource,
            selectedIdentities);
        var isLegacyCompatibility = dataset.RowFamilies.All(family =>
            string.Equals(family.Id.AnchorPath, "/legacy", StringComparison.Ordinal));
        var familyAssignments = dataset.Fields
            .SelectMany(field => field.DetailedIdentities.Select(identity =>
                new KeyValuePair<DiscoveryInformationIdentity, RowFamilyId>(
                    identity,
                    field.RowFamilyId)))
            .ToDictionary(item => item.Key, item => item.Value);
        var flattened = await hierarchyFlattening.FlattenAsync(
            isLegacyCompatibility
                ? new HierarchyFlatteningRequest(
                    dataset.SourceSetId,
                    dataset.RepeatedDataLayout,
                    [input],
                    [],
                    new Dictionary<DiscoveryInformationIdentity, RowFamilyId>(),
                    maximumRowsPerFamily)
                : new HierarchyFlatteningRequest(
                    dataset.SourceSetId,
                    dataset.RepeatedDataLayout,
                    [input],
                    dataset.RowFamilies,
                    familyAssignments,
                    maximumRowsPerFamily),
            cancellationToken).ConfigureAwait(false);
        if (flattened.Rows.Any(row => row.SourceId != source.SourceId))
        {
            throw new InvalidOperationException(
                "Hierarchy flattening returned rows outside the source boundary.");
        }

        var metadata = CreateSourceMetadata(dataset, source);
        var normalizedRows = isLegacyCompatibility
            ? flattened.Rows.Select(row => new FlattenedHierarchyRow(
                row.Ordinal,
                row.SourceId,
                row.Cells,
                dataset.RowFamilies.Single().Id,
                row.Cells.Min(cell => cell.Lineage.ElementPath[0].InstanceId),
                row.Ordinal))
            : flattened.Rows;
        var rows = normalizedRows.Select(row => CreateDatabaseRow(
            dataset,
            source,
            metadata,
            row)).ToArray();
        return new DatabaseSourceProjection(
            rows,
            rows.SelectMany(row => row.Cells)
                .Select(cell => cell.ColumnIdentity)
                .Distinct()
                .ToArray(),
            flattened.IsTruncated,
            flattened.TruncatedRowFamilies);
    }

    private static DatabaseSourceProjection ProjectSourceFile(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source,
        InterpretedSourceDocument interpretedSource)
    {
        var selected = dataset.Fields
            .SelectMany(field => field.DetailedIdentities)
            .ToHashSet();
        var repeatedPaths = dataset.SourceProjectionRepeatedPaths.ToHashSet(StringComparer.Ordinal);
        var siblingOrdinals = CreateSiblingOrdinals(interpretedSource);
        var cells = interpretedSource.Values
            .Where(value => value.Lineage is not null)
            .Select(value => HierarchySourceOccurrence.FromInterpretedValue(dataset.SourceSetId, value))
            .Where(occurrence => selected.Contains(occurrence.Identity))
            .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
            .Select(occurrence => new FlattenedHierarchyCell(
                new FlattenedColumnIdentity(
                    occurrence.Identity,
                    CreateSourceFileCoordinates(
                        occurrence.Lineage,
                        repeatedPaths,
                        siblingOrdinals)),
                occurrence.Value,
                occurrence.SourceId,
                occurrence.Lineage))
            .ToArray();
        var projectionFamily = dataset.RowFamilies.Single().Id;
        var anchorInstanceId = interpretedSource.Values
            .Select(value => value.Lineage?.ElementPath.FirstOrDefault()?.InstanceId)
            .FirstOrDefault(id => id is not null) ?? 1;
        var row = new FlattenedHierarchyRow(
            1,
            source.SourceId,
            cells,
            projectionFamily,
            anchorInstanceId,
            1);
        var projectedRow = CreateDatabaseRow(
            dataset,
            source,
            CreateSourceMetadata(dataset, source),
            row);
        var columns = CreateSourceFileColumns(
            dataset,
            interpretedSource,
            repeatedPaths,
            siblingOrdinals);
        return new DatabaseSourceProjection(
            [projectedRow],
            columns,
            IsTruncated: false,
            TruncatedRowFamilies: []);
    }

    private static IReadOnlyList<DatabaseColumnIdentity> CreateSourceFileColumns(
        DatabaseDatasetBuildSpecification dataset,
        InterpretedSourceDocument interpretedSource,
        IReadOnlySet<string> repeatedPaths,
        IReadOnlyDictionary<(long ParentInstanceId, string ExpandedName, long InstanceId), int>
            siblingOrdinals)
    {
        var projectionFamily = dataset.RowFamilies.Single().Id;
        var columns = new List<(DatabaseColumnIdentity Identity, int FieldOrdinal)>();
        for (var fieldOrdinal = 0; fieldOrdinal < dataset.Fields.Count; fieldOrdinal++)
        {
            var field = dataset.Fields[fieldOrdinal];
            var familyRepeatedPaths = repeatedPaths
                .Where(path => string.Equals(
                        path,
                        field.OriginatingRowFamilyId.AnchorPath,
                        StringComparison.Ordinal)
                    || field.OriginatingRowFamilyId.AnchorPath.StartsWith(
                        path + "/",
                        StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            var coordinates = field.OriginatingRowFamilyId.Kind == RowFamilyKind.Root
                ? [DatabaseRepeatCoordinatePath.Empty]
                : interpretedSource.Values
                    .Where(value => value.Lineage is not null
                        && IsWithinFamily(value.Lineage, field.OriginatingRowFamilyId.AnchorPath))
                    .Select(value => new DatabaseRepeatCoordinatePath(
                        CreateSourceFileCoordinates(
                            value.Lineage!,
                            familyRepeatedPaths,
                            siblingOrdinals).Coordinates))
                    .DistinctBy(coordinate => string.Join(",", coordinate.Coordinates))
                    .ToArray();
            columns.AddRange(coordinates.Select(coordinate => (
                new DatabaseColumnIdentity(projectionFamily, field.FieldKey, coordinate),
                fieldOrdinal)));
        }

        return columns
            .DistinctBy(item => (
                item.Identity.FieldKey,
                Coordinates: string.Join(",", item.Identity.RepeatCoordinates.Coordinates)))
            .OrderBy(item => item.Identity.RepeatCoordinates)
            .ThenBy(item => item.FieldOrdinal)
            .Select(item => item.Identity)
            .ToArray();
    }

    private static bool IsWithinFamily(SourceValueLineage lineage, string anchorPath)
    {
        var path = string.Empty;
        foreach (var element in lineage.ElementPath)
        {
            path += "/" + element.ExpandedName;
            if (string.Equals(path, anchorPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<(long ParentInstanceId, string ExpandedName, long InstanceId), int>
        CreateSiblingOrdinals(InterpretedSourceDocument interpretedSource)
    {
        var instances = interpretedSource.Values
            .Where(value => value.Lineage is not null)
            .SelectMany(value => value.Lineage!.ElementPath.Select((element, index) => new
            {
                ParentInstanceId = index == 0
                    ? 0
                    : value.Lineage.ElementPath[index - 1].InstanceId,
                element.ExpandedName,
                element.InstanceId,
                element.SiblingPosition
            }))
            .DistinctBy(item => item.InstanceId)
            .GroupBy(item => (item.ParentInstanceId, item.ExpandedName));
        var result = new Dictionary<(long, string, long), int>();
        foreach (var group in instances)
        {
            var ordered = group
                .OrderBy(item => item.SiblingPosition)
                .ThenBy(item => item.InstanceId)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                result[(group.Key.ParentInstanceId, group.Key.ExpandedName, ordered[index].InstanceId)] =
                    index + 1;
            }
        }

        return result;
    }

    private static RepeatCoordinatePath CreateSourceFileCoordinates(
        SourceValueLineage lineage,
        IReadOnlySet<string> repeatedPaths,
        IReadOnlyDictionary<(long ParentInstanceId, string ExpandedName, long InstanceId), int>
            siblingOrdinals)
    {
        var path = string.Empty;
        var coordinates = new List<int>();
        for (var index = 0; index < lineage.ElementPath.Count; index++)
        {
            var element = lineage.ElementPath[index];
            path += "/" + element.ExpandedName;
            if (!repeatedPaths.Contains(path))
            {
                continue;
            }

            var parentInstanceId = index == 0 ? 0 : lineage.ElementPath[index - 1].InstanceId;
            coordinates.Add(siblingOrdinals.GetValueOrDefault(
                (parentInstanceId, element.ExpandedName, element.InstanceId),
                1));
        }

        return new RepeatCoordinatePath(coordinates);
    }

    private static DatabaseReviewRow CreateDatabaseRow(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source,
        DatabaseSourceMetadata metadata,
        FlattenedHierarchyRow row)
    {
        var mapped = row.Cells.Select(cell => new
        {
            Cell = cell,
            Mapping = DatabaseTagMapper.FindMapping(dataset.Fields, cell.DetailedIdentity),
            Coordinates = new DatabaseRepeatCoordinatePath(
                cell.ColumnIdentity.RepeatCoordinates.Coordinates)
        })
            .GroupBy(item => new DatabaseColumnIdentity(
                row.RowFamilyId
                    ?? throw new InvalidOperationException(
                        "Family-aware Database generation requires a Row Family on every row."),
                item.Mapping.FieldKey,
                item.Coordinates));
        var cells = mapped.Select(group =>
        {
            var values = group.Select(item => new DatabaseReviewValue(
                item.Cell.Value,
                item.Cell.DetailedIdentity,
                item.Cell.SourceId,
                new DatabaseLineageEvidence(
                    item.Cell.Lineage.StructuralPath,
                    item.Cell.Lineage.NodeInstanceId,
                    item.Cell.Lineage.ParentInstanceId,
                    item.Cell.Lineage.AncestorInstanceIds,
                    item.Cell.Lineage.TraversalOrder,
                    item.Cell.Lineage.ElementPath.Select(element => new DatabaseSourceElementEvidence(
                        element.LocalName,
                        element.NamespaceUri,
                        element.QualifiedName,
                        element.InstanceId,
                        element.SiblingPosition)).ToArray()),
                group.Key.RepeatCoordinates)).ToArray();
            var conflict = values.Select(value => value.Value)
                .Distinct(StringComparer.Ordinal).Skip(1).Any();
            return new DatabaseReviewCell(group.Key, conflict, values);
        }).ToArray();
        var recordIdentity = new DatabaseRecordIdentity(
            row.RowFamilyId!.Value,
            source.SourceId,
            row.AnchorInstanceId!.Value,
            row.ProjectionOrdinal!.Value);
        return new DatabaseReviewRow(
            row.Ordinal,
            true,
            recordIdentity,
            metadata,
            cells);
    }

    private static DatabaseSourceMetadata CreateSourceMetadata(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source)
    {
        var fullPath = Path.GetFullPath(source.Path);
        DateTimeOffset? modifiedUtc = null;
        try
        {
            modifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            modifiedUtc = null;
        }

        return new DatabaseSourceMetadata(
            dataset.SourceSetId,
            dataset.DisplayName,
            source.SourceId,
            Path.GetFileName(fullPath),
            fullPath,
            source.Kind,
            source.ArchiveProvenance?.OriginalArchivePath,
            source.ArchiveProvenance?.ArchiveMemberPath,
            modifiedUtc);
    }
}

public sealed record DatabaseSourceProjection(
    IReadOnlyList<DatabaseReviewRow> Rows,
    IReadOnlyList<DatabaseColumnIdentity> Columns,
    bool IsTruncated,
    IReadOnlyList<RowFamilyId> TruncatedRowFamilies);
