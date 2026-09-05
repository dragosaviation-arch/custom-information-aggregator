using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Discovery;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class DiscoveryWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IDiscoveryClient _discoveryClient;
    private readonly ActiveLoadedSourceSet _sourceSet;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly ObservableCollection<DiscoveredInformationItemViewModel> _visibleInformation = [];
    private readonly ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> _readOnlyInformation;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand<DiscoveredInformationItemViewModel> _inspectSourcesCommand;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private IReadOnlyList<DiscoveredInformationItemViewModel> _allInformation = [];
    private string _searchText = string.Empty;
    private string _sortColumn = "Tag";
    private bool _sortAscending = true;
    private int _pageSize = 50;
    private int _currentPage = 1;
    private bool _isBusy;
    private bool _hasCompletedDiscovery;
    private bool _isSourceInspectionOpen;
    private int _issueCount;
    private WorkflowArtifactStatus _discoveryStatus;
    private string _statusTitle = "Discovery ready";
    private string _statusDetail = "Run Discovery against the current active source set.";
    private DiscoveredInformationItemViewModel? _selectedInformation;
    private DiscoveredInformationItemViewModel? _inspectedInformation;
    private int _disposed;

    public DiscoveryWorkspaceViewModel(
        IDiscoveryClient discoveryClient,
        ActiveLoadedSourceSet sourceSet,
        IApplicationWorkflowCoordinator workflowCoordinator)
    {
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryClient = discoveryClient;
        _sourceSet = sourceSet;
        _workflowCoordinator = workflowCoordinator;
        _discoveryStatus = workflowCoordinator.Current.Discovery;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _readOnlyInformation = new ReadOnlyObservableCollection<DiscoveredInformationItemViewModel>(
            _visibleInformation);

        RunDiscoveryCommand = new AsyncRelayCommand(RunDiscoveryAsync, CanRunDiscovery);
        SortCommand = new RelayCommand<string>(SortBy, column => column is not null);
        _previousPageCommand = new RelayCommand(
            () => CurrentPage--,
            () => CurrentPage > 1);
        _nextPageCommand = new RelayCommand(
            () => CurrentPage++,
            () => CurrentPage < PageCount);
        _inspectSourcesCommand = new RelayCommand<DiscoveredInformationItemViewModel>(
            InspectSources,
            information => information?.SourceCount > 0);
        CloseSourceInspectionCommand = new RelayCommand(
            () => IsSourceInspectionOpen = false);

        foreach (var source in _sourceSet.Items)
        {
            source.PropertyChanged += OnSourcePropertyChanged;
        }

        ((INotifyCollectionChanged)_sourceSet.Items).CollectionChanged += OnSourcesChanged;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
        RefreshPresentation();
    }

    public ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> Information =>
        _readOnlyInformation;

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 250];

    public IAsyncRelayCommand RunDiscoveryCommand { get; }

    public IRelayCommand<string> SortCommand { get; }

    public IRelayCommand PreviousPageCommand => _previousPageCommand;

    public IRelayCommand NextPageCommand => _nextPageCommand;

    public IRelayCommand<DiscoveredInformationItemViewModel> InspectSourcesCommand =>
        _inspectSourcesCommand;

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(RunButtonText));
                OnPropertyChanged(nameof(DiscoveryStateText));
                RunDiscoveryCommand.NotifyCanExecuteChanged();
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
        "0 / {0:N0} selected",
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

    public string ProgressStage => IsBusy ? "Stage: Interpreting sources" : "Stage: Complete";

    public string ResultSummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} tags · {1:N0} issues",
        _allInformation.Count,
        _issueCount);

    public DiscoveredInformationItemViewModel? SelectedInformation
    {
        get => _selectedInformation;
        set => SetProperty(ref _selectedInformation, value);
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
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;

        foreach (var source in _sourceSet.Items)
        {
            source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }

    private bool CanRunDiscovery()
    {
        return !IsBusy
            && _workflowCoordinator.Current.ActiveOperation is null
            && _sourceSet.CreateIncludedReadySnapshot().Count > 0;
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

            if (!result.Accepted || !completion.Accepted)
            {
                StatusTitle = "Discovery failed";
                StatusDetail = result.FailureDescription
                    ?? completion.Rejection?.Reason
                    ?? "Discovery did not produce a usable result.";
                return;
            }

            _allInformation = result.Information
                .Select(information => new DiscoveredInformationItemViewModel(information))
                .ToArray();
            _issueCount = result.Issues.Count;
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
            }

            StatusTitle = "Discovery cancelled";
            StatusDetail = "The Discovery operation was cancelled.";
        }
        catch (Exception)
        {
            if (operation is not null)
            {
                _workflowCoordinator.CompleteOperation(
                    operation.OperationId,
                    OperationOutcome.Failed);
            }

            StatusTitle = "Discovery failed";
            StatusDetail = "Discovery could not be completed by the Processing Host.";
        }
        finally
        {
            IsBusy = false;
            NotifyProgressChanged();
        }
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

    private void RefreshPresentation()
    {
        IEnumerable<DiscoveredInformationItemViewModel> query = _allInformation;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(information =>
                information.InformationType.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.SampleValue.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        query = ApplySort(query);
        var filtered = query.ToArray();
        FilteredCount = filtered.Length;
        _currentPage = Math.Clamp(_currentPage, 1, PageCount);

        _visibleInformation.Clear();
        foreach (var information in filtered
                     .Skip((CurrentPage - 1) * PageSize)
                     .Take(PageSize))
        {
            _visibleInformation.Add(information);
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
    }

    private IEnumerable<DiscoveredInformationItemViewModel> ApplySort(
        IEnumerable<DiscoveredInformationItemViewModel> information)
    {
        Func<DiscoveredInformationItemViewModel, object> keySelector = _sortColumn switch
        {
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
                DiscoveryStatus = state.Discovery;
                RunDiscoveryCommand.NotifyCanExecuteChanged();
            });
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
        OnPropertyChanged(nameof(TagHeaderText));
        OnPropertyChanged(nameof(DatabaseTagHeaderText));
        OnPropertyChanged(nameof(OccurrencesHeaderText));
        OnPropertyChanged(nameof(SourcesHeaderText));
        OnPropertyChanged(nameof(SampleHeaderText));
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

public sealed class DiscoveredInformationItemViewModel
{
    public DiscoveredInformationItemViewModel(DiscoveredInformation information)
    {
        ArgumentNullException.ThrowIfNull(information);
        InformationType = information.InformationType;
        DatabaseTag = information.InformationType;
        TotalOccurrenceCount = information.TotalOccurrenceCount;
        ContributingSources = information.ContributingSources
            .Select(source => new DiscoveredSourceContributionViewModel(source))
            .ToArray();
        SampleValue = information.SampleValue;
    }

    public string InformationType { get; }

    public string DatabaseTag { get; }

    public int TotalOccurrenceCount { get; }

    public string TotalOccurrenceText => TotalOccurrenceCount.ToString("N0", CultureInfo.CurrentCulture);

    public IReadOnlyList<DiscoveredSourceContributionViewModel> ContributingSources { get; }

    public int SourceCount => ContributingSources.Count;

    public string SourceCountText => SourceCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SampleValue { get; }
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
