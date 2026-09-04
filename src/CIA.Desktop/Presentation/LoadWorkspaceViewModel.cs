using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CIA.Contracts.Sources;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class LoadWorkspaceViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<string> AvailableStatuses =
        ["All", "Ready", "Unavailable", "Unsupported", "Failed validation"];

    private readonly ISourcePathPicker _pathPicker;
    private readonly ISourceRemovalConfirmation _removalConfirmation;
    private readonly SourceLoadingCoordinator _loadingCoordinator;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly MainWindowViewModel _shell;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly RelayCommand<LoadedSourceItem> _toggleSourceInclusionCommand;
    private readonly RelayCommand _includeVisibleCommand;
    private readonly RelayCommand _excludeVisibleCommand;
    private readonly RelayCommand _removeCheckedCommand;
    private readonly AsyncRelayCommand<LoadedSourceItem> _refreshSourceCommand;
    private readonly AsyncRelayCommand _refreshSelectedCommand;
    private IReadOnlyList<LoadedSourceItem> _highlightedSources = [];
    private bool _includeXmlFiles = true;
    private bool _includeArchives = true;
    private bool _searchSubfolders = true;
    private bool _openDiscoveryWhenLoadingCompletes;
    private bool _dontWarnWhenRemovingEntries;
    private bool _isBusy;
    private bool _isFilterOptionsOpen;
    private LoadedSourceItem? _selectedSource;
    private string _filterText = string.Empty;
    private string _filterStatus = "All";
    private string _minimumSizeMb = string.Empty;
    private string _maximumSizeMb = string.Empty;
    private LoadedSourceStatus? _appliedStatus;
    private double? _appliedMinimumSizeMb;
    private double? _appliedMaximumSizeMb;
    private string _statusTitle = "Load workspace ready";
    private string _statusDetail = "No active operation";
    private WorkflowArtifactStatus _discoveryStatus;
    private int _disposed;

    public LoadWorkspaceViewModel(
        ISourcePathPicker pathPicker,
        ISourceRemovalConfirmation removalConfirmation,
        SourceLoadingCoordinator loadingCoordinator,
        ActiveLoadedSourceSet sourceSet,
        IApplicationWorkflowCoordinator workflowCoordinator,
        MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(pathPicker);
        ArgumentNullException.ThrowIfNull(removalConfirmation);
        ArgumentNullException.ThrowIfNull(loadingCoordinator);
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);
        ArgumentNullException.ThrowIfNull(shell);

        _pathPicker = pathPicker;
        _removalConfirmation = removalConfirmation;
        _loadingCoordinator = loadingCoordinator;
        _workflowCoordinator = workflowCoordinator;
        _shell = shell;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _discoveryStatus = workflowCoordinator.Current.Discovery;

        Sources = sourceSet.Items;
        VisibleSources = CollectionViewSource.GetDefaultView(Sources);
        VisibleSources.Filter = MatchesFilter;

        foreach (var source in Sources)
        {
            source.PropertyChanged += OnSourcePropertyChanged;
        }

        ((INotifyCollectionChanged)Sources).CollectionChanged += OnSourcesChanged;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;

        AddXmlFileCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.XmlFile, _pathPicker.PickXmlFile),
            () => !IsBusy);
        AddFolderCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.Folder, _pathPicker.PickFolder),
            () => !IsBusy);
        AddArchiveCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.Archive, _pathPicker.PickArchive),
            () => !IsBusy);
        _toggleSourceInclusionCommand = new RelayCommand<LoadedSourceItem>(
            ToggleSourceInclusion,
            source => source is not null && !IsBusy);
        _includeVisibleCommand = new RelayCommand(
            () => SetVisibleInclusion(isIncluded: true),
            CanChangeVisibleInclusion);
        _excludeVisibleCommand = new RelayCommand(
            () => SetVisibleInclusion(isIncluded: false),
            CanChangeVisibleInclusion);
        _removeCheckedCommand = new RelayCommand(RemoveChecked, CanRemoveChecked);
        _refreshSourceCommand = new AsyncRelayCommand<LoadedSourceItem>(
            RefreshSourceAsync,
            source => source is not null && !IsBusy);
        _refreshSelectedCommand = new AsyncRelayCommand(
            RefreshSelectedAsync,
            () => !IsBusy && _highlightedSources.Count > 0);
        ToggleFilterOptionsCommand = new RelayCommand(
            () => IsFilterOptionsOpen = !IsFilterOptionsOpen);
        ApplyFilterOptionsCommand = new RelayCommand(ApplyFilterOptions);
        ClearFilterOptionsCommand = new RelayCommand(ClearFilterOptions);
    }

    public ReadOnlyObservableCollection<LoadedSourceItem> Sources { get; }
    public ICollectionView VisibleSources { get; }
    public IAsyncRelayCommand AddXmlFileCommand { get; }
    public IAsyncRelayCommand AddFolderCommand { get; }
    public IAsyncRelayCommand AddArchiveCommand { get; }
    public IRelayCommand ToggleSourceInclusionCommand => _toggleSourceInclusionCommand;
    public IRelayCommand IncludeVisibleCommand => _includeVisibleCommand;
    public IRelayCommand ExcludeVisibleCommand => _excludeVisibleCommand;
    public IRelayCommand RemoveCheckedCommand => _removeCheckedCommand;
    public IAsyncRelayCommand RefreshSourceCommand => _refreshSourceCommand;
    public IAsyncRelayCommand RefreshSelectedCommand => _refreshSelectedCommand;
    public IRelayCommand ToggleFilterOptionsCommand { get; }
    public IRelayCommand ApplyFilterOptionsCommand { get; }
    public IRelayCommand ClearFilterOptionsCommand { get; }
    public IReadOnlyList<string> FilterStatuses => AvailableStatuses;
    public bool HasSources => Sources.Count > 0;
    public int IncludedCount => Sources.Count(source => source.IsIncluded);
    public string IncludedSummary => $"{IncludedCount} / {Sources.Count} included";
    public string SourceSummary =>
        $"Files found {Sources.Count} · Loaded {Sources.Count} · Issues {Sources.Count(source => source.Status != LoadedSourceStatus.Ready)}";

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RefreshVisibleSources();
            }
        }
    }

    public string FilterStatus
    {
        get => _filterStatus;
        set => SetProperty(ref _filterStatus, value);
    }

    public string MinimumSizeMb
    {
        get => _minimumSizeMb;
        set => SetProperty(ref _minimumSizeMb, value);
    }

    public string MaximumSizeMb
    {
        get => _maximumSizeMb;
        set => SetProperty(ref _maximumSizeMb, value);
    }

    public bool IsFilterOptionsOpen
    {
        get => _isFilterOptionsOpen;
        set => SetProperty(ref _isFilterOptionsOpen, value);
    }

    public bool IncludeXmlFiles
    {
        get => _includeXmlFiles;
        set => SetProperty(ref _includeXmlFiles, value);
    }

    public bool IncludeArchives
    {
        get => _includeArchives;
        set => SetProperty(ref _includeArchives, value);
    }

    public bool SearchSubfolders
    {
        get => _searchSubfolders;
        set => SetProperty(ref _searchSubfolders, value);
    }

    public bool OpenDiscoveryWhenLoadingCompletes
    {
        get => _openDiscoveryWhenLoadingCompletes;
        set => SetProperty(ref _openDiscoveryWhenLoadingCompletes, value);
    }

    public bool DontWarnWhenRemovingEntries
    {
        get => _dontWarnWhenRemovingEntries;
        set => SetProperty(ref _dontWarnWhenRemovingEntries, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            AddXmlFileCommand.NotifyCanExecuteChanged();
            AddFolderCommand.NotifyCanExecuteChanged();
            AddArchiveCommand.NotifyCanExecuteChanged();
            NotifySourceCommandsCanExecuteChanged();
        }
    }

    public LoadedSourceItem? SelectedSource
    {
        get => _selectedSource;
        set => SetProperty(ref _selectedSource, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public WorkflowArtifactStatus DiscoveryStatus
    {
        get => _discoveryStatus;
        private set
        {
            if (!SetProperty(ref _discoveryStatus, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DiscoveryStatusText));
            OnPropertyChanged(nameof(DiscoveryStatusDetail));
        }
    }

    public string DiscoveryStatusText => DiscoveryStatus switch
    {
        WorkflowArtifactStatus.Current => "● Discovery current",
        WorkflowArtifactStatus.Stale => "● Discovery out of date",
        _ => "● Discovery not available"
    };

    public string DiscoveryStatusDetail => DiscoveryStatus switch
    {
        WorkflowArtifactStatus.Current => "Discovery reflects the active source set.",
        WorkflowArtifactStatus.Stale => "Source-set changes require Discovery to be run again.",
        _ => "Add supported sources before running Discovery."
    };

    public async Task AddDroppedPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var acceptedAny = false;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var selectionKind = Directory.Exists(path)
                ? SourceSelectionKind.Folder
                : string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase)
                    ? SourceSelectionKind.XmlFile
                    : SourceSelectionKind.Archive;
            acceptedAny |= await AddPathAsync(selectionKind, path, allowNavigation: false);
        }

        if (acceptedAny && OpenDiscoveryWhenLoadingCompletes)
        {
            NavigateToDiscovery();
        }
    }

    public void SetHighlightedSources(IEnumerable<LoadedSourceItem> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _highlightedSources = sources.Where(Sources.Contains).Distinct().ToArray();
        _refreshSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ((INotifyCollectionChanged)Sources).CollectionChanged -= OnSourcesChanged;
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;

        foreach (var source in Sources)
        {
            source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }

    private async Task AddSelectedPathAsync(SourceSelectionKind selectionKind, Func<string?> pickPath)
    {
        var path = pickPath();

        if (path is not null)
        {
            await AddPathAsync(selectionKind, path, allowNavigation: true);
        }
    }

    private async Task<bool> AddPathAsync(
        SourceSelectionKind selectionKind,
        string path,
        bool allowNavigation)
    {
        IsBusy = true;
        StatusTitle = "Loading sources";
        StatusDetail = "Validating the selected path in the Processing Host…";

        try
        {
            var settings = selectionKind == SourceSelectionKind.Folder
                ? new SourceLoadSettings(IncludeXmlFiles, IncludeArchives, SearchSubfolders)
                : SourceLoadSettings.Default;
            var result = await _loadingCoordinator.AddAsync(selectionKind, path, settings);

            if (!result.Accepted)
            {
                StatusTitle = "Source not added";
                StatusDetail = result.FailureDescription ?? "The selected source could not be loaded.";
                return false;
            }

            SelectedSource = Sources.LastOrDefault();
            StatusTitle = result.AddedCount == 1 ? "Source loaded" : "Sources loaded";
            StatusDetail = result.DuplicateCount == 0
                ? $"Added {result.AddedCount} supported source(s)."
                : $"Added {result.AddedCount} supported source(s); skipped {result.DuplicateCount} duplicate path(s).";

            if (allowNavigation && OpenDiscoveryWhenLoadingCompletes)
            {
                NavigateToDiscovery();
            }

            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not LoadedSourceItem source)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(FilterText)
            && !source.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            && !source.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_appliedStatus is not null && source.Status != _appliedStatus)
        {
            return false;
        }

        if ((_appliedMinimumSizeMb is not null || _appliedMaximumSizeMb is not null)
            && source.SizeBytes is null)
        {
            return false;
        }

        var sizeMb = source.SizeBytes / (1024d * 1024d);
        return (_appliedMinimumSizeMb is null || sizeMb >= _appliedMinimumSizeMb)
            && (_appliedMaximumSizeMb is null || sizeMb <= _appliedMaximumSizeMb);
    }

    private void ApplyFilterOptions()
    {
        if (!TryParseOptionalSize(MinimumSizeMb, out var minimum)
            || !TryParseOptionalSize(MaximumSizeMb, out var maximum)
            || minimum is not null && maximum is not null && minimum > maximum)
        {
            StatusTitle = "Filter options unchanged";
            StatusDetail = "Size filters must be non-negative numbers with minimum no greater than maximum.";
            return;
        }

        _appliedStatus = FilterStatus switch
        {
            "Ready" => LoadedSourceStatus.Ready,
            "Unavailable" => LoadedSourceStatus.Unavailable,
            "Unsupported" => LoadedSourceStatus.Unsupported,
            "Failed validation" => LoadedSourceStatus.FailedValidation,
            _ => null
        };
        _appliedMinimumSizeMb = minimum;
        _appliedMaximumSizeMb = maximum;
        IsFilterOptionsOpen = false;
        RefreshVisibleSources();
        StatusTitle = "Source filters applied";
        StatusDetail = $"Showing {VisibleSources.Cast<object>().Count()} matching source(s).";
    }

    private void ClearFilterOptions()
    {
        FilterStatus = "All";
        MinimumSizeMb = string.Empty;
        MaximumSizeMb = string.Empty;
        _appliedStatus = null;
        _appliedMinimumSizeMb = null;
        _appliedMaximumSizeMb = null;
        IsFilterOptionsOpen = false;
        RefreshVisibleSources();
    }

    private static bool TryParseOptionalSize(string text, out double? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
             && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            || !double.IsFinite(parsed)
            || parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private void ToggleSourceInclusion(LoadedSourceItem? source)
    {
        if (source is not null)
        {
            ApplyInclusionResult(_loadingCoordinator.SetInclusion([source], !source.IsIncluded));
        }
    }

    private void SetVisibleInclusion(bool isIncluded)
    {
        ApplyInclusionResult(
            _loadingCoordinator.SetInclusion(VisibleSources.Cast<LoadedSourceItem>().ToArray(), isIncluded));
    }

    private void ApplyInclusionResult(SourceInclusionResult result)
    {
        if (result.Accepted)
        {
            StatusTitle = "Source selection updated";
            StatusDetail = result.ChangedCount == 1
                ? "Updated one source."
                : $"Updated {result.ChangedCount} sources.";
            return;
        }

        StatusTitle = "Source selection unchanged";
        StatusDetail = result.FailureDescription ?? "The source-selection change was rejected.";
    }

    private void RemoveChecked()
    {
        var targets = Sources.Where(source => source.IsIncluded).ToArray();

        if (targets.Length == 0
            || !DontWarnWhenRemovingEntries && !_removalConfirmation.Confirm(targets.Length))
        {
            return;
        }

        var result = _loadingCoordinator.Remove(targets);

        if (!result.Accepted)
        {
            StatusTitle = "Sources not removed";
            StatusDetail = result.FailureDescription ?? "The source removal was rejected.";
            return;
        }

        if (SelectedSource is not null && targets.Contains(SelectedSource))
        {
            SelectedSource = Sources.FirstOrDefault();
        }

        StatusTitle = result.RemovedCount == 1 ? "Source removed" : "Sources removed";
        StatusDetail = result.RemovedCount == 1
            ? "Removed 1 entry from this session. The source file was not deleted."
            : $"Removed {result.RemovedCount} entries from this session. Source files were not deleted.";
    }

    private async Task RefreshSourceAsync(LoadedSourceItem? source)
    {
        if (source is null)
        {
            return;
        }

        IsBusy = true;
        StatusTitle = "Refreshing source";
        StatusDetail = $"Reloading {source.DisplayName} through the Processing Host…";

        try
        {
            ApplyRefreshResult(source, await _loadingCoordinator.RefreshAsync(source));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshSelectedAsync()
    {
        var targets = _highlightedSources.Where(Sources.Contains).ToArray();

        if (targets.Length == 0)
        {
            return;
        }

        IsBusy = true;
        StatusTitle = targets.Length == 1 ? "Refreshing source" : "Refreshing selected sources";
        StatusDetail = "Reloading highlighted source rows through the Processing Host…";

        try
        {
            var refreshedCount = 0;
            var failedCount = 0;

            foreach (var source in targets)
            {
                var result = await _loadingCoordinator.RefreshAsync(source);
                refreshedCount += result.Accepted ? 1 : 0;
                failedCount += result.Accepted ? 0 : 1;
            }

            RefreshVisibleSources();
            StatusTitle = failedCount == 0
                ? "Selected sources refreshed"
                : "Source refresh completed with issues";
            StatusDetail = $"Refreshed {refreshedCount}; {failedCount} could not be refreshed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyRefreshResult(LoadedSourceItem source, SourceRefreshResult result)
    {
        RefreshVisibleSources();
        NotifySourceCountsChanged();
        StatusTitle = result.Accepted ? "Source refreshed" : "Source refresh failed";
        StatusDetail = result.Accepted
            ? $"Reloaded {source.DisplayName}; its source identity was retained."
            : result.FailureDescription ?? "The selected source could not be refreshed.";
    }

    private bool CanChangeVisibleInclusion() => !IsBusy && !VisibleSources.IsEmpty;

    private bool CanRemoveChecked() => !IsBusy && Sources.Any(source => source.IsIncluded);

    private void RefreshVisibleSources()
    {
        VisibleSources.Refresh();
        NotifySourceCommandsCanExecuteChanged();
    }

    private void NotifySourceCommandsCanExecuteChanged()
    {
        _toggleSourceInclusionCommand.NotifyCanExecuteChanged();
        _includeVisibleCommand.NotifyCanExecuteChanged();
        _excludeVisibleCommand.NotifyCanExecuteChanged();
        _removeCheckedCommand.NotifyCanExecuteChanged();
        _refreshSourceCommand.NotifyCanExecuteChanged();
        _refreshSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NavigateToDiscovery()
    {
        _shell.SelectedWorkspace = _shell.Workspaces.Single(
            workspace => workspace.Area == WorkspaceArea.Discovery);
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (LoadedSourceItem source in e.OldItems)
            {
                source.PropertyChanged -= OnSourcePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (LoadedSourceItem source in e.NewItems)
            {
                source.PropertyChanged += OnSourcePropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasSources));
        NotifySourceCountsChanged();
        NotifySourceCommandsCanExecuteChanged();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoadedSourceItem.IsIncluded)
            or nameof(LoadedSourceItem.Status)
            or nameof(LoadedSourceItem.SizeBytes))
        {
            NotifySourceCountsChanged();
            RefreshVisibleSources();
        }
    }

    private void NotifySourceCountsChanged()
    {
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(IncludedSummary));
        OnPropertyChanged(nameof(SourceSummary));
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot state)
    {
        DispatchToUi(() => DiscoveryStatus = state.Discovery);
    }

    private void DispatchToUi(Action update)
    {
        var uiContext = _uiSynchronizationContext;
        if (uiContext is null || ReferenceEquals(uiContext, SynchronizationContext.Current))
        {
            update();
            return;
        }

        uiContext.Post(static state => ((Action)state!).Invoke(), update);
    }
}
