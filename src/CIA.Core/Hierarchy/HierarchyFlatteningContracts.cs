using System.Collections.ObjectModel;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.Core.Hierarchy;

public sealed class HierarchyFlatteningOptions
{
    public const int DefaultMaximumAllCombinationRows = 100_000;

    public HierarchyFlatteningOptions(
        int maximumAllCombinationRows = DefaultMaximumAllCombinationRows)
    {
        if (maximumAllCombinationRows < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAllCombinationRows),
                "The All Combinations row limit must be positive.");
        }

        MaximumAllCombinationRows = maximumAllCombinationRows;
    }

    public int MaximumAllCombinationRows { get; }
}

public sealed class HierarchyFlatteningRequest
{
    public HierarchyFlatteningRequest(
        SourceSetId sourceSetId,
        RepeatedDataLayout layout,
        IEnumerable<HierarchySourceInput> sources)
    {
        if (sourceSetId == default)
        {
            throw new ArgumentException(
                "Hierarchy flattening requires a Source Set ID.",
                nameof(sourceSetId));
        }

        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }

        ArgumentNullException.ThrowIfNull(sources);
        var sourceArray = sources.ToArray();
        if (sourceArray.Any(source => source is null))
        {
            throw new ArgumentException(
                "Hierarchy source inputs cannot contain null items.",
                nameof(sources));
        }

        if (sourceArray.Select(source => source.SourceId).Distinct().Count()
            != sourceArray.Length)
        {
            throw new ArgumentException(
                "A SourceId can appear only once in a hierarchy-flattening request.",
                nameof(sources));
        }

        if (sourceArray.Any(source => source.Occurrences.Any(
                occurrence => occurrence.Identity.SourceSetId != sourceSetId)))
        {
            throw new ArgumentException(
                "Every selected occurrence must belong to the requested Source Set.",
                nameof(sources));
        }

        SourceSetId = sourceSetId;
        Layout = layout;
        Sources = new ReadOnlyCollection<HierarchySourceInput>(sourceArray);
    }

    public SourceSetId SourceSetId { get; }

    public RepeatedDataLayout Layout { get; }

    public IReadOnlyList<HierarchySourceInput> Sources { get; }
}

public sealed class HierarchySourceInput
{
    public HierarchySourceInput(
        SourceId sourceId,
        IEnumerable<HierarchySourceOccurrence> occurrences)
    {
        if (sourceId == default)
        {
            throw new ArgumentException(
                "Hierarchy source input requires a SourceId.",
                nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(occurrences);
        var occurrenceArray = occurrences.ToArray();
        if (occurrenceArray.Any(occurrence => occurrence is null))
        {
            throw new ArgumentException(
                "Hierarchy source occurrences cannot contain null items.",
                nameof(occurrences));
        }

        if (occurrenceArray.Any(occurrence =>
                occurrence.SourceId != sourceId
                || occurrence.Lineage.OriginatingSourceId != sourceId))
        {
            throw new ArgumentException(
                "Every hierarchy occurrence must retain the containing SourceId.",
                nameof(occurrences));
        }

        SourceId = sourceId;
        Occurrences = new ReadOnlyCollection<HierarchySourceOccurrence>(occurrenceArray);
    }

    public SourceId SourceId { get; }

    public IReadOnlyList<HierarchySourceOccurrence> Occurrences { get; }

    public static HierarchySourceInput FromInterpretedSource(
        SourceSetId sourceSetId,
        InterpretedSourceDocument source,
        IEnumerable<DiscoveryInformationIdentity> selectedDetailedIdentities)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedDetailedIdentities);

        var selected = selectedDetailedIdentities.ToHashSet();
        if (selected.Any(identity => identity.SourceSetId != sourceSetId))
        {
            throw new ArgumentException(
                "Selected identities must belong to the supplied Source Set.",
                nameof(selectedDetailedIdentities));
        }

        var occurrences = source.Values
            .Where(value => value.Lineage is not null)
            .Select(value => HierarchySourceOccurrence.FromInterpretedValue(sourceSetId, value))
            .Where(occurrence => selected.Contains(occurrence.Identity))
            .ToArray();

        return new HierarchySourceInput(source.OriginatingSourceId, occurrences);
    }
}

public sealed class HierarchySourceOccurrence
{
    public HierarchySourceOccurrence(
        DiscoveryInformationIdentity identity,
        SourceId sourceId,
        string value,
        SourceValueLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (sourceId == default)
        {
            throw new ArgumentException(
                "A hierarchy occurrence requires a SourceId.",
                nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.OriginatingSourceId != sourceId)
        {
            throw new ArgumentException(
                "The occurrence SourceId must match its retained lineage.",
                nameof(lineage));
        }

        Identity = identity;
        SourceId = sourceId;
        Value = value;
        Lineage = lineage;
    }

    public DiscoveryInformationIdentity Identity { get; }

    public SourceId SourceId { get; }

    public string Value { get; }

    public SourceValueLineage Lineage { get; }

    public static HierarchySourceOccurrence FromInterpretedValue(
        SourceSetId sourceSetId,
        InterpretedSourceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Lineage is null)
        {
            throw new ArgumentException(
                "Hierarchy flattening requires retained source lineage.",
                nameof(value));
        }

        var identity = new DiscoveryInformationIdentity(
            sourceSetId,
            value.Lineage.StructuralPath,
            value.InformationType,
            value.CandidateKind,
            value.StructuralIdentity);

        return new HierarchySourceOccurrence(
            identity,
            value.Lineage.OriginatingSourceId,
            value.Content,
            value.Lineage);
    }
}

