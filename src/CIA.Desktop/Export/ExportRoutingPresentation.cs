using System.Collections.ObjectModel;
using System.ComponentModel;
using CIA.Contracts.Database;
using CIA.Contracts.Export;
using CIA.Contracts.Sources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CIA.Desktop.Export;

public sealed class ExportRoutingConfigurationPresentation
{
    private readonly ObservableCollection<ExportWorkbookPresentation> _workbooks = [];
    private readonly ObservableCollection<ExportSourceSetPresentation> _sourceSets = [];

    public ExportRoutingConfigurationPresentation()
    {
        Workbooks = new ReadOnlyObservableCollection<ExportWorkbookPresentation>(_workbooks);
        SourceSets = new ReadOnlyObservableCollection<ExportSourceSetPresentation>(_sourceSets);
    }

    public event EventHandler? Changed;

    public ReadOnlyObservableCollection<ExportWorkbookPresentation> Workbooks { get; }

    public ReadOnlyObservableCollection<ExportSourceSetPresentation> SourceSets { get; }

    public void Synchronize(DatabaseGenerationSummary generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!generation.IsHierarchyAware)
        {
            return;
        }

        var defaultWorkbook = _workbooks.OrderBy(workbook => workbook.Order).FirstOrDefault()
            ?? AddWorkbook(CreateUniqueWorkbookFileName());
        var currentSourceSetIds = generation.Datasets
            .Select(dataset => dataset.SourceSetId)
            .ToHashSet();
        foreach (var removed in _sourceSets
                     .Where(set => !currentSourceSetIds.Contains(set.SourceSetId))
                     .ToArray())
        {
            removed.Changed -= OnSourceSetChanged;
            _sourceSets.Remove(removed);
            CompactWorksheetOrder(removed.WorkbookDefinitionId);
        }
        foreach (var dataset in generation.Datasets.OrderBy(dataset => dataset.Ordinal))
        {
            var set = _sourceSets.SingleOrDefault(candidate =>
                candidate.SourceSetId == dataset.SourceSetId);
            if (set is null)
            {
                set = new ExportSourceSetPresentation(
                    dataset,
                    defaultWorkbook.WorkbookDefinitionId,
                    WorksheetDefinitionId.CreateNew(),
                    CreateUniqueWorksheetName(defaultWorkbook.WorkbookDefinitionId, dataset.DisplayName),
                    NextWorksheetOrder(defaultWorkbook.WorkbookDefinitionId));
                Subscribe(set);
                _sourceSets.Add(set);
            }
            else
            {
                set.UpdateDataset(dataset);
            }
        }

