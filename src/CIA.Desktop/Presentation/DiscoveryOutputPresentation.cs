using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CIA.Desktop.Presentation;

public sealed record DiscoverySelectionImpactPresentation(
    int SelectedLogicalFieldCount,
    int SelectedDetailedContextCount,
    IReadOnlyList<DiscoverySelectionImpactGroupPresentation> Groups)
{
    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} selected fields · {1:N0} structural contexts · {2:N0} output tables",
        SelectedLogicalFieldCount,
        SelectedDetailedContextCount,
        Groups.Count);

    public static DiscoverySelectionImpactPresentation Empty { get; } =
        new(0, 0, []);
}

public sealed record DiscoverySelectionImpactGroupPresentation(
    RowFamilyId RowFamilyId,
    string DisplayName,
    string StructuralContext,
    string RelationshipText,
    IReadOnlyList<string> ProjectedColumns)
{
    public string ColumnsText => ProjectedColumns.Count == 0
        ? "No selected columns"
        : string.Join(", ", ProjectedColumns);
}

public sealed class DiscoveryOutputPreviewGroupPresentation
{
    public DiscoveryOutputPreviewGroupPresentation(
        DiscoverySelectionImpactGroupPresentation impact,
        DiscoveryOutputPreviewGroup preview)
    {
        Impact = impact;
        Columns = preview.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => new DiscoveryOutputPreviewColumnPresentation(
                column.Identity,
                column.Header))
            .ToArray();
        Rows = preview.Rows.Select(row => new DiscoveryOutputPreviewRowPresentation(
            row.Ordinal,
            Columns.Select(column =>
            {
                var cell = row.Cells.FirstOrDefault(candidate =>
                    candidate.ColumnIdentity == column.Identity);
                return cell is null
                    ? new DiscoveryOutputPreviewCellPresentation(string.Empty, false, false)
                    : new DiscoveryOutputPreviewCellPresentation(
                        string.Join(" | ", cell.Values),
                        cell.HasConflict,
                        cell.IsTruncated);
            }).ToArray())).ToArray();
        IsTruncated = preview.IsTruncated;
    }

    public DiscoverySelectionImpactGroupPresentation Impact { get; }

    public RowFamilyId RowFamilyId => Impact.RowFamilyId;

    public string DisplayName => Impact.DisplayName;

    public string StructuralContext => Impact.StructuralContext;

    public IReadOnlyList<DiscoveryOutputPreviewColumnPresentation> Columns { get; }

    public IReadOnlyList<DiscoveryOutputPreviewRowPresentation> Rows { get; }

    public bool IsTruncated { get; }

    public string RowSummary => IsTruncated
        ? $"Preview is limited to the first {Rows.Count:N0} rows."
        : $"Showing {Rows.Count:N0} preview rows.";
}

public sealed record DiscoveryOutputPreviewColumnPresentation(
    DatabaseColumnIdentity Identity,
    string Header);

public sealed record DiscoveryOutputPreviewRowPresentation(
    int Ordinal,
    IReadOnlyList<DiscoveryOutputPreviewCellPresentation> Cells);

public sealed record DiscoveryOutputPreviewCellPresentation(
    string Value,
    bool HasConflict,
    bool IsTruncated);

public sealed class DiscoveryOutputSetItemViewModel : ObservableObject
{
    private readonly Func<OutputSetId, bool, bool> _setEnabled;
    private readonly Func<OutputSetId, string, bool> _rename;
    private bool _isEnabled;
    private string _name;

    public DiscoveryOutputSetItemViewModel(
        DiscoveryOutputSetDefinition definition,
        string parentSourceSetName,
        DiscoverySelectionImpactGroupPresentation impact,
        Func<OutputSetId, bool, bool> setEnabled,
        Func<OutputSetId, string, bool> rename)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSourceSetName);
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(setEnabled);
        ArgumentNullException.ThrowIfNull(rename);
        OutputSetId = definition.OutputSetId;
        ParentSourceSetId = definition.ParentSourceSetId;
        ModelKey = definition.ModelKey;
        SetNumber = definition.SetNumber;
        ModelOrder = definition.ModelOrder;
        RowFamilyIds = definition.RowFamilyIds;
        ParentSourceSetName = parentSourceSetName;
        ModelName = impact.DisplayName;
        RelationshipText = impact.RelationshipText;
        StructuralContext = impact.StructuralContext;
        ColumnsText = impact.ColumnsText;
        _name = definition.Name;
        _isEnabled = definition.IsEnabled;
        _setEnabled = setEnabled;
        _rename = rename;
    }

    public OutputSetId OutputSetId { get; }

    public SourceSetId ParentSourceSetId { get; }

    public DiscoveryOutputModelKey ModelKey { get; }

    public int SetNumber { get; }

    public string SetNumberText => $"Set {SetNumber}";

    public int ModelOrder { get; }

    public IReadOnlyList<RowFamilyId> RowFamilyIds { get; }

    public string ParentSourceSetName { get; }

    public string ModelName { get; }

    public string RelationshipText { get; }

    public string StructuralContext { get; }

    public string ColumnsText { get; }

    public string DisplayLabel => $"Set {SetNumber}: {Name}";

    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal)
                || !_rename(OutputSetId, value))
            {
                OnPropertyChanged();
                return;
            }

            _name = value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value || !_setEnabled(OutputSetId, value))
            {
                OnPropertyChanged();
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
        }
    }
}

public static class DiscoverySelectionImpactBuilder
{
    public static DiscoverySelectionImpactPresentation Create(DatabaseBuildSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var detailed = specification.Datasets
            .SelectMany(dataset => dataset.Fields)
            .SelectMany(field => field.DetailedIdentities)
            .Distinct()
            .ToArray();
        var logicalCount = detailed
            .Select(DatabaseLogicalFieldIdentity.Create)
            .Distinct()
            .Count();
        var groups = specification.Datasets
            .SelectMany(dataset => dataset.RowFamilies.Select(family => CreateGroup(dataset, family)))
            .ToArray();
        return new DiscoverySelectionImpactPresentation(
            logicalCount,
            detailed.Length,
            groups);
    }

    private static DiscoverySelectionImpactGroupPresentation CreateGroup(
        DatabaseDatasetBuildSpecification dataset,
        RowFamilyDefinition family)
    {
        var columns = dataset.Fields
            .Where(field => field.RowFamilyId == family.Id)
            .Select(field => field.EffectiveName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var relationship = family.Id.Kind == RowFamilyKind.Root
            ? "Root output table"
            : family.ParentRowFamilyId?.Kind == RowFamilyKind.Repeated
                ? "Nested repeated output table"
                : "Repeated output table";
        var displayName = family.Id.Kind == RowFamilyKind.Root
            ? $"{dataset.DisplayName} — Root"
            : $"{dataset.DisplayName} — {GetFinalPathSegment(family.Id.AnchorPath)}";
        return new DiscoverySelectionImpactGroupPresentation(
            family.Id,
            displayName,
            family.Id.AnchorPath,
            relationship,
            columns);
    }

    private static string GetFinalPathSegment(string path)
    {
        var segment = path[(path.LastIndexOf('/') + 1)..];
        var namespaceEnd = segment.LastIndexOf('}');
        return namespaceEnd >= 0 && namespaceEnd < segment.Length - 1
            ? segment[(namespaceEnd + 1)..]
            : segment;
    }
}
