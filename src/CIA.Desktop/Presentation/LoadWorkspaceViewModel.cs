using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CIA.Contracts.Sources;
using CIA.Desktop.Sources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class LoadWorkspaceViewModel : ObservableObject
{
    private readonly ISourcePathPicker _pathPicker;
    private readonly SourceLoadingCoordinator _loadingCoordinator;
    private bool _isBusy;
    private LoadedSourceItem? _selectedSource;
    private string _statusTitle = "Load workspace ready";
    private string _statusDetail = "No active operation";

    public LoadWorkspaceViewModel(
        ISourcePathPicker pathPicker,
        SourceLoadingCoordinator loadingCoordinator,
        ActiveLoadedSourceSet sourceSet)
    {
        ArgumentNullException.ThrowIfNull(pathPicker);
        ArgumentNullException.ThrowIfNull(loadingCoordinator);
        ArgumentNullException.ThrowIfNull(sourceSet);

        _pathPicker = pathPicker;
        _loadingCoordinator = loadingCoordinator;
        Sources = sourceSet.Items;
        ((INotifyCollectionChanged)Sources).CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(SourceSummary));
        };

        AddXmlFileCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.XmlFile, _pathPicker.PickXmlFile),
            () => !IsBusy);
        AddFolderCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.Folder, _pathPicker.PickFolder),
            () => !IsBusy);
        AddArchiveCommand = new AsyncRelayCommand(
            () => AddSelectedPathAsync(SourceSelectionKind.Archive, _pathPicker.PickArchive),
            () => !IsBusy);
    }

    public ReadOnlyObservableCollection<LoadedSourceItem> Sources { get; }

    public IAsyncRelayCommand AddXmlFileCommand { get; }

    public IAsyncRelayCommand AddFolderCommand { get; }

    public IAsyncRelayCommand AddArchiveCommand { get; }

    public bool HasSources => Sources.Count > 0;

    public string SourceSummary => $"Files found {Sources.Count} · Loaded {Sources.Count} · Warnings 0";

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

    private async Task AddSelectedPathAsync(
        SourceSelectionKind selectionKind,
        Func<string?> pickPath)
    {
        var path = pickPath();

        if (path is null)
        {
            return;
        }

        IsBusy = true;
        StatusTitle = "Loading sources";
        StatusDetail = "Validating the selected path in the Processing Host…";

        try
        {
            var result = await _loadingCoordinator.AddAsync(selectionKind, path);

            if (!result.Accepted)
            {
                StatusTitle = "Source not added";
                StatusDetail = result.FailureDescription ?? "The selected source could not be loaded.";
                return;
            }

            StatusTitle = result.AddedCount == 1 ? "Source loaded" : "Sources loaded";
            StatusDetail = result.DuplicateCount == 0
                ? $"Added {result.AddedCount} supported source(s)."
                : $"Added {result.AddedCount} supported source(s); skipped {result.DuplicateCount} duplicate path(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
