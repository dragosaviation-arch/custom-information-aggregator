using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Ipc;
using CIA.Core.Hierarchy;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Discovery;

public sealed class DiscoveryOutputPreviewService(
    StructuredInformationRepository repository,
    ISourceInterpreter sourceInterpreter,
    DatabaseSourceProjectionService sourceProjection,
    ILogger<DiscoveryOutputPreviewService> logger)
{
    public async Task<DiscoveryOutputPreviewHostResult> GetPreviewAsync(
        DiscoveryOutputPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var resolution = await repository.ResolveDiscoveryOccurrenceAsync(
                    request.Occurrence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolution is null)
            {
                return DiscoveryOutputPreviewHostResult.Reject(
                    "discovery-output-preview-out-of-date",
                    "The selected occurrence is no longer available from the current Discovery result.");
            }

            if (resolution.Source.SourceSetId != request.Projection.SourceSetId)
            {
                return DiscoveryOutputPreviewHostResult.Reject(
                    "discovery-output-preview-source-set-mismatch",
                    "The selected occurrence no longer belongs to the requested output configuration.");
            }

            var interpretation = await sourceInterpreter.InterpretAsync(
                    resolution.Source,
                    cancellationToken)
                .ConfigureAwait(false);
            if (interpretation.Status != SourceInterpretationStatus.Usable
                || interpretation.Source is null)
            {
                return DiscoveryOutputPreviewHostResult.Reject(
                    interpretation.Failure?.Code ?? "discovery-output-preview-source-failed",
                    "The selected source could not be projected for output preview.");
            }

            var matchingOccurrences = interpretation.Source.Values
                .Where(value => value.Lineage is not null)
                .Select(value => HierarchySourceOccurrence.FromInterpretedValue(
                    request.Projection.SourceSetId,
                    value))
                .Where(occurrence => occurrence.Identity == resolution.Identity)
                .OrderBy(occurrence => occurrence.Lineage.TraversalOrder)
                .ToArray();
            if (matchingOccurrences.Length != resolution.ExpectedSourceOccurrenceCount
                || resolution.LocalOrdinal < 1
                || resolution.LocalOrdinal > matchingOccurrences.Length)
            {
                return DiscoveryOutputPreviewHostResult.Reject(
                    "discovery-output-preview-source-changed",
                    "The selected source changed after Discovery. Run Discovery again before previewing output.");
            }

            var dataset = new DatabaseDatasetBuildSpecification(
                request.Projection.SourceSetId,
                request.Projection.SourceSetName,
                ordinal: 1,
                request.Projection.RepeatedDataLayout,
                [resolution.Source],
                request.Projection.RowFamilies,
                request.Projection.Fields);
            var projected = await sourceProjection.ProjectAsync(
                    dataset,
                    resolution.Source,
                    interpretation.Source,
                    request.MaximumRowsPerGroup,
                    cancellationToken)
                .ConfigureAwait(false);
            var groups = CreateGroups(dataset, projected);
            return DiscoveryOutputPreviewHostResult.Accept(new DiscoveryOutputPreview(
                request.Occurrence.DiscoveryOperationId,
                resolution.Identity,
                request.Occurrence.GlobalOrdinal,
                resolution.Source.SourceId,
                resolution.SourceName,
                request.Projection.RepeatedDataLayout,
                groups));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Discovery output preview failed for operation {DiscoveryOperationId}",
                request.Occurrence.DiscoveryOperationId);
            return DiscoveryOutputPreviewHostResult.Reject(
                "discovery-output-preview-failed",
                "The occurrence output preview could not be generated.");
        }
    }

    private static IReadOnlyList<DiscoveryOutputPreviewGroup> CreateGroups(
        DatabaseDatasetBuildSpecification dataset,
        DatabaseSourceProjection projection)
    {
        var truncatedFamilies = projection.TruncatedRowFamilies.ToHashSet();
        return projection.Rows
            .GroupBy(row => row.SemanticIdentity?.RowFamilyId
                ?? throw new InvalidOperationException(
                    "A family-aware output preview row requires semantic record identity."))
            .OrderBy(group => group.Key)
            .Take(DiscoveryOutputPreviewLimits.MaximumGroups)
            .Select(group => CreateGroup(
                dataset,
                group.Key,
                group.ToArray(),
                projection.IsTruncated && truncatedFamilies.Count == 0
                    || truncatedFamilies.Contains(group.Key)))
            .ToArray();
    }

    private static DiscoveryOutputPreviewGroup CreateGroup(
        DatabaseDatasetBuildSpecification dataset,
        RowFamilyId familyId,
        IReadOnlyList<DatabaseReviewRow> rows,
        bool isTruncated)
    {
        var allColumns = rows
            .SelectMany(row => row.Cells)
            .Select(cell => cell.ColumnIdentity)
            .Distinct()
            .OrderBy(identity => ResolveFieldOrdinal(dataset, identity))
            .ThenBy(identity => identity.RepeatCoordinates)
            .ToArray();
        var columns = allColumns
            .Take(DiscoveryOutputPreviewLimits.MaximumColumnsPerGroup)
            .Select((identity, index) => new DiscoveryOutputPreviewColumn(
                identity,
                ResolveHeader(dataset, identity),
                index + 1))
            .ToArray();
        var includedColumns = columns.Select(column => column.Identity).ToHashSet();
        var previewRows = rows.Select(row => new DiscoveryOutputPreviewRow(
            row.Ordinal,
            row.SemanticIdentity
                ?? throw new InvalidOperationException(
                    "A family-aware output preview row requires semantic record identity."),
            row.Cells
                .Where(cell => includedColumns.Contains(cell.ColumnIdentity))
                .Select(cell => new DiscoveryOutputPreviewCell(
                    cell.ColumnIdentity,
                    cell.Values
                        .Select(value => value.Value)
                        .Take(DiscoveryOutputPreviewLimits.MaximumValuesPerCell)
                        .ToArray(),
                    cell.HasConflict,
                    cell.Values.Count > DiscoveryOutputPreviewLimits.MaximumValuesPerCell))
                .ToArray()))
            .ToArray();
        return new DiscoveryOutputPreviewGroup(
            familyId,
            columns,
            previewRows,
            isTruncated
            || allColumns.Length > columns.Length
            || previewRows.SelectMany(row => row.Cells).Any(cell => cell.IsTruncated));
    }

    private static int ResolveFieldOrdinal(
        DatabaseDatasetBuildSpecification dataset,
        DatabaseColumnIdentity identity)
    {
        var index = dataset.Fields.ToList().FindIndex(field =>
            string.Equals(field.FieldKey, identity.FieldKey, StringComparison.Ordinal));
        return index < 0 ? int.MaxValue : index;
    }

    private static string ResolveHeader(
        DatabaseDatasetBuildSpecification dataset,
        DatabaseColumnIdentity identity)
    {
        var field = dataset.Fields.Single(mapping =>
            string.Equals(mapping.FieldKey, identity.FieldKey, StringComparison.Ordinal));
        return identity.RepeatCoordinates.Coordinates.Count == 0
            ? field.EffectiveName
            : $"{field.EffectiveName} {identity.RepeatCoordinates}";
    }
}

public sealed record DiscoveryOutputPreviewHostResult(
    bool Accepted,
    DiscoveryOutputPreview? Preview,
    IpcFailure? Failure)
{
    public static DiscoveryOutputPreviewHostResult Accept(DiscoveryOutputPreview preview) =>
        new(true, preview, null);

    public static DiscoveryOutputPreviewHostResult Reject(string code, string description) =>
        new(false, null, new IpcFailure(code, description));
}
