using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CIA.Contracts.Discovery;
using CIA.Desktop.Discovery;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class DatabaseWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly ActiveDiscoveryConfiguration _discoveryConfiguration;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly Dictionary<string, DatabaseColumnPresentation> _columnCache = new(
        StringComparer.Ordinal);
    private readonly ObservableCollection<DatabaseColumnPresentation> _columns = [];
    private readonly ObservableCollection<DatabaseColumnPresentation> _visibleColumns = [];
    private WorkflowArtifactStatus _databaseStatus;
    private int _disposed;

    public DatabaseWorkspaceViewModel(
        ActiveDiscoveryConfiguration discoveryConfiguration,
        IApplicationWorkflowCoordinator workflowCoordinator)
    {
        ArgumentNullException.ThrowIfNull(discoveryConfiguration);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryConfiguration = discoveryConfiguration;
        _workflowCoordinator = workflowCoordinator;
        _uiSynchronizationContext = SynchronizationContext.Current;
        Columns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(_columns);
        VisibleColumns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(
            _visibleColumns);
        MoveColumnUpCommand = new RelayCommand<DatabaseColumnPresentation>(
            MoveColumnUp,
            CanMoveColumnUp);
        MoveColumnDownCommand = new RelayCommand<DatabaseColumnPresentation>(
            MoveColumnDown,
            CanMoveColumnDown);
        ResetColumnLayoutCommand = new RelayCommand(ResetColumnLayout, HasColumns);
        ResetHeadersCommand = new RelayCommand(ResetHeaders, HasColumns);
        ResetExportCommand = new RelayCommand(ResetExport, HasColumns);

        _databaseStatus = workflowCoordinator.Current.Database;
        SynchronizeColumns();
        _discoveryConfiguration.Changed += OnDiscoveryConfigurationChanged;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
    }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> Columns { get; }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> VisibleColumns { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Records { get; } =
        Array.Empty<IReadOnlyDictionary<string, string?>>();

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnUpCommand { get; }

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnDownCommand { get; }

    public IRelayCommand ResetColumnLayoutCommand { get; }

    public IRelayCommand ResetHeadersCommand { get; }

    public IRelayCommand ResetExportCommand { get; }

    public WorkflowArtifactStatus DatabaseStatus
    {
        get => _databaseStatus;
        private set
        {
            if (SetProperty(ref _databaseStatus, value))
            {
                OnPropertyChanged(nameof(DatabaseStateText));
                OnPropertyChanged(nameof(DatabaseStateContext));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateDetail));
            }
        }
    }

    public string DatabaseStateText => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Current => "Database current",
        WorkflowArtifactStatus.Stale => "Database out of date",
        _ => "Database not available"
    };

    public string DatabaseStateContext => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Current =>
            "Discovery configuration aligned · last published Database remains authoritative.",
        WorkflowArtifactStatus.Stale =>
            "Discovery or its configuration changed · the last published Database is out of date.",
        _ => "No Database has been published for the current session."
    };

    public string EmptyStateTitle => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Stale => "Database is out of date",
        WorkflowArtifactStatus.Current => "No Database records available",
        _ => "Database not available"
    };

    public string EmptyStateDetail => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Stale =>
            "The retained Database requires a later creation/update workflow before review.",
        WorkflowArtifactStatus.Current =>
            "No structured Database records are available to review.",
        _ => "Database records will appear after a later Database creation/update workflow."
    };

    public string RecordCountText => "0 / 0 records";

    public string ColumnCountText => $"{VisibleColumns.Count} / {Columns.Count} columns";

    public string ExportFieldCountText =>
        $"{Columns.Count(column => column.IsExported)} / {Columns.Count} fields";

    public string WorkbookExportFieldCountText =>
        $"{Columns.Count(column => column.IsExported)} selected";

    public bool IsExportAvailable => false;

    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CIA",
        "Exports");

    public bool AlwaysAskWhereToExport { get; set; } = true;

    public string ExportFileName { get; set; } = "CIA_Export_YYYY-MM-DD_HHmm.xlsx";

    public bool AutoFitColumns { get; set; } = true;

    public bool FreezeHeaderRow { get; set; } = true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _discoveryConfiguration.Changed -= OnDiscoveryConfigurationChanged;
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
        foreach (var column in _columnCache.Values)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }
    }

    private bool HasColumns()
    {
        return Columns.Count > 0;
    }

    private bool CanMoveColumnUp(DatabaseColumnPresentation? column)
    {
        return column is not null && _columns.IndexOf(column) > 0;
    }

    private bool CanMoveColumnDown(DatabaseColumnPresentation? column)
    {
        return column is not null
            && _columns.IndexOf(column) is var index
            && index >= 0
            && index < _columns.Count - 1;
    }

    private void MoveColumnUp(DatabaseColumnPresentation? column)
    {
        if (column is not null)
        {
            MoveColumn(column, -1);
        }
    }

    private void MoveColumnDown(DatabaseColumnPresentation? column)
    {
        if (column is not null)
        {
            MoveColumn(column, 1);
        }
    }

    private void MoveColumn(DatabaseColumnPresentation column, int offset)
    {
        var currentIndex = _columns.IndexOf(column);
        var nextIndex = currentIndex + offset;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= _columns.Count)
        {
            return;
        }

        _columns.Move(currentIndex, nextIndex);
        UpdatePositionsAndPresentation();
    }

    private void ResetColumnLayout()
    {
        var configuredOrder = GetSelectedConfiguration()
            .Select(item => item.InformationType)
            .ToArray();
        foreach (var column in _columns)
        {
            column.ResetPresentation();
        }

        ReorderColumns(configuredOrder);
        UpdatePositionsAndPresentation();
    }

    private void ResetHeaders()
    {
        foreach (var column in _columns)
        {
            column.ResetExcelHeader();
        }
    }

    private void ResetExport()
    {
        foreach (var column in _columns)
        {
            column.IsExported = true;
        }
    }

    private void SynchronizeColumns()
    {
        var selectedConfiguration = GetSelectedConfiguration();
        var overrides = _discoveryConfiguration.DatabaseTagOverrides;
        var selectedIds = selectedConfiguration
            .Select(item => item.InformationType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in selectedConfiguration)
        {
            if (!_columnCache.TryGetValue(item.InformationType, out var column))
            {
                column = new DatabaseColumnPresentation(
                    item.InformationType,
                    GetEffectiveDatabaseField(item.InformationType, overrides));
                column.PropertyChanged += OnColumnPropertyChanged;
                _columnCache.Add(item.InformationType, column);
            }
            else
            {
                column.UpdateDatabaseField(
                    GetEffectiveDatabaseField(item.InformationType, overrides));
            }
        }

        var retainedOrder = _columns
            .Where(column => selectedIds.Contains(column.InformationType))
            .Select(column => column.InformationType)
            .ToList();
        retainedOrder.AddRange(selectedConfiguration
            .Select(item => item.InformationType)
            .Where(identity => !retainedOrder.Contains(identity, StringComparer.Ordinal)));
        ReorderColumns(retainedOrder);
        UpdatePositionsAndPresentation();
    }

    private IReadOnlyList<DiscoveryConfigurationItem> GetSelectedConfiguration()
    {
        return _discoveryConfiguration.Current.Items
            .Where(item => item.Disposition == DiscoveryInformationDisposition.Selected)
            .ToArray();
    }

    private void ReorderColumns(IEnumerable<string> orderedInformationTypes)
    {
        _columns.Clear();
        foreach (var informationType in orderedInformationTypes)
        {
            if (_columnCache.TryGetValue(informationType, out var column))
            {
                _columns.Add(column);
            }
        }
    }

    private void UpdatePositionsAndPresentation()
    {
        for (var index = 0; index < _columns.Count; index++)
        {
            _columns[index].SetPosition(index + 1);
        }

        RebuildVisibleColumns();
        NotifyColumnSummariesChanged();
        MoveColumnUpCommand.NotifyCanExecuteChanged();
        MoveColumnDownCommand.NotifyCanExecuteChanged();
        ResetColumnLayoutCommand.NotifyCanExecuteChanged();
        ResetHeadersCommand.NotifyCanExecuteChanged();
        ResetExportCommand.NotifyCanExecuteChanged();
    }

    private void RebuildVisibleColumns()
    {
        _visibleColumns.Clear();
        foreach (var column in _columns.Where(column => column.IsVisible))
        {
            _visibleColumns.Add(column);
        }

        OnPropertyChanged(nameof(ColumnCountText));
    }

    private void NotifyColumnSummariesChanged()
    {
        OnPropertyChanged(nameof(ColumnCountText));
        OnPropertyChanged(nameof(ExportFieldCountText));
        OnPropertyChanged(nameof(WorkbookExportFieldCountText));
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DatabaseColumnPresentation.IsVisible))
        {
            RebuildVisibleColumns();
        }

        if (e.PropertyName == nameof(DatabaseColumnPresentation.IsExported))
        {
            OnPropertyChanged(nameof(ExportFieldCountText));
            OnPropertyChanged(nameof(WorkbookExportFieldCountText));
        }
    }

    private void OnDiscoveryConfigurationChanged(object? sender, EventArgs e)
    {
        DispatchToUi(SynchronizeColumns);
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot e)
    {
        DispatchToUi(() => DatabaseStatus = e.Database);
    }

    private void DispatchToUi(Action update)
    {
        var uiContext = _uiSynchronizationContext;
        if (uiContext is null || ReferenceEquals(uiContext, SynchronizationContext.Current))
        {
            update();
            return;
        }

        uiContext.Post(_ => update(), null);
    }

    private static string GetEffectiveDatabaseField(
        string informationType,
        IReadOnlyDictionary<string, string> overrides)
    {
        return overrides.GetValueOrDefault(informationType) ?? informationType;
    }
}

