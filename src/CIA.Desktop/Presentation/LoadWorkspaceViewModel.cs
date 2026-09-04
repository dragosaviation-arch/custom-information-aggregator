using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly ISourcePathPicker _pathPicker;
    private readonly SourceLoadingCoordinator _loadingCoordinator;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly MainWindowViewModel _shell;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly RelayCommand<LoadedSourceItem> _toggleSourceInclusionCommand;
    private readonly RelayCommand _includeVisibleCommand;
    private readonly RelayCommand _excludeVisibleCommand;
    private bool _includeXmlFiles = true;
    private bool _includeArchives = true;
    private bool _searchSubfolders = true;
    private bool _openDiscoveryWhenLoadingCompletes;
    private bool _isBusy;
    private LoadedSourceItem? _selectedSource;
    private string _filterText = string.Empty;
    private string _statusTitle = "Load workspace ready";
    private string _statusDetail = "No active operation";
    private WorkflowArtifactStatus _discoveryStatus;
    private int _disposed;

    public LoadWorkspaceViewModel(
        ISourcePathPicker pathPicker,
        SourceLoadingCoordinator loadingCoordinator,
        ActiveLoadedSourceSet sourceSet,
        IApplicationWorkflowCoordinator workflowCoordinator,
        MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(pathPicker);
        ArgumentNullException.ThrowIfNull(loadingCoordinator);
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);
        ArgumentNullException.ThrowIfNull(shell);

        _pathPicker = pathPicker;
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
    }

    public ReadOnlyObservableCollection<LoadedSourceItem> Sources { get; }

    public ICollectionView VisibleSources { get; }

    public IAsyncRelayCommand AddXmlFileCommand { get; }

    public IAsyncRelayCommand AddFolderCommand { get; }

    public IAsyncRelayCommand AddArchiveCommand { get; }

    public IRelayCommand ToggleSourceInclusionCommand => _toggleSourceInclusionCommand;

    public IRelayCommand IncludeVisibleCommand => _includeVisibleCommand;

    public IRelayCommand ExcludeVisibleCommand => _excludeVisibleCommand;

    public bool HasSources => Sources.Count > 0;

    public int IncludedCount => Sources.Count(source => source.IsIncluded);

    public string IncludedSummary => $"{IncludedCount} / {Sources.Count} included";

    public string SourceSummary => $"Files found {Sources.Count} · Loaded {Sources.Count} · Warnings 0";

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value))
            {
                return;
            }

            VisibleSources.Refresh();
            NotifyInclusionCommandsCanExecuteChanged();
        }
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
            NotifyInclusionCommandsCanExecuteChanged();
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

    private async Task AddSelectedPathAsync(
        SourceSelectionKind selectionKind,
        Func<string?> pickPath)
    {
        var path = pickPath();

        if (path is null)
        {
            return;
        }

        await AddPathAsync(selectionKind, path, allowNavigation: true);
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
                StatusDetail = result.FailureDescription
                    ?? "The selected source could not be loaded.";
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
        if (item is not LoadedSourceItem source || string.IsNullOrWhiteSpace(FilterText))
        {
            return item is LoadedSourceItem;
        }

        return source.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || source.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleSourceInclusion(LoadedSourceItem? source)
    {
        if (source is null)
        {
            return;
        }

        ApplyInclusionResult(
            _loadingCoordinator.SetInclusion([source], !source.IsIncluded));
    }

    private void SetVisibleInclusion(bool isIncluded)
    {
        var visibleSources = VisibleSources.Cast<LoadedSourceItem>().ToArray();
        ApplyInclusionResult(_loadingCoordinator.SetInclusion(visibleSources, isIncluded));
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
        StatusDetail = result.FailureDescription
            ?? "The source-selection change was rejected.";
    }

    private bool CanChangeVisibleInclusion()
    {
        return !IsBusy && !VisibleSources.IsEmpty;
    }

    private void NotifyInclusionCommandsCanExecuteChanged()
    {
        _toggleSourceInclusionCommand.NotifyCanExecuteChanged();
        _includeVisibleCommand.NotifyCanExecuteChanged();
        _excludeVisibleCommand.NotifyCanExecuteChanged();
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
        NotifyInclusionCommandsCanExecuteChanged();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoadedSourceItem.IsIncluded))
        {
            NotifySourceCountsChanged();
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
