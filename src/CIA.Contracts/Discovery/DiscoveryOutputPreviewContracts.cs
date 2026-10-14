using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Discovery;

public static class DiscoveryOutputPreviewLimits
{
    public const int DefaultMaximumRowsPerGroup = 20;
    public const int MaximumRowsPerGroup = 50;
    public const int MaximumGroups = 128;
    public const int MaximumColumnsPerGroup = 128;
    public const int MaximumValuesPerCell = 16;
    public const int SourceFileColumnsPerPage = 10;
    public const int WideColumnWarningThreshold = 500;
}

public sealed record DiscoveryOutputProjectionSpecification
{
    [JsonConstructor]
    public DiscoveryOutputProjectionSpecification(
        SourceSetId sourceSetId,
        string sourceSetName,
        RepeatedDataLayout repeatedDataLayout,
        IReadOnlyList<RowFamilyDefinition> rowFamilies,
        IReadOnlyList<DatabaseFieldMapping> fields,
        DiscoveryOutputProjectionMode projectionMode =
            DiscoveryOutputProjectionMode.PreserveRelationships,
        IReadOnlyList<string>? sourceProjectionRepeatedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        ArgumentNullException.ThrowIfNull(rowFamilies);
        ArgumentNullException.ThrowIfNull(fields);
        if (!Enum.IsDefined(repeatedDataLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(repeatedDataLayout));
        }

        if (!Enum.IsDefined(projectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(projectionMode));
        }

        var families = rowFamilies.ToArray();
        var mappings = fields.ToArray();
        var familyIds = families.Select(family => family.Id).ToHashSet();
        if (families.Length == 0
            || families.Length > DiscoveryOutputPreviewLimits.MaximumGroups
            || familyIds.Count != families.Length
            || families.Any(family => family.Id.SourceSetId != sourceSetId)
            || mappings.Length == 0
            || mappings.Any(mapping => mapping.RowFamilyId.SourceSetId != sourceSetId
                || !familyIds.Contains(mapping.RowFamilyId)))
        {
            throw new ArgumentException(
                "An output preview requires bounded Row Family metadata and selected fields from one Source Set.",
                nameof(rowFamilies));
        }

        SourceSetId = sourceSetId;
        SourceSetName = sourceSetName.Trim();
        RepeatedDataLayout = repeatedDataLayout;
        RowFamilies = new ReadOnlyCollection<RowFamilyDefinition>(families);
        Fields = new ReadOnlyCollection<DatabaseFieldMapping>(mappings);
        ProjectionMode = projectionMode;
        SourceProjectionRepeatedPaths = new ReadOnlyCollection<string>(
            (sourceProjectionRepeatedPaths ?? []).ToArray());
    }

    public SourceSetId SourceSetId { get; }

    public string SourceSetName { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }

    public IReadOnlyList<RowFamilyDefinition> RowFamilies { get; }

    public IReadOnlyList<DatabaseFieldMapping> Fields { get; }

    public DiscoveryOutputProjectionMode ProjectionMode { get; }

    public IReadOnlyList<string> SourceProjectionRepeatedPaths { get; }
}

public sealed record DiscoveryOutputPreviewRequest
{
    [JsonConstructor]
    public DiscoveryOutputPreviewRequest(
        OutputSetId outputSetId,
        DiscoveryOccurrenceLookup occurrence,
        DiscoveryOutputProjectionSpecification projection,
        int maximumRowsPerGroup = DiscoveryOutputPreviewLimits.DefaultMaximumRowsPerGroup,
        int columnStartIndex = 0)
    {
        if (outputSetId.Value == Guid.Empty)
        {
            throw new ArgumentException("An output preview requires an Output Set identity.", nameof(outputSetId));
        }

        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(projection);
        if (occurrence.DetailedIdentities.Any(identity =>
                identity.SourceSetId != projection.SourceSetId))
        {
            throw new ArgumentException(
                "The preview occurrence and projection must belong to the same Source Set.",
                nameof(projection));
        }

        if (maximumRowsPerGroup is < 1 or > DiscoveryOutputPreviewLimits.MaximumRowsPerGroup)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRowsPerGroup));
        }

        if (columnStartIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columnStartIndex));
        }

        OutputSetId = outputSetId;
        Occurrence = occurrence;
        Projection = projection;
        MaximumRowsPerGroup = maximumRowsPerGroup;
        ColumnStartIndex = columnStartIndex;
    }

    public OutputSetId OutputSetId { get; }

    public DiscoveryOccurrenceLookup Occurrence { get; }

    public DiscoveryOutputProjectionSpecification Projection { get; }

    public int MaximumRowsPerGroup { get; }

    public int ColumnStartIndex { get; }
}

public sealed record DiscoveryOutputPreview(
    OutputSetId OutputSetId,
    OperationId DiscoveryOperationId,
    DiscoveryInformationIdentity OccurrenceIdentity,
    int OccurrenceOrdinal,
    SourceId SourceId,
    string SourceName,
    RepeatedDataLayout RepeatedDataLayout,
    IReadOnlyList<DiscoveryOutputPreviewGroup> Groups,
    DiscoveryOutputProjectionMode ProjectionMode =
        DiscoveryOutputProjectionMode.PreserveRelationships);

public sealed record DiscoveryOutputPreviewGroup(
    RowFamilyId RowFamilyId,
    IReadOnlyList<DiscoveryOutputPreviewColumn> Columns,
    IReadOnlyList<DiscoveryOutputPreviewRow> Rows,
    bool IsTruncated,
    int TotalColumnCount = 0,
    int ColumnStartIndex = 0);

public sealed record DiscoveryOutputPreviewColumn(
    DatabaseColumnIdentity Identity,
    string Header,
    int Ordinal);

public sealed record DiscoveryOutputPreviewRow(
    int Ordinal,
    DatabaseRecordIdentity RecordIdentity,
    IReadOnlyList<DiscoveryOutputPreviewCell> Cells);

public sealed record DiscoveryOutputPreviewCell(
    DatabaseColumnIdentity ColumnIdentity,
    IReadOnlyList<string> Values,
    bool HasConflict,
    bool IsTruncated);
