using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class DiscoveryWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IDiscoveryClient _discoveryClient;
    private readonly ActiveDiscoveryConfiguration _activeConfiguration;
    private readonly ActiveLoadedSourceSet _sourceSet;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly DatabaseBuildCoordinator? _databaseBuildCoordinator;
    private readonly ObservableCollection<DiscoveredInformationItemViewModel> _visibleInformation = [];
    private readonly ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> _readOnlyInformation;
    private readonly ObservableCollection<SourceSetLayoutItemViewModel> _sourceSetLayouts = [];
    private readonly ReadOnlyObservableCollection<SourceSetLayoutItemViewModel>
        _readOnlySourceSetLayouts;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly AsyncRelayCommand<DiscoveredInformationItemViewModel?> _selectInformationCommand;
    private readonly AsyncRelayCommand _previousOccurrenceCommand;
    private readonly AsyncRelayCommand _nextOccurrenceCommand;
    private readonly AsyncRelayCommand _jumpToOccurrenceCommand;
    private readonly RelayCommand<DiscoveredInformationItemViewModel> _inspectSourcesCommand;
    private readonly RelayCommand<DiscoveredInformationItemViewModel> _toggleSelectionCommand;
    private readonly RelayCommand<DiscoveredInformationItemViewModel> _toggleBlacklistCommand;
    private readonly RelayCommand _clearDatabaseTagOverrideCommand;
    private readonly RelayCommand _selectVisibleCommand;
    private readonly RelayCommand _deselectVisibleCommand;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private IReadOnlyList<DiscoveredInformationItemViewModel> _allInformation = [];
    private IReadOnlyList<DiscoveredInformationItemViewModel> _filteredInformation = [];
    private string _searchText = string.Empty;
    private string _sortColumn = "Tag";
    private bool _sortAscending = true;
    private int _pageSize = 50;
    private int _currentPage = 1;
    private bool _isBusy;
    private bool _hasCompletedDiscovery;
    private bool _isSourceInspectionOpen;
    private bool _showBlacklisted = true;
    private bool _showOnlySelected;
    private bool _showOnlyDatabaseTagOverrides;
    private int _issueCount;
    private string _progressStage = "Stage: Ready";
    private WorkflowArtifactStatus _discoveryStatus;
    private string _statusTitle = "Discovery ready";
    private string _statusDetail = "Run Discovery against the current active source set.";
    private DiscoveredInformationItemViewModel? _selectedInformation;
    private DiscoveredInformationItemViewModel? _inspectedInformation;
    private OperationId? _publishedDiscoveryOperationId;
    private IReadOnlyDictionary<SourceId, LoadedSourceContract> _publishedDiscoverySources =
        new Dictionary<SourceId, LoadedSourceContract>();
    private string _occurrencePreviewText =
        "Select a discovered tag to inspect its occurrence value.";
    private string _occurrenceOrdinalInput = string.Empty;
    private int _currentOccurrenceOrdinal;
    private int _occurrenceTotal;
    private bool _isOccurrenceLoading;
    private int _previewRequestVersion;
    private int _disposed;

    public DiscoveryWorkspaceViewModel(
        IDiscoveryClient discoveryClient,
        ActiveDiscoveryConfiguration activeConfiguration,
        ActiveLoadedSourceSet sourceSet,
        IApplicationWorkflowCoordinator workflowCoordinator,
        DatabaseBuildCoordinator? databaseBuildCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(activeConfiguration);
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryClient = discoveryClient;
        _activeConfiguration = activeConfiguration;
        _sourceSet = sourceSet;
        _workflowCoordinator = workflowCoordinator;
        _databaseBuildCoordinator = databaseBuildCoordinator;
        _discoveryStatus = workflowCoordinator.Current.Discovery;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _readOnlyInformation = new ReadOnlyObservableCollection<DiscoveredInformationItemViewModel>(
            _visibleInformation);
        _readOnlySourceSetLayouts = new ReadOnlyObservableCollection<SourceSetLayoutItemViewModel>(
            _sourceSetLayouts);

        RunDiscoveryCommand = new AsyncRelayCommand(RunDiscoveryAsync, CanRunDiscovery);
        BuildDatabaseCommand = new AsyncRelayCommand(
            BuildDatabaseAsync,
            CanBuildDatabase);
        SortCommand = new RelayCommand<string>(SortBy, column => column is not null);
        _previousPageCommand = new RelayCommand(
            () => CurrentPage--,
            () => CurrentPage > 1);
        _nextPageCommand = new RelayCommand(
            () => CurrentPage++,
            () => CurrentPage < PageCount);
        _selectInformationCommand = new AsyncRelayCommand<DiscoveredInformationItemViewModel?>(
            SelectInformationAsync,
            _ => true,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        _previousOccurrenceCommand = new AsyncRelayCommand(
            () => NavigateOccurrenceAsync(CurrentOccurrenceOrdinal - 1),
            CanNavigateToPreviousOccurrence);
        _nextOccurrenceCommand = new AsyncRelayCommand(
            () => NavigateOccurrenceAsync(CurrentOccurrenceOrdinal + 1),
            CanNavigateToNextOccurrence);
        _jumpToOccurrenceCommand = new AsyncRelayCommand(
            JumpToOccurrenceAsync,
            CanJumpToOccurrence);
        _inspectSourcesCommand = new RelayCommand<DiscoveredInformationItemViewModel>(
            InspectSources,
            information => information?.SourceCount > 0);
        _toggleSelectionCommand = new RelayCommand<DiscoveredInformationItemViewModel>(
            ToggleSelection,
            CanToggleSelection);
        _toggleBlacklistCommand = new RelayCommand<DiscoveredInformationItemViewModel>(
            ToggleBlacklist,
            CanToggleBlacklist);
        _clearDatabaseTagOverrideCommand = new RelayCommand(
            ClearDatabaseTagOverride,
            CanClearDatabaseTagOverride);
        _selectVisibleCommand = new RelayCommand(
            () => SetVisibleSelection(isSelected: true),
            CanSelectVisible);
        _deselectVisibleCommand = new RelayCommand(
            () => SetVisibleSelection(isSelected: false),
            CanDeselectVisible);
        CloseSourceInspectionCommand = new RelayCommand(
            () => IsSourceInspectionOpen = false);

        foreach (var source in _sourceSet.Items)
        {
            source.PropertyChanged += OnSourcePropertyChanged;
        }

        foreach (var sourceSetDefinition in _sourceSet.SourceSets)
        {
            sourceSetDefinition.PropertyChanged += OnSourceSetPropertyChanged;
        }

        ((INotifyCollectionChanged)_sourceSet.Items).CollectionChanged += OnSourcesChanged;
        ((INotifyCollectionChanged)_sourceSet.SourceSets).CollectionChanged += OnSourceSetsChanged;
        _activeConfiguration.SynchronizeSourceSets(
            _sourceSet.SourceSets.Select(sourceSet => sourceSet.SourceSetId));
        RefreshSourceSetLayouts();
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
        if (_databaseBuildCoordinator is not null)
        {
            _databaseBuildCoordinator.PublishedGenerationChanged +=
                OnPublishedDatabaseGenerationChanged;
        }
        RefreshPresentation();
    }

    public ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> Information =>
        _readOnlyInformation;

    public ReadOnlyObservableCollection<SourceSetLayoutItemViewModel> SourceSetLayouts =>
        _readOnlySourceSetLayouts;

    public IReadOnlyList<RepeatedDataLayoutOption> RepeatedDataLayoutOptions { get; } =
    [
        new(RepeatedDataLayout.AlignRepeatedGroupsByPosition, "Align repeated groups by position"),
        new(RepeatedDataLayout.StructuralRows, "Structural rows"),
        new(RepeatedDataLayout.AllCombinations, "All combinations"),
        new(RepeatedDataLayout.NumberRepeatedValuesIntoColumns, "Number repeated values into columns")
    ];

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 250];

    public IAsyncRelayCommand RunDiscoveryCommand { get; }

    public IAsyncRelayCommand BuildDatabaseCommand { get; }

    public string DatabaseBuildButtonText =>
        _databaseBuildCoordinator?.CurrentGeneration is null
            ? "Create Database"
            : "Update Database";

    public string DatabaseStateText => _workflowCoordinator.Current.Database switch
    {
        WorkflowArtifactStatus.Current => "Database current",
        WorkflowArtifactStatus.Stale => "Database out of date",
        _ => "Database not created"
    };

    public IRelayCommand<string> SortCommand { get; }

    public IRelayCommand PreviousPageCommand => _previousPageCommand;

    public IRelayCommand NextPageCommand => _nextPageCommand;

    public IAsyncRelayCommand<DiscoveredInformationItemViewModel?> SelectInformationCommand =>
        _selectInformationCommand;

    public IAsyncRelayCommand PreviousOccurrenceCommand => _previousOccurrenceCommand;

    public IAsyncRelayCommand NextOccurrenceCommand => _nextOccurrenceCommand;

    public IAsyncRelayCommand JumpToOccurrenceCommand => _jumpToOccurrenceCommand;

    public IRelayCommand<DiscoveredInformationItemViewModel> InspectSourcesCommand =>
        _inspectSourcesCommand;

    public IRelayCommand<DiscoveredInformationItemViewModel> ToggleSelectionCommand =>
        _toggleSelectionCommand;

    public IRelayCommand<DiscoveredInformationItemViewModel> ToggleBlacklistCommand =>
        _toggleBlacklistCommand;

    public IRelayCommand ClearDatabaseTagOverrideCommand => _clearDatabaseTagOverrideCommand;

    public IRelayCommand SelectVisibleCommand => _selectVisibleCommand;

    public IRelayCommand DeselectVisibleCommand => _deselectVisibleCommand;

    public IRelayCommand CloseSourceInspectionCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _currentPage = 1;
                RefreshPresentation();
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (!PageSizes.Contains(value) || !SetProperty(ref _pageSize, value))
            {
                return;
            }

            _currentPage = 1;
            RefreshPresentation();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            var page = Math.Clamp(value, 1, PageCount);
            if (SetProperty(ref _currentPage, page))
            {
                RefreshPresentation();
            }
        }
    }

    public int PageCount => Math.Max(1, (FilteredCount + PageSize - 1) / PageSize);

    public int FilteredCount { get; private set; }

    public bool HasInformation => _allInformation.Count > 0;

    public bool ShowBlacklisted
    {
        get => _showBlacklisted;
        set
        {
            if (SetProperty(ref _showBlacklisted, value))
            {
                _currentPage = 1;
                RefreshPresentation();
            }
        }
    }

    public bool ShowOnlySelected
    {
        get => _showOnlySelected;
        set
        {
            if (SetProperty(ref _showOnlySelected, value))
            {
                _currentPage = 1;
                RefreshPresentation();
            }
        }
    }

    public bool ShowOnlyDatabaseTagOverrides
    {
        get => _showOnlyDatabaseTagOverrides;
        set
        {
            if (SetProperty(ref _showOnlyDatabaseTagOverrides, value))
            {
                _currentPage = 1;
                RefreshPresentation();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(RunButtonText));
                OnPropertyChanged(nameof(DiscoveryStateText));
                OnPropertyChanged(nameof(CanEditDatabaseTagOverride));
                OnPropertyChanged(nameof(CanConfigureRepeatedDataLayout));
                RunDiscoveryCommand.NotifyCanExecuteChanged();
                BuildDatabaseCommand.NotifyCanExecuteChanged();
                NotifyConfigurationCommandsChanged();
                NotifyOccurrenceCommandsChanged();
            }
        }
    }

    public string RunButtonText => IsBusy
        ? "Discovery running"
        : _hasCompletedDiscovery
            ? "↻ Re-run Discovery"
            : "▶ Run Discovery";

    public WorkflowArtifactStatus DiscoveryStatus
    {
        get => _discoveryStatus;
        private set
        {
            if (SetProperty(ref _discoveryStatus, value))
            {
                OnPropertyChanged(nameof(DiscoveryStateText));
                OnPropertyChanged(nameof(CanEditDatabaseTagOverride));
                OnPropertyChanged(nameof(CanConfigureRepeatedDataLayout));
            }
        }
    }

    public string DiscoveryStateText => IsBusy
        ? "Discovery running"
        : DiscoveryStatus switch
        {
            WorkflowArtifactStatus.Current => "Discovery current",
            WorkflowArtifactStatus.Stale => "Discovery out of date",
            _ => "Discovery not available"
        };

    public string IncludedSourceSummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} / {1:N0} sources included",
        _sourceSet.CreateIncludedReadySnapshot().Count,
        _sourceSet.Items.Count);

    public string SelectionSummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} / {1:N0} selected",
        _activeConfiguration.Current.SelectedCount,
        _allInformation.Count);

    public string ShowingSummary
    {
        get
        {
            if (FilteredCount == 0)
            {
                return "Showing 0–0 of 0 tags";
            }

            var first = ((CurrentPage - 1) * PageSize) + 1;
            var last = Math.Min(CurrentPage * PageSize, FilteredCount);
            return string.Format(
                CultureInfo.CurrentCulture,
                "Showing {0:N0}–{1:N0} of {2:N0} tags",
                first,
                last,
                FilteredCount);
        }
    }

    public string PageSummary => $"{CurrentPage} / {PageCount}";

    public string TagHeaderText => HeaderText("Tag", "Tag");

    public string SourceSetHeaderText => HeaderText("SourceSet", "Source Set");

    public string ContextHeaderText => HeaderText("Context", "Context");

    public string DatabaseTagHeaderText => HeaderText("DatabaseTag", "Database Tag");

    public string OccurrencesHeaderText => HeaderText("Occurrences", "Occurrences");

    public string SourcesHeaderText => HeaderText("Sources", "Sources");

    public string SampleHeaderText => HeaderText("Sample", "Sample Value");

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

    public string ProgressStage => IsBusy ? "Stage: Interpreting sources" : _progressStage;

    public string ResultSummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} tags · {1:N0} issues",
        _allInformation.Count,
        _issueCount);

    public DiscoveredInformationItemViewModel? SelectedInformation
    {
        get => _selectedInformation;
        set
        {
            if (SetProperty(ref _selectedInformation, value))
            {
                OnPropertyChanged(nameof(SelectedDatabaseTag));
                OnPropertyChanged(nameof(CanEditDatabaseTagOverride));
                OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
                _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
                PrepareOccurrenceSelection(value);
                _selectInformationCommand.Execute(value);
            }
        }
    }

    public string SelectedDatabaseTag
    {
        get => SelectedInformation?.DatabaseTag ?? string.Empty;
        set
        {
            if (SelectedInformation is not null)
            {
                SetDatabaseTagOverride(SelectedInformation, value);
            }
        }
    }

    public bool CanEditDatabaseTagOverride =>
        SelectedInformation is not null && CanChangeConfiguration();

    public bool CanConfigureRepeatedDataLayout => CanChangeConfiguration();

    public string DatabaseTagOverrideActionText =>
        SelectedInformation?.HasDatabaseTagOverride == true ? "Revert" : "Default";

    public string OccurrencePreviewText
    {
        get => _occurrencePreviewText;
        private set => SetProperty(ref _occurrencePreviewText, value);
    }

    public string OccurrenceOrdinalInput
    {
        get => _occurrenceOrdinalInput;
        set => SetProperty(ref _occurrenceOrdinalInput, value);
    }

    public int CurrentOccurrenceOrdinal
    {
        get => _currentOccurrenceOrdinal;
        private set
        {
            if (SetProperty(ref _currentOccurrenceOrdinal, value))
            {
                NotifyOccurrenceCommandsChanged();
            }
        }
    }

    public int OccurrenceTotal
    {
        get => _occurrenceTotal;
        private set
        {
            if (SetProperty(ref _occurrenceTotal, value))
            {
                OnPropertyChanged(nameof(OccurrenceTotalText));
                NotifyOccurrenceCommandsChanged();
            }
        }
    }

    public string OccurrenceTotalText => string.Format(
        CultureInfo.CurrentCulture,
        "of {0:N0}",
        OccurrenceTotal);

    public bool IsOccurrenceLoading
    {
        get => _isOccurrenceLoading;
        private set
        {
            if (SetProperty(ref _isOccurrenceLoading, value))
            {
                NotifyOccurrenceCommandsChanged();
            }
        }
    }

    public DiscoveredInformationItemViewModel? InspectedInformation
    {
        get => _inspectedInformation;
        private set
        {
            if (SetProperty(ref _inspectedInformation, value))
            {
                OnPropertyChanged(nameof(SourceInspectionSummary));
            }
        }
    }

    public bool IsSourceInspectionOpen
    {
        get => _isSourceInspectionOpen;
        set => SetProperty(ref _isSourceInspectionOpen, value);
    }

    public string SourceInspectionSummary => InspectedInformation is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} sources · {1:N0} occurrences · matches tag aggregate",
            InspectedInformation.SourceCount,
            InspectedInformation.ContributingSources.Sum(source => source.OccurrenceCount));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ((INotifyCollectionChanged)_sourceSet.Items).CollectionChanged -= OnSourcesChanged;
        ((INotifyCollectionChanged)_sourceSet.SourceSets).CollectionChanged -= OnSourceSetsChanged;
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
        if (_databaseBuildCoordinator is not null)
        {
            _databaseBuildCoordinator.PublishedGenerationChanged -=
                OnPublishedDatabaseGenerationChanged;
        }

        foreach (var source in _sourceSet.Items)
        {
            source.PropertyChanged -= OnSourcePropertyChanged;
        }

        foreach (var sourceSet in _sourceSet.SourceSets)
        {
            sourceSet.PropertyChanged -= OnSourceSetPropertyChanged;
        }

        Interlocked.Increment(ref _previewRequestVersion);
    }

    private bool CanRunDiscovery()
    {
        return !IsBusy
            && _workflowCoordinator.Current.ActiveOperation is null
            && _sourceSet.CreateIncludedReadySnapshot().Count > 0;
    }

    private bool CanBuildDatabase()
    {
        return !IsBusy && _databaseBuildCoordinator?.CanBuild() == true;
    }

    private async Task BuildDatabaseAsync()
    {
        if (_databaseBuildCoordinator is not null)
        {
            await _databaseBuildCoordinator.BuildAsync();
        }
    }

    private async Task RunDiscoveryAsync()
    {
        var sources = _sourceSet.CreateIncludedReadySnapshot();
        if (sources.Count == 0)
        {
            StatusTitle = "Discovery not started";
            StatusDetail = "Include at least one ready source before running Discovery.";
            return;
        }

        IsBusy = true;
        StatusTitle = "Discovering information";
        StatusDetail = "Interpreting the current active source set in the Processing Host.";
        NotifyProgressChanged();

        OperationCorrelation? operation = null;

        try
        {
            var begin = await _workflowCoordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
            if (!begin.Accepted || begin.Operation is null)
            {
                StatusTitle = "Discovery not started";
                StatusDetail = begin.Rejection?.Reason
                    ?? "The workflow rejected the Discovery request.";
                return;
            }

            operation = begin.Operation;
            var result = await _discoveryClient.RunAsync(operation, sources);
            var completion = _workflowCoordinator.CompleteOperation(result.Completion);
            SynchronizeDiscoveryStatus();

            if (!result.Accepted || !completion.Accepted)
            {
                _issueCount = result.Issues.Count;
                _progressStage = "Stage: Failed";
                PresentFailure(
                    result.FailureDescription
                    ?? completion.Rejection?.Reason
                    ?? "Discovery did not produce a usable result.");
                return;
            }

            _activeConfiguration.Synchronize(
                result.Information.Select(information => information.Identity));
            _activeConfiguration.SynchronizeSourceSets(
                _sourceSet.SourceSets.Select(sourceSet => sourceSet.SourceSetId)
                    .Concat(result.Information.Select(information => information.SourceSetId)));
            var dispositions = _activeConfiguration.Current.Items.ToDictionary(
                item => item.Identity,
                item => item.Disposition);
            var databaseTagOverrides = _activeConfiguration.DatabaseTagOverridesByIdentity;
            _allInformation = result.Information
                .Select(information => new DiscoveredInformationItemViewModel(
                    information,
                    ResolveSourceSetName(information.SourceSetId),
                    dispositions[information.Identity],
                    databaseTagOverrides.GetValueOrDefault(information.Identity)))
                .ToArray();
            RefreshSourceSetLayouts();
            _publishedDiscoveryOperationId = result.Completion.Correlation.OperationId;
            _publishedDiscoverySources = CreatePublishedSourceSnapshot(
                sources,
                result.Information);
            _issueCount = result.Issues.Count;
            _progressStage = "Stage: Complete";
            _hasCompletedDiscovery = true;
            _currentPage = 1;
            RefreshPresentation();
            SelectedInformation = Information.FirstOrDefault();
            StatusTitle = result.Issues.Count == 0
                ? "Discovery complete"
                : "Discovery complete with issues";
            StatusDetail = "Current result available for the active source set.";
            OnPropertyChanged(nameof(RunButtonText));
        }
        catch (OperationCanceledException)
        {
            if (operation is not null)
            {
                _workflowCoordinator.CompleteOperation(
                    operation.OperationId,
                    OperationOutcome.Cancelled);
                SynchronizeDiscoveryStatus();
            }

            StatusTitle = "Discovery cancelled";
            StatusDetail = "The Discovery operation was cancelled.";
            _progressStage = "Stage: Cancelled";
        }
        catch (Exception)
        {
            if (operation is not null)
            {
                _workflowCoordinator.CompleteOperation(
                    operation.OperationId,
                    OperationOutcome.Failed);
                SynchronizeDiscoveryStatus();
            }

            _progressStage = "Stage: Failed";
            PresentFailure("Discovery could not be completed by the Processing Host.");
        }
        finally
        {
            IsBusy = false;
            NotifyProgressChanged();
        }
    }

    private void PresentFailure(string detail)
    {
        if (_hasCompletedDiscovery)
        {
            StatusTitle = "Discovery re-run failed";
            StatusDetail = $"{detail} Previous Discovery results are retained and marked out of date.";
            return;
        }

        StatusTitle = "Discovery failed";
        StatusDetail = detail;
    }

    private async Task SelectInformationAsync(DiscoveredInformationItemViewModel? information)
    {
        var requestVersion = Volatile.Read(ref _previewRequestVersion);
        if (information is null
            || _publishedDiscoveryOperationId is null
            || !ReferenceEquals(SelectedInformation, information))
        {
            return;
        }

        await LoadOccurrenceAsync(information, ordinal: 1, requestVersion);
    }

    private void PrepareOccurrenceSelection(DiscoveredInformationItemViewModel? information)
    {
        Interlocked.Increment(ref _previewRequestVersion);
        var canLoadOccurrence = information is not null
            && _publishedDiscoveryOperationId is not null;
        IsOccurrenceLoading = canLoadOccurrence;
        CurrentOccurrenceOrdinal = 0;
        OccurrenceTotal = information?.TotalOccurrenceCount ?? 0;
        SetOccurrenceOrdinalInput(string.Empty);

        if (information is null)
        {
            OccurrencePreviewText = "Select a discovered tag to inspect its occurrence value.";
            return;
        }

        if (!canLoadOccurrence)
        {
            OccurrencePreviewText = "The selected occurrence could not be retrieved.";
            return;
        }

        OccurrencePreviewText = "Loading occurrence...";
    }

    private Task NavigateOccurrenceAsync(int ordinal)
    {
        var information = SelectedInformation;
        if (information is null)
        {
            return Task.CompletedTask;
        }

        var requestVersion = Interlocked.Increment(ref _previewRequestVersion);
        return LoadOccurrenceAsync(information, ordinal, requestVersion);
    }

    private Task JumpToOccurrenceAsync()
    {
        if (!int.TryParse(
                OccurrenceOrdinalInput,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var ordinal)
            || ordinal < 1
            || ordinal > OccurrenceTotal)
        {
            RestoreOccurrenceOrdinalInput();
            return Task.CompletedTask;
        }

        if (ordinal == CurrentOccurrenceOrdinal)
        {
            RestoreOccurrenceOrdinalInput();
            return Task.CompletedTask;
        }

        return NavigateOccurrenceAsync(ordinal);
    }

    private async Task LoadOccurrenceAsync(
        DiscoveredInformationItemViewModel information,
        int ordinal,
        int requestVersion)
    {
        var discoveryOperationId = _publishedDiscoveryOperationId;
        if (discoveryOperationId is null)
        {
            return;
        }

        if (!TryCreateOccurrenceLookup(
                discoveryOperationId.Value,
                information,
                ordinal,
                out var lookup))
        {
            if (IsCurrentPreviewRequest(requestVersion, information)
                && CurrentOccurrenceOrdinal == 0)
            {
                OccurrencePreviewText = "The selected occurrence could not be retrieved.";
                IsOccurrenceLoading = false;
            }

            RestoreOccurrenceOrdinalInput();
            return;
        }

        IsOccurrenceLoading = true;

        try
        {
            var result = await _discoveryClient.GetOccurrenceAsync(lookup);

            if (!IsCurrentPreviewRequest(requestVersion, information))
            {
                return;
            }

            var occurrence = result.Occurrence;
            if (!result.Accepted
                || occurrence is null
                || occurrence.Identity != information.Identity
                || occurrence.Ordinal != ordinal
                || occurrence.TotalOccurrenceCount != information.TotalOccurrenceCount
                || occurrence.SourceId != lookup.Source.SourceId)
            {
                if (CurrentOccurrenceOrdinal == 0)
                {
                    OccurrencePreviewText = result.FailureDescription
                        ?? "The selected occurrence could not be retrieved.";
                }

                RestoreOccurrenceOrdinalInput();
                return;
            }

            OccurrencePreviewText = occurrence.Value;
            OccurrenceTotal = occurrence.TotalOccurrenceCount;
            CurrentOccurrenceOrdinal = occurrence.Ordinal;
            SetOccurrenceOrdinalInput(
                occurrence.Ordinal.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            if (IsCurrentPreviewRequest(requestVersion, information)
                && CurrentOccurrenceOrdinal == 0)
            {
                OccurrencePreviewText = "The selected occurrence could not be retrieved.";
            }

            RestoreOccurrenceOrdinalInput();
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _previewRequestVersion))
            {
                IsOccurrenceLoading = false;
            }
        }
    }

    private static IReadOnlyDictionary<SourceId, LoadedSourceContract>
        CreatePublishedSourceSnapshot(
            IEnumerable<LoadedSourceContract> sources,
            IEnumerable<DiscoveredInformation> information)
    {
        var contributingSourceIds = information
            .SelectMany(item => item.ContributingSources)
            .Select(contribution => contribution.SourceId)
            .ToHashSet();
        return sources
            .Where(source => contributingSourceIds.Contains(source.SourceId))
            .ToDictionary(source => source.SourceId);
    }

    private bool TryCreateOccurrenceLookup(
        OperationId discoveryOperationId,
        DiscoveredInformationItemViewModel information,
        int globalOrdinal,
        out DiscoveryOccurrenceLookup lookup)
    {
        var localOrdinal = globalOrdinal;
        foreach (var contribution in information.ContributingSources)
        {
            if (localOrdinal > contribution.OccurrenceCount)
            {
                localOrdinal -= contribution.OccurrenceCount;
                continue;
            }

            if (_publishedDiscoverySources.TryGetValue(contribution.SourceId, out var source))
            {
                lookup = new DiscoveryOccurrenceLookup(
                    discoveryOperationId,
                    information.Identity,
                    globalOrdinal,
                    information.TotalOccurrenceCount,
                    source,
                    localOrdinal,
                    contribution.OccurrenceCount);
                return true;
            }

            break;
        }

        lookup = null!;
        return false;
    }

    private bool IsCurrentPreviewRequest(
        int requestVersion,
        DiscoveredInformationItemViewModel information)
    {
        return Volatile.Read(ref _disposed) == 0
            && requestVersion == Volatile.Read(ref _previewRequestVersion)
            && ReferenceEquals(SelectedInformation, information);
    }

    private bool CanNavigateToPreviousOccurrence()
    {
        return !IsBusy
            && !IsOccurrenceLoading
            && CurrentOccurrenceOrdinal > 1;
    }

    private bool CanNavigateToNextOccurrence()
    {
        return !IsBusy
            && !IsOccurrenceLoading
            && CurrentOccurrenceOrdinal > 0
            && CurrentOccurrenceOrdinal < OccurrenceTotal;
    }

    private bool CanJumpToOccurrence()
    {
        return !IsBusy
            && !IsOccurrenceLoading
            && SelectedInformation is not null
            && CurrentOccurrenceOrdinal > 0;
    }

    private void RestoreOccurrenceOrdinalInput()
    {
        SetOccurrenceOrdinalInput(CurrentOccurrenceOrdinal > 0
            ? CurrentOccurrenceOrdinal.ToString(CultureInfo.InvariantCulture)
            : string.Empty);
    }

    private void SetOccurrenceOrdinalInput(string value)
    {
        OccurrenceOrdinalInput = value;
    }

    private void SortBy(string? column)
    {
        if (column is null)
        {
            return;
        }

        if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        _currentPage = 1;
        NotifyHeaderTextChanged();
        RefreshPresentation();
    }

    private bool CanChangeConfiguration()
    {
        return !IsBusy && DiscoveryStatus == WorkflowArtifactStatus.Current;
    }

    private bool CanToggleSelection(DiscoveredInformationItemViewModel? information)
    {
        return information is not null
            && !information.IsBlacklisted
            && CanChangeConfiguration();
    }

    private bool CanToggleBlacklist(DiscoveredInformationItemViewModel? information)
    {
        return information is not null && CanChangeConfiguration();
    }

    private bool CanClearDatabaseTagOverride()
    {
        return CanEditDatabaseTagOverride
            && SelectedInformation?.HasDatabaseTagOverride == true;
    }

    private bool CanSelectVisible()
    {
        return CanChangeConfiguration()
            && _filteredInformation.Any(
                information => !information.IsSelected && !information.IsBlacklisted);
    }

    private bool CanDeselectVisible()
    {
        return CanChangeConfiguration()
            && _filteredInformation.Any(information => information.IsSelected);
    }

    private void ToggleSelection(DiscoveredInformationItemViewModel? information)
    {
        if (information is null || information.IsBlacklisted)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetSelection(
                [information.Identity],
                !information.IsSelected));
    }

    private void ToggleBlacklist(DiscoveredInformationItemViewModel? information)
    {
        if (information is null)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetBlacklisted(
                    information.Identity,
                    !information.IsBlacklisted)
                ? 1
                : 0);
    }

    private void ClearDatabaseTagOverride()
    {
        if (SelectedInformation is not null)
        {
            SetDatabaseTagOverride(SelectedInformation, databaseTagOverride: null);
        }
    }

    private void SetDatabaseTagOverride(
        DiscoveredInformationItemViewModel information,
        string? databaseTagOverride)
    {
        if (!CanChangeConfiguration())
        {
            OnPropertyChanged(nameof(SelectedDatabaseTag));
            return;
        }

        var candidate = databaseTagOverride?.Trim();
        string? next = string.IsNullOrWhiteSpace(candidate)
            || string.Equals(
                candidate,
                information.InformationType,
                StringComparison.Ordinal)
                ? null
                : candidate;
        if (string.Equals(
                information.DatabaseTagOverride,
                next,
                StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedDatabaseTag));
            return;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted
            || !_activeConfiguration.SetDatabaseTagOverride(
                information.Identity,
                next))
        {
            OnPropertyChanged(nameof(SelectedDatabaseTag));
            return;
        }

        information.ApplyDatabaseTagOverride(next);
        OnPropertyChanged(nameof(SelectedDatabaseTag));
        OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        RefreshPresentation();
    }

    private void SetVisibleSelection(bool isSelected)
    {
        var targets = _filteredInformation
            .Where(information => isSelected
                ? !information.IsSelected && !information.IsBlacklisted
                : information.IsSelected)
            .Select(information => information.Identity)
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetSelection(targets, isSelected));
    }

    private void ApplyConfigurationChange(Func<int> applyChange)
    {
        if (!CanChangeConfiguration())
        {
            return;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted || applyChange() == 0)
        {
            return;
        }

        ApplyActiveConfiguration();
    }

    private void ApplyActiveConfiguration()
    {
        var dispositions = _activeConfiguration.Current.Items.ToDictionary(
            item => item.Identity,
            item => item.Disposition);
        var databaseTagOverrides = _activeConfiguration.DatabaseTagOverridesByIdentity;
        foreach (var information in _allInformation)
        {
            information.ApplyDisposition(dispositions[information.Identity]);
            information.ApplyDatabaseTagOverride(
                databaseTagOverrides.GetValueOrDefault(information.Identity));
        }

        OnPropertyChanged(nameof(SelectedDatabaseTag));
        OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        RefreshPresentation();
    }

    private bool ChangeRepeatedDataLayout(SourceSetId sourceSetId, RepeatedDataLayout layout)
    {
        if (!CanConfigureRepeatedDataLayout
            || !_activeConfiguration.RepeatedDataLayouts.TryGetValue(sourceSetId, out var current)
            || current == layout)
        {
            return false;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted
            || !_activeConfiguration.SetRepeatedDataLayout(sourceSetId, layout))
        {
            return false;
        }

        OnPropertyChanged(nameof(DatabaseStateText));
        return true;
    }

    private void RefreshSourceSetLayouts()
    {
        _activeConfiguration.SynchronizeSourceSets(
            _sourceSet.SourceSets.Select(sourceSet => sourceSet.SourceSetId)
                .Concat(_allInformation.Select(information => information.SourceSetId)));
        var layouts = _activeConfiguration.RepeatedDataLayouts;
        _sourceSetLayouts.Clear();
        foreach (var sourceSet in _sourceSet.SourceSets)
        {
            _sourceSetLayouts.Add(new SourceSetLayoutItemViewModel(
                sourceSet.SourceSetId,
                sourceSet.Name,
                layouts[sourceSet.SourceSetId],
                RepeatedDataLayoutOptions,
                ChangeRepeatedDataLayout));
        }
    }

    private string ResolveSourceSetName(SourceSetId sourceSetId)
    {
        return _sourceSet.SourceSets
            .FirstOrDefault(sourceSet => sourceSet.SourceSetId == sourceSetId)?.Name
            ?? "Unknown Source Set";
    }

    private void RefreshPresentation()
    {
        IEnumerable<DiscoveredInformationItemViewModel> query = _allInformation;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(information =>
                information.InformationType.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.SourceSetName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.StructuralPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.DatabaseTag.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.SampleValue.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        if (!ShowBlacklisted)
        {
            query = query.Where(information => !information.IsBlacklisted);
        }

        if (ShowOnlySelected)
        {
            query = query.Where(information => information.IsSelected);
        }

        if (ShowOnlyDatabaseTagOverrides)
        {
            query = query.Where(information => information.HasDatabaseTagOverride);
        }

        query = ApplySort(query);
        var filtered = query.ToArray();
        _filteredInformation = filtered;
        FilteredCount = filtered.Length;
        _currentPage = Math.Clamp(_currentPage, 1, PageCount);

        var visible = filtered
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToArray();
        if (!_visibleInformation.SequenceEqual(visible))
        {
            _visibleInformation.Clear();
            foreach (var information in visible)
            {
                _visibleInformation.Add(information);
            }
        }

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(HasInformation));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ShowingSummary));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(ResultSummary));
        _previousPageCommand.NotifyCanExecuteChanged();
        _nextPageCommand.NotifyCanExecuteChanged();
        NotifyConfigurationCommandsChanged();
    }

    private IEnumerable<DiscoveredInformationItemViewModel> ApplySort(
        IEnumerable<DiscoveredInformationItemViewModel> information)
    {
        Func<DiscoveredInformationItemViewModel, object> keySelector = _sortColumn switch
        {
            "SourceSet" => item => item.SourceSetName,
            "Context" => item => item.StructuralPath,
            "DatabaseTag" => item => item.DatabaseTag,
            "Occurrences" => item => item.TotalOccurrenceCount,
            "Sources" => item => item.SourceCount,
            "Sample" => item => item.SampleValue,
            _ => item => item.InformationType
        };

        return _sortAscending
            ? information.OrderBy(keySelector, DiscoverySortComparer.Instance)
            : information.OrderByDescending(keySelector, DiscoverySortComparer.Instance);
    }

    private void InspectSources(DiscoveredInformationItemViewModel? information)
    {
        if (information is null)
        {
            return;
        }

        InspectedInformation = information;
        IsSourceInspectionOpen = true;
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

        NotifySourceStateChanged();
    }

    private void OnSourceSetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SourceSetDefinition sourceSet in e.OldItems)
            {
                sourceSet.PropertyChanged -= OnSourceSetPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SourceSetDefinition sourceSet in e.NewItems)
            {
                sourceSet.PropertyChanged += OnSourceSetPropertyChanged;
            }
        }

        RefreshSourceSetLayouts();
        RefreshSourceSetNames();
    }

    private void OnSourceSetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceSetDefinition.Name))
        {
            RefreshSourceSetLayouts();
            RefreshSourceSetNames();
        }
    }

    private void RefreshSourceSetNames()
    {
        foreach (var information in _allInformation)
        {
            information.ApplySourceSetName(ResolveSourceSetName(information.SourceSetId));
        }

        RefreshPresentation();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoadedSourceItem.IsIncluded)
            or nameof(LoadedSourceItem.Status))
        {
            NotifySourceStateChanged();
        }
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot state)
    {
        DispatchToUi(
            () =>
            {
                SynchronizeDiscoveryStatus();
                RunDiscoveryCommand.NotifyCanExecuteChanged();
                BuildDatabaseCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DatabaseStateText));
                OnPropertyChanged(nameof(DatabaseBuildButtonText));
                NotifyConfigurationCommandsChanged();
            });
    }

    private void OnPublishedDatabaseGenerationChanged(
        object? sender,
        CIA.Contracts.Database.DatabaseGenerationSummary generation)
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(DatabaseStateText));
            OnPropertyChanged(nameof(DatabaseBuildButtonText));
            BuildDatabaseCommand.NotifyCanExecuteChanged();
        });
    }

    private void SynchronizeDiscoveryStatus()
    {
        DiscoveryStatus = _workflowCoordinator.Current.Discovery;
    }

    private void NotifySourceStateChanged()
    {
        OnPropertyChanged(nameof(IncludedSourceSummary));
        RunDiscoveryCommand.NotifyCanExecuteChanged();
    }

    private void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(ProgressStage));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void NotifyHeaderTextChanged()
    {
        OnPropertyChanged(nameof(SourceSetHeaderText));
        OnPropertyChanged(nameof(ContextHeaderText));
        OnPropertyChanged(nameof(TagHeaderText));
        OnPropertyChanged(nameof(DatabaseTagHeaderText));
        OnPropertyChanged(nameof(OccurrencesHeaderText));
        OnPropertyChanged(nameof(SourcesHeaderText));
        OnPropertyChanged(nameof(SampleHeaderText));
    }

    private void NotifyConfigurationCommandsChanged()
    {
        _toggleSelectionCommand.NotifyCanExecuteChanged();
        _toggleBlacklistCommand.NotifyCanExecuteChanged();
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        _selectVisibleCommand.NotifyCanExecuteChanged();
        _deselectVisibleCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOccurrenceCommandsChanged()
    {
        _previousOccurrenceCommand.NotifyCanExecuteChanged();
        _nextOccurrenceCommand.NotifyCanExecuteChanged();
        _jumpToOccurrenceCommand.NotifyCanExecuteChanged();
    }

    private string HeaderText(string column, string label)
    {
        if (!string.Equals(_sortColumn, column, StringComparison.Ordinal))
        {
            return $"{label} ↕";
        }

        return $"{label} {(_sortAscending ? "↑" : "↓")}";
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

    private sealed class DiscoverySortComparer : IComparer<object>
    {
        public static DiscoverySortComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            return x is string left && y is string right
                ? StringComparer.OrdinalIgnoreCase.Compare(left, right)
                : Comparer<object>.Default.Compare(x, y);
        }
    }
}

