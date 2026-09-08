using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Core.Database;
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
    private readonly List<string> _publishedColumnOrder = [];
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
        foreach (var column in _columns)
        {
            column.ResetPresentation();
        }

        ReorderColumns(_publishedColumnOrder);
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

    private IReadOnlyList<DatabaseColumnMapping> CapturePublishedColumns()
    {
        return DatabaseTagMapper.CreateMapping(
                _discoveryConfiguration.Current,
                _discoveryConfiguration.DatabaseTagOverrides)
            .Columns;
    }

    private void PublishDatabaseGeneration(
        IReadOnlyList<DatabaseColumnMapping> publishedColumns)
    {
        var publishedIds = publishedColumns
            .Select(column => CreateColumnIdentity(column.SourceInformationTypes))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var publishedColumn in publishedColumns)
        {
            var columnIdentity = CreateColumnIdentity(
                publishedColumn.SourceInformationTypes);
            if (!_columnCache.TryGetValue(columnIdentity, out var column))
            {
                column = new DatabaseColumnPresentation(
                    columnIdentity,
                    publishedColumn);
                column.PropertyChanged += OnColumnPropertyChanged;
                _columnCache.Add(columnIdentity, column);
            }
            else
            {
                column.UpdateMapping(publishedColumn);
            }
        }

        foreach (var removedIdentity in _columnCache.Keys
                     .Where(identity => !publishedIds.Contains(identity))
                     .ToArray())
        {
            _columnCache[removedIdentity].PropertyChanged -= OnColumnPropertyChanged;
            _columnCache.Remove(removedIdentity);
        }

        var retainedPresentationOrder = _columns
            .Where(column => publishedIds.Contains(column.MappingIdentity))
            .Select(column => column.MappingIdentity)
            .ToList();
        retainedPresentationOrder.AddRange(publishedColumns
            .Select(column => CreateColumnIdentity(column.SourceInformationTypes))
            .Where(identity => !retainedPresentationOrder.Contains(
                identity,
                StringComparer.Ordinal)));

        _publishedColumnOrder.Clear();
        _publishedColumnOrder.AddRange(
            publishedColumns.Select(column => CreateColumnIdentity(
                column.SourceInformationTypes)));
        ReorderColumns(retainedPresentationOrder);
        UpdatePositionsAndPresentation();
    }

    private void ReorderColumns(IEnumerable<string> orderedColumnIdentities)
    {
        _columns.Clear();
        foreach (var columnIdentity in orderedColumnIdentities)
        {
            if (_columnCache.TryGetValue(columnIdentity, out var column))
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

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot e)
    {
        var publishedColumns = IsSuccessfulDatabasePublication(e)
            ? CapturePublishedColumns()
            : null;
        DispatchToUi(() =>
        {
            if (publishedColumns is not null)
            {
                PublishDatabaseGeneration(publishedColumns);
            }

            DatabaseStatus = e.Database;
        });
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

    private static string CreateColumnIdentity(
        IReadOnlyList<string> sourceInformationTypes)
    {
        var identity = new StringBuilder();
        foreach (var informationType in sourceInformationTypes)
        {
            identity.Append(informationType.Length);
            identity.Append(':');
            identity.Append(informationType);
        }

        return identity.ToString();
    }

    private static bool IsSuccessfulDatabasePublication(WorkflowStateSnapshot state)
    {
        return state.Database == WorkflowArtifactStatus.Current
            && state.ActiveOperation is null
            && state.LatestOperation is
            {
                Kind: WorkflowOperationKind.DatabaseBuild,
                State: WorkflowOperationState.CompletedSuccessfully
                    or WorkflowOperationState.CompletedWithIssues
            };
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

    internal DatabaseColumnPresentation(
        string mappingIdentity,
        DatabaseColumnMapping mapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingIdentity);
        ArgumentNullException.ThrowIfNull(mapping);

        MappingIdentity = mappingIdentity;
        SourceInformationTypes = mapping.SourceInformationTypes;
        _databaseField = mapping.DatabaseTagName;
        _excelHeader = mapping.DatabaseTagName;
    }

    internal string MappingIdentity { get; }

    public string InformationType => SourceInformationTypes[0];

    public IReadOnlyList<string> SourceInformationTypes { get; }

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

    internal void UpdateMapping(DatabaseColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (!SourceInformationTypes.SequenceEqual(
                mapping.SourceInformationTypes,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "A published Database column cannot change its source information identities.");
        }

        var followsDatabaseField = !HasExcelHeaderOverride;
        DatabaseField = mapping.DatabaseTagName;
        if (followsDatabaseField)
        {
            ExcelHeader = mapping.DatabaseTagName;
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
