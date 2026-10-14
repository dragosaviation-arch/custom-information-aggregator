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
            flattened.IsTruncated,
            flattened.TruncatedRowFamilies);
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
    bool IsTruncated,
    IReadOnlyList<RowFamilyId> TruncatedRowFamilies);