public sealed class DatabaseColumnPresentation : ObservableObject
{
    public const double DefaultWidth = 160;
    private const double MinimumWidth = 60;
    private const double MaximumWidth = 500;
    private string _databaseField;
    private string _excelHeader;
    private bool _isVisible = true;
    private double _width = DefaultWidth;
    private int _position;
    private bool _isExported = true;

    public DatabaseColumnPresentation(string informationType, string databaseField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseField);

        InformationType = informationType;
        _databaseField = databaseField;
        _excelHeader = databaseField;
    }

    public string InformationType { get; }

    public string DatabaseField
    {
        get => _databaseField;
        private set => SetProperty(ref _databaseField, value);
    }

    public string ExcelHeader
    {
        get => _excelHeader;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? DatabaseField
                : value.Trim();
            if (SetProperty(ref _excelHeader, normalized))
            {
                OnPropertyChanged(nameof(HasExcelHeaderOverride));
            }
        }
    }

    public bool HasExcelHeaderOverride => !string.Equals(
        ExcelHeader,
        DatabaseField,
        StringComparison.Ordinal);

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Clamp(value, MinimumWidth, MaximumWidth));
    }

    public int Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public bool IsExported
    {
        get => _isExported;
        set => SetProperty(ref _isExported, value);
    }

    internal void UpdateDatabaseField(string databaseField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseField);
        var followsDatabaseField = !HasExcelHeaderOverride;
        DatabaseField = databaseField;
        if (followsDatabaseField)
        {
            ExcelHeader = databaseField;
        }
        else
        {
            OnPropertyChanged(nameof(HasExcelHeaderOverride));
        }
    }

    internal void SetPosition(int position)
    {
        Position = position;
    }

    internal void ResetPresentation()
    {
        IsVisible = true;
        Width = DefaultWidth;
    }

    internal void ResetExcelHeader()
    {
        ExcelHeader = DatabaseField;
    }
}