public sealed record FlattenedColumnIdentity
{
    public FlattenedColumnIdentity(
        DiscoveryInformationIdentity detailedIdentity,
        int? repeatOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(detailedIdentity);
        if (repeatOrdinal is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatOrdinal),
                "A numbered-column repeat ordinal must be positive.");
        }

        DetailedIdentity = detailedIdentity;
        RepeatOrdinal = repeatOrdinal;
    }

    public DiscoveryInformationIdentity DetailedIdentity { get; }

    public int? RepeatOrdinal { get; }
}

public sealed class FlattenedHierarchyCell
{
    public FlattenedHierarchyCell(
        FlattenedColumnIdentity columnIdentity,
        string value,
        SourceId sourceId,
        SourceValueLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(columnIdentity);
        ArgumentNullException.ThrowIfNull(value);
        if (sourceId == default)
        {
            throw new ArgumentException("A flattened cell requires a SourceId.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.OriginatingSourceId != sourceId)
        {
            throw new ArgumentException(
                "A flattened cell must retain matching source lineage.",
                nameof(lineage));
        }

        ColumnIdentity = columnIdentity;
        Value = value;
        SourceId = sourceId;
        Lineage = lineage;
    }

    public FlattenedColumnIdentity ColumnIdentity { get; }

    public string Value { get; }

    public SourceId SourceId { get; }

    public SourceValueLineage Lineage { get; }

    public DiscoveryInformationIdentity DetailedIdentity => ColumnIdentity.DetailedIdentity;

    public SourceValueCandidateKind CandidateKind => DetailedIdentity.CandidateKind;

    public string StructuralPath => Lineage.StructuralPath;

    public string StructuralIdentity => DetailedIdentity.StructuralIdentity;
}

public sealed class FlattenedHierarchyRow
{
    public FlattenedHierarchyRow(
        int ordinal,
        SourceId sourceId,
        IEnumerable<FlattenedHierarchyCell> cells)
    {
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                "A flattened row ordinal must be positive.");
        }

        if (sourceId == default)
        {
            throw new ArgumentException("A flattened row requires a SourceId.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(cells);
        var cellArray = cells.ToArray();
        if (cellArray.Any(cell => cell is null || cell.SourceId != sourceId))
        {
            throw new ArgumentException(
                "Every flattened row cell must belong to the row SourceId.",
                nameof(cells));
        }

        Ordinal = ordinal;
        SourceId = sourceId;
        Cells = new ReadOnlyCollection<FlattenedHierarchyCell>(cellArray);
    }

    public int Ordinal { get; }

    public SourceId SourceId { get; }

    public IReadOnlyList<FlattenedHierarchyCell> Cells { get; }
}

public sealed class HierarchyFlatteningResult
{
    public HierarchyFlatteningResult(
        SourceSetId sourceSetId,
        RepeatedDataLayout layout,
        IEnumerable<FlattenedHierarchyRow> rows)
    {
        if (sourceSetId == default)
        {
            throw new ArgumentException(
                "A hierarchy-flattening result requires a Source Set ID.",
                nameof(sourceSetId));
        }

        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }

        ArgumentNullException.ThrowIfNull(rows);
        var rowArray = rows.ToArray();
        if (rowArray.Any(row => row is null))
        {
            throw new ArgumentException(
                "Flattened rows cannot contain null items.",
                nameof(rows));
        }

        SourceSetId = sourceSetId;
        Layout = layout;
        Rows = new ReadOnlyCollection<FlattenedHierarchyRow>(rowArray);
    }

    public SourceSetId SourceSetId { get; }

    public RepeatedDataLayout Layout { get; }

    public IReadOnlyList<FlattenedHierarchyRow> Rows { get; }
}

public sealed class AllCombinationsLimitExceededException : InvalidOperationException
{
    public AllCombinationsLimitExceededException(long? projectedRows, int maximumRows)
        : base(projectedRows is null
            ? $"All Combinations exceeds the configured limit of {maximumRows} rows."
            : $"All Combinations projects {projectedRows} rows, exceeding the configured limit of {maximumRows} rows.")
    {
        ProjectedRows = projectedRows;
        MaximumRows = maximumRows;
    }

    public long? ProjectedRows { get; }

    public int MaximumRows { get; }
}