public sealed class DiscoveredInformationItemViewModel : ObservableObject
{
    private DiscoveryInformationDisposition _disposition;
    private string? _databaseTagOverride;
    private string _sourceSetName;

    public DiscoveredInformationItemViewModel(
        DiscoveredInformation information,
        string sourceSetName,
        DiscoveryInformationDisposition disposition,
        string? databaseTagOverride = null)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        Identity = information.Identity;
        _sourceSetName = sourceSetName;
        var candidate = databaseTagOverride?.Trim();
        _databaseTagOverride = string.IsNullOrWhiteSpace(candidate)
            || string.Equals(
                candidate,
                information.InformationType,
                StringComparison.Ordinal)
                ? null
                : candidate;
        TotalOccurrenceCount = information.TotalOccurrenceCount;
        ContributingSources = information.ContributingSources
            .Select(source => new DiscoveredSourceContributionViewModel(source))
            .ToArray();
        SampleValue = information.SampleValue;
        _disposition = disposition;
    }

    public DiscoveryInformationIdentity Identity { get; }

    public SourceSetId SourceSetId => Identity.SourceSetId;

    public string SourceSetName => _sourceSetName;

    public string StructuralPath => Identity.StructuralPath;

    public string InformationType => Identity.InformationType;

    public string DatabaseTag => DatabaseTagOverride ?? InformationType;

    public string? DatabaseTagOverride => _databaseTagOverride;

    public bool HasDatabaseTagOverride => DatabaseTagOverride is not null;

    public int TotalOccurrenceCount { get; }

    public string TotalOccurrenceText => TotalOccurrenceCount.ToString("N0", CultureInfo.CurrentCulture);

    public IReadOnlyList<DiscoveredSourceContributionViewModel> ContributingSources { get; }

    public int SourceCount => ContributingSources.Count;

    public string SourceCountText => SourceCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SampleValue { get; }

    public DiscoveryInformationDisposition Disposition => _disposition;

    public bool IsSelected => Disposition == DiscoveryInformationDisposition.Selected;

    public bool IsBlacklisted => Disposition == DiscoveryInformationDisposition.Blacklisted;

    public string DispositionText => Disposition switch
    {
        DiscoveryInformationDisposition.Selected => "Selected",
        DiscoveryInformationDisposition.Blacklisted => "Excluded",
        _ => "Neutral"
    };

    public string BlacklistActionText => IsBlacklisted ? "Blacklisted" : "Blacklist";

    internal void ApplyDisposition(DiscoveryInformationDisposition disposition)
    {
        if (_disposition == disposition)
        {
            return;
        }

        _disposition = disposition;
        OnPropertyChanged(nameof(Disposition));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsBlacklisted));
        OnPropertyChanged(nameof(DispositionText));
        OnPropertyChanged(nameof(BlacklistActionText));
    }

    internal void ApplyDatabaseTagOverride(string? databaseTagOverride)
    {
        var candidate = databaseTagOverride?.Trim();
        string? next = string.IsNullOrWhiteSpace(candidate)
            || string.Equals(
                candidate,
                InformationType,
                StringComparison.Ordinal)
                ? null
                : candidate;
        if (string.Equals(_databaseTagOverride, next, StringComparison.Ordinal))
        {
            return;
        }

        _databaseTagOverride = next;
        OnPropertyChanged(nameof(DatabaseTagOverride));
        OnPropertyChanged(nameof(DatabaseTag));
        OnPropertyChanged(nameof(HasDatabaseTagOverride));
    }

    internal void ApplySourceSetName(string sourceSetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        SetProperty(ref _sourceSetName, sourceSetName, nameof(SourceSetName));
    }
}