        OnChanged();
    }

    public ExportWorkbookPresentation CreateWorkbookFor(SourceSetId sourceSetId)
    {
        var workbook = AddWorkbook(CreateUniqueWorkbookFileName());
        AssignWorkbook(sourceSetId, workbook.WorkbookDefinitionId);
        return workbook;
    }

    public ExportWorkbookPresentation CreateWorkbook(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var workbook = AddWorkbook(fileName);
        OnChanged();
        return workbook;
    }

    public void AssignWorkbook(
        SourceSetId sourceSetId,
        WorkbookDefinitionId workbookDefinitionId)
    {
        var set = GetSet(sourceSetId);
        if (!_workbooks.Any(workbook => workbook.WorkbookDefinitionId == workbookDefinitionId)
            || set.WorkbookDefinitionId == workbookDefinitionId)
        {
            return;
        }

        var previousWorkbookId = set.WorkbookDefinitionId;
        set.AssignWorkbook(workbookDefinitionId, NextWorksheetOrder(workbookDefinitionId));
        CompactWorksheetOrder(previousWorkbookId);
        OnChanged();
    }

    public bool TryDeleteWorkbook(WorkbookDefinitionId workbookDefinitionId)
    {
        var workbook = _workbooks.SingleOrDefault(candidate =>
            candidate.WorkbookDefinitionId == workbookDefinitionId);
        if (workbook is null
            || _sourceSets.Any(set => set.WorkbookDefinitionId == workbookDefinitionId))
        {
            return false;
        }

        workbook.PropertyChanged -= OnChildPropertyChanged;
        _workbooks.Remove(workbook);
        ReorderWorkbooks();
        OnChanged();
        return true;
    }

    public bool MoveWorksheet(SourceSetId sourceSetId, int offset)
    {
        var set = GetSet(sourceSetId);
        var ordered = _sourceSets
            .Where(candidate => candidate.WorkbookDefinitionId == set.WorkbookDefinitionId)
            .OrderBy(candidate => candidate.WorksheetOrder)
            .ToList();
        var currentIndex = ordered.IndexOf(set);
        var nextIndex = currentIndex + offset;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= ordered.Count)
        {
            return false;
        }

        var other = ordered[nextIndex];
        var currentOrder = set.WorksheetOrder;
        set.SetWorksheetOrder(other.WorksheetOrder);
        other.SetWorksheetOrder(currentOrder);
        OnChanged();
        return true;
    }

    public ExportSourceSetPresentation GetSet(SourceSetId sourceSetId) =>
        _sourceSets.Single(set => set.SourceSetId == sourceSetId);

    public ExportConfigurationSnapshot Capture() => new(
        _workbooks.OrderBy(workbook => workbook.Order)
            .Select(workbook => new WorkbookDefinition(
                workbook.WorkbookDefinitionId,
                workbook.FileName,
                workbook.Order,
                _sourceSets
                    .Where(set => set.WorkbookDefinitionId == workbook.WorkbookDefinitionId)
                    .OrderBy(set => set.WorksheetOrder)
                    .Select(set => new WorksheetDefinition(
                        set.WorksheetDefinitionId,
                        workbook.WorkbookDefinitionId,
                        set.SourceSetId,
                        set.WorksheetName,
                        set.WorksheetOrder))
                    .ToArray()))
            .ToArray(),
        _sourceSets.OrderBy(set => set.DatasetOrder)
            .Select(set => set.Capture())
            .ToArray());

    private ExportWorkbookPresentation AddWorkbook(string fileName)
    {
        var workbook = new ExportWorkbookPresentation(
            WorkbookDefinitionId.CreateNew(),
            fileName,
            _workbooks.Count + 1);
        workbook.PropertyChanged += OnChildPropertyChanged;
        _workbooks.Add(workbook);
        return workbook;
    }

    private void Subscribe(ExportSourceSetPresentation set)
    {
        set.Changed += OnSourceSetChanged;
    }

    private void OnSourceSetChanged(object? sender, EventArgs e) => OnChanged();

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnChanged();

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private int NextWorksheetOrder(WorkbookDefinitionId workbookDefinitionId) =>
        _sourceSets.Where(set => set.WorkbookDefinitionId == workbookDefinitionId)
            .Select(set => set.WorksheetOrder)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private void CompactWorksheetOrder(WorkbookDefinitionId workbookDefinitionId)
    {
        var order = 1;
        foreach (var set in _sourceSets
                     .Where(candidate => candidate.WorkbookDefinitionId == workbookDefinitionId)
                     .OrderBy(candidate => candidate.WorksheetOrder))
        {
            set.SetWorksheetOrder(order++);
        }
    }

    private void ReorderWorkbooks()
    {
        var order = 1;
        foreach (var workbook in _workbooks.OrderBy(workbook => workbook.Order))
        {
            workbook.SetOrder(order++);
        }
    }

    private string CreateUniqueWorkbookFileName()
    {
        const string stem = "CIA Export";
        var suffix = 1;
        while (true)
        {
            var candidate = suffix == 1 ? $"{stem}.xlsx" : $"{stem} {suffix}.xlsx";
            if (_workbooks.All(workbook => !string.Equals(
                    workbook.FileName,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private string CreateUniqueWorksheetName(
        WorkbookDefinitionId workbookDefinitionId,
        string preferredName)
    {
        var baseName = SanitizeWorksheetName(preferredName);
        var suffix = 1;
        while (true)
        {
            var suffixText = suffix == 1 ? string.Empty : $" ({suffix})";
            var candidate = baseName[..Math.Min(baseName.Length, 31 - suffixText.Length)]
                + suffixText;
            if (_sourceSets.Where(set => set.WorkbookDefinitionId == workbookDefinitionId)
                .All(set => !string.Equals(
                    set.WorksheetName,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string SanitizeWorksheetName(string preferredName)
    {
        var sanitized = new string((preferredName ?? string.Empty)
            .Select(character => character is ':' or '\\' or '/' or '?' or '*' or '[' or ']'
                ? '-'
                : character)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Source Set" : sanitized[..Math.Min(31, sanitized.Length)];
    }
}

public sealed class ExportWorkbookPresentation : ObservableObject
{
    private string _fileName;
    private int _order;

    internal ExportWorkbookPresentation(
        WorkbookDefinitionId workbookDefinitionId,
        string fileName,
        int order)
    {
        WorkbookDefinitionId = workbookDefinitionId;
        _fileName = fileName;
        _order = order;
    }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value ?? string.Empty);
    }

    public int Order => _order;

    internal void SetOrder(int order) => SetProperty(ref _order, order, nameof(Order));
}

public sealed class ExportSourceSetPresentation
{
    private readonly ObservableCollection<ExportFieldPresentation> _fields = [];
    private readonly ObservableCollection<ExportMetadataFieldPresentation> _metadataFields = [];
    private bool _isEnabled = true;
    private string _displayName;
    private int _datasetOrder;
    private WorkbookDefinitionId _workbookDefinitionId;
    private string _worksheetName;
    private int _worksheetOrder;

    internal ExportSourceSetPresentation(
        DatabaseDatasetSummary dataset,
        WorkbookDefinitionId workbookDefinitionId,
        WorksheetDefinitionId worksheetDefinitionId,
        string worksheetName,
        int worksheetOrder)
    {
        SourceSetId = dataset.SourceSetId;
        WorksheetDefinitionId = worksheetDefinitionId;
        _displayName = dataset.DisplayName;
        _datasetOrder = dataset.Ordinal;
        _workbookDefinitionId = workbookDefinitionId;
        _worksheetName = worksheetName;
        _worksheetOrder = worksheetOrder;
        Fields = new ReadOnlyObservableCollection<ExportFieldPresentation>(_fields);
        MetadataFields = new ReadOnlyObservableCollection<ExportMetadataFieldPresentation>(
            _metadataFields);
        foreach (var field in Enum.GetValues<DatabaseMetadataField>())
        {
            AddMetadata(new ExportMetadataFieldPresentation(
                field,
                DatabaseMetadataNames.GetDisplayName(field)));
        }
        UpdateDataset(dataset);
    }

    public event EventHandler? Changed;

    public SourceSetId SourceSetId { get; }

    public WorksheetDefinitionId WorksheetDefinitionId { get; }

    public string DisplayName => _displayName;

    public int DatasetOrder => _datasetOrder;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnChanged();
            }
        }
    }

    public WorkbookDefinitionId WorkbookDefinitionId => _workbookDefinitionId;

    public string WorksheetName
    {
        get => _worksheetName;
        set
        {
            var next = value ?? string.Empty;
            if (!string.Equals(_worksheetName, next, StringComparison.Ordinal))
            {
                _worksheetName = next;
                OnChanged();
            }
        }
    }

    public int WorksheetOrder => _worksheetOrder;

    public ReadOnlyObservableCollection<ExportFieldPresentation> Fields { get; }

    public ReadOnlyObservableCollection<ExportMetadataFieldPresentation> MetadataFields { get; }

    public void MoveField(ExportFieldPresentation field, int offset)
    {
        var current = _fields.IndexOf(field);
        var next = current + offset;
        if (current < 0 || next < 0 || next >= _fields.Count)
        {
            return;
        }

        _fields.Move(current, next);
        UpdateFieldPositions();
        OnChanged();
    }

    public void ResetFields()
    {
        foreach (var field in _fields)
        {
            field.ResetExport();
        }

        var ordered = _fields.OrderBy(field => field.CanonicalOrder).ToArray();
        _fields.Clear();
        foreach (var field in ordered)
        {
            _fields.Add(field);
        }
        UpdateFieldPositions();
        OnChanged();
    }

    public void ResetHeaders()
    {
        foreach (var field in _fields)
        {
            field.ResetHeader();
        }
        foreach (var field in _metadataFields)
        {
            field.ResetHeader();
        }
        OnChanged();
    }

    internal void UpdateDataset(DatabaseDatasetSummary dataset)
    {
        _displayName = dataset.DisplayName;
        _datasetOrder = dataset.Ordinal;
        var byIdentity = _fields.ToDictionary(field => field.DatabaseColumnIdentity);
        var retainedOrder = _fields
            .Where(field => dataset.Columns.Any(column => column.Identity == field.DatabaseColumnIdentity))
            .ToList();
        foreach (var column in dataset.Columns.OrderBy(column => column.Ordinal))
        {
            if (byIdentity.TryGetValue(column.Identity, out var existing))
            {
                existing.UpdateColumn(column);
            }
            else
            {
                var field = new ExportFieldPresentation(column);
                Subscribe(field);
                retainedOrder.Add(field);
            }
        }

        _fields.Clear();
        foreach (var field in retainedOrder)
        {
            _fields.Add(field);
        }
        UpdateFieldPositions();
    }

    internal void AssignWorkbook(WorkbookDefinitionId workbookDefinitionId, int worksheetOrder)
    {
        _workbookDefinitionId = workbookDefinitionId;
        _worksheetOrder = worksheetOrder;
        OnChanged();
    }

    internal void SetWorksheetOrder(int order)
    {
        if (_worksheetOrder != order)
        {
            _worksheetOrder = order;
            OnChanged();
        }
    }

    internal SourceSetExportConfiguration Capture() => new(
        SourceSetId,
        IsEnabled,
        WorksheetDefinitionId,
        _fields.Select(field => field.Capture()).ToArray(),
        _metadataFields.Select(field => field.Capture()).ToArray());

    private void AddMetadata(ExportMetadataFieldPresentation field)
    {
        Subscribe(field);
        _metadataFields.Add(field);
    }

    private void Subscribe(INotifyPropertyChanged child) =>
        child.PropertyChanged += OnChildPropertyChanged;

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnChanged();

    private void UpdateFieldPositions()
    {
        for (var index = 0; index < _fields.Count; index++)
        {
            _fields[index].SetExportPosition(index + 1);
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class ExportFieldPresentation : ObservableObject
{
    private string _databaseField;
    private string _excelHeader;
    private bool _isExported = true;
    private bool _isSourceIdExported;
    private int _exportPosition;

    internal ExportFieldPresentation(DatabaseColumnDefinition column)
    {
        DatabaseColumnIdentity = column.Identity;
        CanonicalOrder = column.Ordinal;
        _databaseField = column.EffectiveName;
        _excelHeader = column.EffectiveName;
    }

    public DatabaseColumnIdentity DatabaseColumnIdentity { get; }

    public int CanonicalOrder { get; }

    public string DatabaseField => _databaseField;

    public string ExcelHeader
    {
        get => _excelHeader;
        set
        {
            if (SetProperty(ref _excelHeader, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasExcelHeaderOverride));
                OnPropertyChanged(nameof(SourceIdExportHeader));
            }
        }
    }

    public bool HasExcelHeaderOverride => !string.Equals(
        ExcelHeader,
        DatabaseField,
        StringComparison.Ordinal);

    public int ExportPosition => _exportPosition;

    public bool IsExported
    {
        get => _isExported;
        set
        {
            if (SetProperty(ref _isExported, value) && !value)
            {
                IsSourceIdExported = false;
            }
        }
    }

    public bool IsSourceIdExported
    {
        get => _isSourceIdExported;
        set
        {
            if (!value || IsExported)
            {
                SetProperty(ref _isSourceIdExported, value);
            }
        }
    }

    public string SourceIdExportHeader => $"{ExcelHeader} SourceId";

    internal void UpdateColumn(DatabaseColumnDefinition column)
    {
        var followsDatabaseField = !HasExcelHeaderOverride;
        _databaseField = column.EffectiveName;
        OnPropertyChanged(nameof(DatabaseField));
        if (followsDatabaseField)
        {
            ExcelHeader = column.EffectiveName;
        }
    }

    internal void SetExportPosition(int position) =>
        SetProperty(ref _exportPosition, position, nameof(ExportPosition));

    internal void ResetHeader() => ExcelHeader = DatabaseField;

    internal void ResetExport()
    {
        IsExported = true;
        IsSourceIdExported = false;
    }

    internal ExportFieldConfiguration Capture() => new(
        DatabaseColumnIdentity,
        IsExported,
        ExcelHeader,
        IsSourceIdExported);
}

public sealed class ExportMetadataFieldPresentation : ObservableObject
{
    private bool _isExported;
    private string _excelHeader;

    internal ExportMetadataFieldPresentation(
        DatabaseMetadataField metadataField,
        string displayName)
    {
        MetadataField = metadataField;
        DisplayName = displayName;
        _excelHeader = displayName;
    }

    public DatabaseMetadataField MetadataField { get; }

    public string DisplayName { get; }

    public bool IsExported
    {
        get => _isExported;
        set => SetProperty(ref _isExported, value);
    }

    public string ExcelHeader
    {
        get => _excelHeader;
        set => SetProperty(ref _excelHeader, value ?? string.Empty);
    }

    internal void ResetHeader() => ExcelHeader = DisplayName;

    internal ExportMetadataFieldConfiguration Capture() => new(
        MetadataField,
        IsExported,
        ExcelHeader);
}

internal static class DatabaseMetadataNames
{
    internal static string GetDisplayName(DatabaseMetadataField field) => field switch
    {
        DatabaseMetadataField.SourceSet => "Source Set",
        DatabaseMetadataField.SourceFile => "Source File",
        DatabaseMetadataField.FullSourcePath => "Full Source Path",
        DatabaseMetadataField.SourceId => "Source ID",
        DatabaseMetadataField.FileModified => "File Modified",
        DatabaseMetadataField.SourceKind => "Source Kind",
        DatabaseMetadataField.ContainerProvenance => "Container Provenance",
        DatabaseMetadataField.RecordHierarchy => "Record Hierarchy",
        DatabaseMetadataField.ValuePath => "Value / XML Path",
        DatabaseMetadataField.TraversalOrdinal => "Traversal Ordinal",
        DatabaseMetadataField.CandidateKind => "Candidate Kind",
        DatabaseMetadataField.StructuralIdentity => "Structural Identity",
        _ => field.ToString()
    };
}