public sealed record RepeatedDataLayoutOption(RepeatedDataLayout Mode, string DisplayName);

public sealed class SourceSetLayoutItemViewModel : ObservableObject
{
    private readonly Func<SourceSetId, RepeatedDataLayout, bool> _changeLayout;
    private RepeatedDataLayout _selectedLayout;

    public SourceSetLayoutItemViewModel(
        SourceSetId sourceSetId,
        string sourceSetName,
        RepeatedDataLayout selectedLayout,
        IReadOnlyList<RepeatedDataLayoutOption> options,
        Func<SourceSetId, RepeatedDataLayout, bool> changeLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changeLayout);
        SourceSetId = sourceSetId;
        SourceSetName = sourceSetName;
        _selectedLayout = selectedLayout;
        Options = options;
        _changeLayout = changeLayout;
    }

    public SourceSetId SourceSetId { get; }

    public string SourceSetName { get; }

    public IReadOnlyList<RepeatedDataLayoutOption> Options { get; }

    public RepeatedDataLayout SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (_selectedLayout != value && _changeLayout(SourceSetId, value))
            {
                SetProperty(ref _selectedLayout, value);
            }
        }
    }
}

public sealed class DiscoveredSourceContributionViewModel
{
    public DiscoveredSourceContributionViewModel(DiscoveredSourceContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        SourceId = contribution.SourceId;
        SourceName = contribution.SourceName;
        OccurrenceCount = contribution.OccurrenceCount;
    }

    public SourceId SourceId { get; }

    public string SourceIdText => SourceId.ToString();

    public string SourceName { get; }

    public int OccurrenceCount { get; }

    public string OccurrenceCountText => OccurrenceCount.ToString("N0", CultureInfo.CurrentCulture);
}
