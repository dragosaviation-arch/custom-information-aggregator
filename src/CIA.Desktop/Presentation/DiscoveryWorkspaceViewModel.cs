using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Profiles;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Profiles;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class DiscoveryWorkspaceViewModel : ObservableObject, IDisposable
{
    internal const string SourceSetColumnKey = "discovery.sourceSet";
    internal const string TagColumnKey = "discovery.tag";
    internal const string DatabaseTagColumnKey = "discovery.databaseTag";
    internal const string OccurrencesColumnKey = "discovery.occurrences";
    internal const string SourcesColumnKey = "discovery.sources";
    internal const string SampleColumnKey = "discovery.sample";
    internal const string BlacklistColumnKey = "discovery.blacklist";
    private const double MaximumColumnWidth = 2000;
    private readonly IDiscoveryClient _discoveryClient;
    private readonly ActiveDiscoveryConfiguration _activeConfiguration;
    private readonly ActiveLoadedSourceSet _sourceSet;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly DatabaseBuildCoordinator? _databaseBuildCoordinator;
    private readonly GlobalOperationProgress? _globalProgress;
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
    private readonly RelayCommand _refreshProfilesCommand;
    private readonly RelayCommand _loadProfileCommand;
    private readonly RelayCommand _saveNewProfileCommand;
    private readonly RelayCommand _updateProfileCommand;
    private readonly AsyncRelayCommand _deleteProfileCommand;
    private readonly RelayCommand _confirmDeleteProfileCommand;
    private readonly RelayCommand _cancelDeleteProfileCommand;
    private readonly IInformationSelectionProfileCoordinator? _profileCoordinator;
    private readonly IBlacklistProfileCoordinator? _blacklistProfileCoordinator;
    private readonly ReusableProfileSessionState _reusableProfileSessionState;
    private readonly IProfileDeleteConfirmation _profileDeleteConfirmation;
    private readonly MainWindowViewModel? _shell;
    private readonly ApplicationSettingsService? _settingsService;
    private readonly RelayCommand _refreshBlacklistProfilesCommand;
    private readonly RelayCommand _loadBlacklistProfileCommand;
    private readonly RelayCommand _saveNewBlacklistProfileCommand;
    private readonly RelayCommand _updateBlacklistProfileCommand;
    private readonly RelayCommand _cloneBlacklistProfileCommand;
    private readonly AsyncRelayCommand _deleteBlacklistProfileCommand;
    private readonly RelayCommand _setDefaultBlacklistProfileCommand;
    private readonly RelayCommand _clearDefaultBlacklistProfileCommand;
    private readonly SemaphoreSlim _previewRequestGate = new(1, 1);
    private readonly object _previewRequestStateGate = new();
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
    private int _progressCompletedSourceCount;
    private int _progressTotalSourceCount = 1;
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
    private CancellationTokenSource _previewRequestCancellation = new();
    private IReadOnlyList<InformationSelectionProfileItem> _informationSelectionProfiles = [];
    private InformationSelectionProfileItem? _selectedInformationSelectionProfile;
    private string _newProfileName = string.Empty;
    private string _profileStatusText = "Refresh to view information-selection profiles.";
    private IReadOnlyList<BlacklistProfileItem> _blacklistProfiles = [];
    private BlacklistProfileItem? _selectedBlacklistProfile;
    private string _newBlacklistProfileName = string.Empty;
    private string _blacklistProfileStatusText = "Refresh to view blacklist profiles.";
    private int _disposed;
    private bool _openDatabaseWhenCreationCompletes;
    private double _sourceSetColumnWidth;
    private double _tagColumnWidth;
    private double _databaseTagColumnWidth;
    private double _occurrencesColumnWidth;
    private double _sourcesColumnWidth;
    private double _sampleColumnWidth;
    private double _blacklistColumnWidth;
    private Guid? _discoveryProgressUpdateId;

    public DiscoveryWorkspaceViewModel(
        IDiscoveryClient discoveryClient,
        ActiveDiscoveryConfiguration activeConfiguration,
        ActiveLoadedSourceSet sourceSet,
        IApplicationWorkflowCoordinator workflowCoordinator,
        DatabaseBuildCoordinator? databaseBuildCoordinator = null,
        IInformationSelectionProfileCoordinator? profileCoordinator = null,
        IProfileDeleteConfirmation? profileDeleteConfirmation = null,
        IBlacklistProfileCoordinator? blacklistProfileCoordinator = null,
        ReusableProfileSessionState? reusableProfileSessionState = null,
        MainWindowViewModel? shell = null,
        ApplicationSettingsService? settingsService = null,
        GlobalOperationProgress? globalProgress = null)
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
        _globalProgress = globalProgress;
        _profileCoordinator = profileCoordinator;
        _blacklistProfileCoordinator = blacklistProfileCoordinator;
        _reusableProfileSessionState = reusableProfileSessionState
            ?? new ReusableProfileSessionState();
        _profileDeleteConfirmation = profileDeleteConfirmation
            ?? new InApplicationProfileDeleteConfirmation();
        _shell = shell;
        _settingsService = settingsService;
        _openDatabaseWhenCreationCompletes =
            settingsService?.Current.OpenDatabaseWhenCreationCompletes == true;
        _sourceSetColumnWidth = ResolveColumnWidth(SourceSetColumnKey, 100, 60);
        _tagColumnWidth = ResolveColumnWidth(TagColumnKey, 110, 55);
        _databaseTagColumnWidth = ResolveColumnWidth(DatabaseTagColumnKey, 120, 64);
        _occurrencesColumnWidth = ResolveColumnWidth(OccurrencesColumnKey, 90, 52);
        _sourcesColumnWidth = ResolveColumnWidth(SourcesColumnKey, 64, 44);
        _sampleColumnWidth = ResolveColumnWidth(SampleColumnKey, 170, 72);
        _blacklistColumnWidth = ResolveColumnWidth(BlacklistColumnKey, 104, 88);
        _profileDeleteConfirmation.Changed += OnProfileDeleteConfirmationChanged;
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
        _refreshProfilesCommand = new RelayCommand(
            RefreshProfiles,
            () => _profileCoordinator is not null);
        _loadProfileCommand = new RelayCommand(LoadSelectedProfile, CanLoadSelectedProfile);
        _saveNewProfileCommand = new RelayCommand(SaveNewProfile, CanSaveNewProfile);
        _updateProfileCommand = new RelayCommand(UpdateSelectedProfile, CanUpdateSelectedProfile);
        _deleteProfileCommand = new AsyncRelayCommand(
            DeleteSelectedProfileAsync,
            CanDeleteSelectedProfile);
        _confirmDeleteProfileCommand = new RelayCommand(
            _profileDeleteConfirmation.Accept,
            () => _profileDeleteConfirmation.IsOpen);
        _cancelDeleteProfileCommand = new RelayCommand(
            _profileDeleteConfirmation.Decline,
            () => _profileDeleteConfirmation.IsOpen);
        _refreshBlacklistProfilesCommand = new RelayCommand(
            RefreshBlacklistProfiles,
            () => _blacklistProfileCoordinator is not null);
        _loadBlacklistProfileCommand = new RelayCommand(
            LoadSelectedBlacklistProfile,
            CanLoadSelectedBlacklistProfile);
        _saveNewBlacklistProfileCommand = new RelayCommand(
            SaveNewBlacklistProfile,
            CanSaveNewBlacklistProfile);
        _updateBlacklistProfileCommand = new RelayCommand(
            UpdateSelectedBlacklistProfile,
            CanUpdateSelectedBlacklistProfile);
        _cloneBlacklistProfileCommand = new RelayCommand(
            CloneSelectedBlacklistProfile,
            CanCloneSelectedBlacklistProfile);
        _deleteBlacklistProfileCommand = new AsyncRelayCommand(
            DeleteSelectedBlacklistProfileAsync,
            CanDeleteSelectedBlacklistProfile);
        _setDefaultBlacklistProfileCommand = new RelayCommand(
            SetDefaultBlacklistProfile,
            CanSetDefaultBlacklistProfile);
        _clearDefaultBlacklistProfileCommand = new RelayCommand(
            ClearDefaultBlacklistProfile,
            CanClearDefaultBlacklistProfile);
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
        if (_profileCoordinator is not null)
        {
            RefreshProfiles();
        }
        if (_blacklistProfileCoordinator is not null)
        {
            RefreshBlacklistProfiles();
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

    public string DatabaseBuildAvailabilityReason
    {
        get
        {
            if (_databaseBuildCoordinator is null)
            {
                return "Database creation is unavailable.";
            }

            if (_workflowCoordinator.Current.ActiveOperation is not null || IsBusy)
            {
                return "Wait for the active operation to finish before creating the Database.";
            }

            if (_workflowCoordinator.Current.Discovery != WorkflowArtifactStatus.Current)
            {
                return "Run Discovery successfully before creating the Database.";
            }

            if (_sourceSet.CreateIncludedReadySnapshot().Count == 0)
            {
                return "Include at least one ready source before creating the Database.";
            }

            return _databaseBuildCoordinator.CanBuild()
                ? "Create a hierarchy-aware Database from the current selected Discovery mapping."
                : "Select at least one discovered field from a ready Source Set before creating the Database.";
        }
    }

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

    public IRelayCommand RefreshProfilesCommand => _refreshProfilesCommand;

    public IRelayCommand LoadProfileCommand => _loadProfileCommand;

    public IRelayCommand SaveNewProfileCommand => _saveNewProfileCommand;

    public IRelayCommand UpdateProfileCommand => _updateProfileCommand;

    public IAsyncRelayCommand DeleteProfileCommand => _deleteProfileCommand;

    public IRelayCommand ConfirmDeleteProfileCommand => _confirmDeleteProfileCommand;

    public IRelayCommand CancelDeleteProfileCommand => _cancelDeleteProfileCommand;

    public IRelayCommand RefreshBlacklistProfilesCommand => _refreshBlacklistProfilesCommand;

    public IRelayCommand LoadBlacklistProfileCommand => _loadBlacklistProfileCommand;

    public IRelayCommand SaveNewBlacklistProfileCommand => _saveNewBlacklistProfileCommand;

    public IRelayCommand UpdateBlacklistProfileCommand => _updateBlacklistProfileCommand;

    public IRelayCommand CloneBlacklistProfileCommand => _cloneBlacklistProfileCommand;

    public IAsyncRelayCommand DeleteBlacklistProfileCommand => _deleteBlacklistProfileCommand;

    public IRelayCommand SetDefaultBlacklistProfileCommand =>
        _setDefaultBlacklistProfileCommand;

    public IRelayCommand ClearDefaultBlacklistProfileCommand =>
        _clearDefaultBlacklistProfileCommand;

    public IRelayCommand CloseSourceInspectionCommand { get; }

    public IReadOnlyList<InformationSelectionProfileItem> InformationSelectionProfiles
    {
        get => _informationSelectionProfiles;
        private set => SetProperty(ref _informationSelectionProfiles, value);
    }

    public InformationSelectionProfileItem? SelectedInformationSelectionProfile
    {
        get => _selectedInformationSelectionProfile;
        set
        {
            if (SetProperty(ref _selectedInformationSelectionProfile, value))
            {
                NotifyProfileCommandsChanged();
            }
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (SetProperty(ref _newProfileName, value))
            {
                _saveNewProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ProfileStatusText
    {
        get => _profileStatusText;
        private set => SetProperty(ref _profileStatusText, value);
    }

    public string ProfileCaptureAvailabilityReason =>
        _profileCoordinator?.CaptureReadinessReason
        ?? "Information-selection profile management is unavailable.";

    public IReadOnlyList<BlacklistProfileItem> BlacklistProfiles
    {
        get => _blacklistProfiles;
        private set => SetProperty(ref _blacklistProfiles, value);
    }

    public BlacklistProfileItem? SelectedBlacklistProfile
    {
        get => _selectedBlacklistProfile;
        set
        {
            if (SetProperty(ref _selectedBlacklistProfile, value))
            {
                NotifyBlacklistProfileCommandsChanged();
            }
        }
    }

    public string NewBlacklistProfileName
    {
        get => _newBlacklistProfileName;
        set
        {
            if (SetProperty(ref _newBlacklistProfileName, value))
            {
                NotifyBlacklistProfileCommandsChanged();
            }
        }
    }

    public string BlacklistProfileStatusText
    {
        get => _blacklistProfileStatusText;
        private set => SetProperty(ref _blacklistProfileStatusText, value);
    }

    public string BlacklistCaptureAvailabilityReason =>
        _blacklistProfileCoordinator?.CaptureReadinessReason
        ?? "Blacklist profile management is unavailable.";

    public string DefaultBlacklistProfileText =>
        _blacklistProfileCoordinator?.Inventory.DefaultProfileId is { } defaultProfileId
            ? BlacklistProfiles.FirstOrDefault(profile => profile.ProfileId == defaultProfileId)
                is { } profile
                    ? $"Default: {profile.Name}"
                    : "Default profile is configured but unavailable."
            : _blacklistProfileCoordinator?.Inventory.DefaultDesignationProblem is { } problem
                ? $"Default unavailable: {problem}"
                : "Default: None";

    public bool IsDeleteProfileConfirmationOpen => _profileDeleteConfirmation.IsOpen;

    public string DeleteProfileConfirmationMessage =>
        _profileDeleteConfirmation.ProfileName is { } name
            ? $"Delete profile '{name}'?"
            : "Delete the selected profile?";

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
                OnPropertyChanged(nameof(DatabaseBuildAvailabilityReason));
                RunDiscoveryCommand.NotifyCanExecuteChanged();
                BuildDatabaseCommand.NotifyCanExecuteChanged();
                NotifyConfigurationCommandsChanged();
                NotifyProfileCommandsChanged();
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
                NotifyProfileCommandsChanged();
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
        _allInformation.Count(information => information.IsSelected),
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

    public bool IsProgressIndeterminate => false;

    public double ProgressMaximum => Math.Max(1, _progressTotalSourceCount);

    public double ProgressValue => _progressCompletedSourceCount;

    public string ProgressPercentText => string.Format(
        CultureInfo.CurrentCulture,
        "{0:0}%",
        Math.Clamp(ProgressValue / ProgressMaximum, 0, 1) * 100);

    public bool OpenDatabaseWhenCreationCompletes
    {
        get => _openDatabaseWhenCreationCompletes;
        set
        {
            if (SetProperty(ref _openDatabaseWhenCreationCompletes, value)
                && _settingsService is not null)
            {
                _settingsService.Save(_settingsService.Current with
                {
                    OpenDatabaseWhenCreationCompletes = value
                });
            }
        }
    }

    public double SourceSetColumnWidth
    {
        get => _sourceSetColumnWidth;
        private set => SetProperty(ref _sourceSetColumnWidth, value);
    }

    public double TagColumnWidth
    {
        get => _tagColumnWidth;
        private set => SetProperty(ref _tagColumnWidth, value);
    }

    public double DatabaseTagColumnWidth
    {
        get => _databaseTagColumnWidth;
        private set => SetProperty(ref _databaseTagColumnWidth, value);
    }

    public double OccurrencesColumnWidth
    {
        get => _occurrencesColumnWidth;
        private set => SetProperty(ref _occurrencesColumnWidth, value);
    }

    public double SourcesColumnWidth
    {
        get => _sourcesColumnWidth;
        private set => SetProperty(ref _sourcesColumnWidth, value);
    }

    public double SampleColumnWidth
    {
        get => _sampleColumnWidth;
        private set => SetProperty(ref _sampleColumnWidth, value);
    }

    public double BlacklistColumnWidth
    {
        get => _blacklistColumnWidth;
        private set => SetProperty(ref _blacklistColumnWidth, value);
    }

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

    internal void ResizeColumns(string leftKey, string rightKey, double horizontalChange)
    {
        var leftWidth = GetColumnWidth(leftKey);
        var rightWidth = GetColumnWidth(rightKey);
        var appliedChange = ColumnWidthPreferences.ApplyAdjacentDelta(
            leftWidth,
            rightWidth,
            horizontalChange,
            GetColumnMinimum(leftKey),
            GetColumnMinimum(rightKey),
            MaximumColumnWidth);
        SetColumnWidth(leftKey, leftWidth + appliedChange);
        SetColumnWidth(rightKey, rightWidth - appliedChange);
    }

    internal void PersistColumnWidths(string leftKey, string rightKey)
    {
        ColumnWidthPreferences.Save(
            _settingsService,
            (leftKey, GetColumnWidth(leftKey)),
            (rightKey, GetColumnWidth(rightKey)));
    }

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
        _profileDeleteConfirmation.Changed -= OnProfileDeleteConfirmationChanged;
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

        lock (_previewRequestStateGate)
        {
            Interlocked.Increment(ref _previewRequestVersion);
            _previewRequestCancellation.Cancel();
        }
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
        if (_databaseBuildCoordinator is null)
        {
            return;
        }

        StatusTitle = "Creating Database";
        StatusDetail = "Building hierarchy-aware Source Set datasets in the Processing Host.";
        var progressUpdateId = _globalProgress?.Begin(
            "Database build",
            "Preparing Database build");
        var buildProgress = progressUpdateId is { } updateId
            ? new InlineProgress<DatabaseBuildProgressSnapshot>(snapshot => DispatchToUi(() =>
                _globalProgress?.Report(
                    updateId,
                    snapshot.Stage,
                    snapshot.CompletedWorkCount,
                    snapshot.TotalWorkCount)))
            : null;
        var result = await _databaseBuildCoordinator.BuildAsync(progress: buildProgress);
        if (result.Accepted)
        {
            if (progressUpdateId is { } completedUpdateId)
            {
                _globalProgress?.Complete(
                    completedUpdateId,
                    _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.CompletedWithIssues
                        ? GlobalOperationProgressState.CompletedWithIssues
                        : GlobalOperationProgressState.CompletedSuccessfully,
                    _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.CompletedWithIssues
                        ? "completed with issues"
                        : "completed successfully");
            }
            StatusTitle = "Database created";
            StatusDetail = "The published Database is current and available in the Database workspace.";
            NavigateToWorkspaceIfConfigured(
                OpenDatabaseWhenCreationCompletes,
                WorkspaceArea.Database);
            return;
        }

        if (progressUpdateId is { } failedUpdateId)
        {
            var terminalState = _workflowCoordinator.Current.LatestOperation?.State
                == WorkflowOperationState.Cancelled
                ? GlobalOperationProgressState.Cancelled
                : GlobalOperationProgressState.Failed;
            _globalProgress?.Complete(
                failedUpdateId,
                terminalState,
                terminalState == GlobalOperationProgressState.Cancelled
                    ? "cancelled"
                    : "failed");
        }
        StatusTitle = "Database creation failed";
        StatusDetail = result.Rejection?.Reason
            ?? "The Database could not be created from the current Discovery configuration.";
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
        _progressCompletedSourceCount = 0;
        _progressTotalSourceCount = sources.Count;
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
            _discoveryProgressUpdateId = _globalProgress?.Begin(
                "Discovery",
                $"Processing source 0 of {sources.Count:N0}",
                0,
                sources.Count);
            var progress = new InlineProgress<DiscoveryProgressSnapshot>(snapshot =>
                DispatchToUi(() => ApplyDiscoveryProgress(snapshot)));
            var result = await _discoveryClient.RunAsync(operation, sources, progress);
            var completion = _workflowCoordinator.CompleteOperation(
                result.Completion,
                result.FailureCode,
                result.FailureDescription,
                result.FailureTechnicalDetail);
            SynchronizeDiscoveryStatus();

            if (!result.Accepted || !completion.Accepted)
            {
                _issueCount = result.Issues.Count;
                _progressStage = "Stage: Failed";
                PresentFailure(
                    result.FailureDescription
                    ?? completion.Rejection?.Reason
                    ?? "Discovery did not produce a usable result.");
                CompleteDiscoveryGlobalProgress(
                    GlobalOperationProgressState.Failed,
                    "failed");
                return;
            }

            _activeConfiguration.Synchronize(
                result.Information.Select(information => information.Identity));
            _activeConfiguration.SynchronizeSourceSets(
                _sourceSet.SourceSets.Select(sourceSet => sourceSet.SourceSetId)
                    .Concat(result.Information.Select(information => information.SourceSetId)));
            _reusableProfileSessionState.ApplyTo(_activeConfiguration);
            var dispositions = _activeConfiguration.Current.Items.ToDictionary(
                item => item.Identity,
                item => item.Disposition);
            var databaseTagOverrides = _activeConfiguration.DatabaseTagOverridesByIdentity;
            _allInformation = DiscoveredInformationItemViewModel
                .CreateLogicalItems(
                    result.Information,
                    ResolveSourceSetName,
                    identity => dispositions[identity],
                    identity => databaseTagOverrides.GetValueOrDefault(identity))
                .ToArray();
            RefreshSourceSetLayouts();
            _publishedDiscoveryOperationId = result.Completion.Correlation.OperationId;
            _publishedDiscoverySources = CreatePublishedSourceSnapshot(
                sources,
                result.Information);
            _issueCount = result.Issues.Count;
            _progressStage = "Stage: Complete";
            _progressCompletedSourceCount = _progressTotalSourceCount;
            _hasCompletedDiscovery = true;
            _currentPage = 1;
            RefreshPresentation();
            SelectedInformation = Information.FirstOrDefault();
            StatusTitle = result.Issues.Count == 0
                ? "Discovery complete"
                : "Discovery complete with issues";
            StatusDetail = "Current result available for the active source set.";
            CompleteDiscoveryGlobalProgress(
                result.Issues.Count == 0
                    ? GlobalOperationProgressState.CompletedSuccessfully
                    : GlobalOperationProgressState.CompletedWithIssues,
                result.Issues.Count == 0
                    ? "completed successfully"
                    : "completed with issues");
            OnPropertyChanged(nameof(RunButtonText));
            NavigateToWorkspaceIfConfigured(
                _settingsService?.Current.OpenDiscoveryWhenGenerationCompletes == true,
                WorkspaceArea.Discovery);
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
            CompleteDiscoveryGlobalProgress(
                GlobalOperationProgressState.Cancelled,
                "cancelled");
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
            CompleteDiscoveryGlobalProgress(
                GlobalOperationProgressState.Failed,
                "failed");
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
        var request = CapturePreviewRequest();
        if (information is null
            || _publishedDiscoveryOperationId is null
            || !ReferenceEquals(SelectedInformation, information))
        {
            return;
        }

        await LoadOccurrenceAsync(
            information,
            ordinal: 1,
            request.Version,
            request.CancellationToken);
    }

    private void PrepareOccurrenceSelection(DiscoveredInformationItemViewModel? information)
    {
        SupersedePreviewRequest();
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

        var request = SupersedePreviewRequest();
        return LoadOccurrenceAsync(
            information,
            ordinal,
            request.Version,
            request.CancellationToken);
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
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var enteredRequestGate = false;

        try
        {
            await _previewRequestGate.WaitAsync(cancellationToken);
            enteredRequestGate = true;

            if (!IsCurrentPreviewRequest(requestVersion, information))
            {
                return;
            }

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
                if (CurrentOccurrenceOrdinal == 0)
                {
                    OccurrencePreviewText = "The selected occurrence could not be retrieved.";
                }

                RestoreOccurrenceOrdinalInput();
                return;
            }

            IsOccurrenceLoading = true;

            // Once a framed IPC request is sent, its response must be drained before another
            // request uses the shared connection. Supersession therefore cancels queued reads
            // and ignores an obsolete active result rather than abandoning the response frame.
            var result = await _discoveryClient.GetOccurrenceAsync(lookup);

            if (!IsCurrentPreviewRequest(requestVersion, information))
            {
                return;
            }

            var occurrence = result.Occurrence;
            if (!result.Accepted
                || occurrence is null
                || occurrence.Identity != lookup.Identity
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            if (enteredRequestGate)
            {
                _previewRequestGate.Release();
            }

            if (requestVersion == Volatile.Read(ref _previewRequestVersion))
            {
                IsOccurrenceLoading = false;
            }
        }
    }

    private PreviewRequest SupersedePreviewRequest()
    {
        lock (_previewRequestStateGate)
        {
            _previewRequestCancellation.Cancel();
            _previewRequestCancellation.Dispose();
            _previewRequestCancellation = new CancellationTokenSource();
            return new PreviewRequest(
                Interlocked.Increment(ref _previewRequestVersion),
                _previewRequestCancellation.Token);
        }
    }

    private PreviewRequest CapturePreviewRequest()
    {
        lock (_previewRequestStateGate)
        {
            return new PreviewRequest(
                Volatile.Read(ref _previewRequestVersion),
                _previewRequestCancellation.Token);
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
        foreach (var member in information.DetailedInformation)
        {
            foreach (var contribution in member.ContributingSources)
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
                        member.Identity,
                        globalOrdinal,
                        information.TotalOccurrenceCount,
                        source,
                        localOrdinal,
                        contribution.OccurrenceCount);
                    return true;
                }

                break;
            }
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

    private bool CanLoadSelectedProfile()
    {
        return !IsBusy
            && SelectedInformationSelectionProfile is not null
            && _profileCoordinator?.CanLoadCurrentConfiguration == true;
    }

    private bool CanSaveNewProfile()
    {
        var name = NewProfileName.Trim();
        return !IsBusy
            && name.Length is > 0 and <= ProfileArtifactValidator.MaximumProfileNameLength
            && _profileCoordinator?.CanCaptureCurrentConfiguration == true;
    }

    private bool CanUpdateSelectedProfile()
    {
        return !IsBusy
            && SelectedInformationSelectionProfile is not null
            && _profileCoordinator?.CanCaptureCurrentConfiguration == true;
    }

    private bool CanDeleteSelectedProfile()
    {
        return _profileCoordinator is not null
            && SelectedInformationSelectionProfile is not null
            && !_profileDeleteConfirmation.IsOpen;
    }

    private bool CanLoadSelectedBlacklistProfile()
    {
        return !IsBusy
            && SelectedBlacklistProfile is not null
            && _blacklistProfileCoordinator?.CanLoadCurrentConfiguration == true;
    }

    private bool CanSaveNewBlacklistProfile()
    {
        var name = NewBlacklistProfileName.Trim();
        return !IsBusy
            && name.Length is > 0 and <= ProfileArtifactValidator.MaximumProfileNameLength
            && _blacklistProfileCoordinator?.CanCaptureCurrentConfiguration == true;
    }

    private bool CanUpdateSelectedBlacklistProfile()
    {
        return !IsBusy
            && SelectedBlacklistProfile is not null
            && _blacklistProfileCoordinator?.CanCaptureCurrentConfiguration == true;
    }

    private bool CanCloneSelectedBlacklistProfile()
    {
        var name = NewBlacklistProfileName.Trim();
        return SelectedBlacklistProfile is not null
            && name.Length is > 0 and <= ProfileArtifactValidator.MaximumProfileNameLength;
    }

    private bool CanDeleteSelectedBlacklistProfile()
    {
        return _blacklistProfileCoordinator is not null
            && SelectedBlacklistProfile is not null
            && !_profileDeleteConfirmation.IsOpen;
    }

    private bool CanSetDefaultBlacklistProfile()
    {
        return _blacklistProfileCoordinator is not null
            && SelectedBlacklistProfile is not null;
    }

    private bool CanClearDefaultBlacklistProfile()
    {
        return _blacklistProfileCoordinator?.Inventory.DefaultProfileId is not null;
    }

    private void RefreshProfiles()
    {
        if (_profileCoordinator is null)
        {
            return;
        }

        var previous = SelectedInformationSelectionProfile;
        var inventory = _profileCoordinator.RefreshInventory();
        InformationSelectionProfiles = inventory.Profiles;
        SelectedInformationSelectionProfile = previous is null
            ? null
            : inventory.Profiles.FirstOrDefault(profile =>
                profile.ProfileId == previous.ProfileId
                && profile.Fingerprint == previous.Fingerprint);
        ProfileStatusText = inventory.Problems.Count == 0
            ? $"{inventory.Profiles.Count:N0} information-selection profiles available."
            : $"{inventory.Profiles.Count:N0} profiles available; {inventory.Problems.Count:N0} profile artifacts unavailable.";
        NotifyProfileCommandsChanged();
    }

    private void LoadSelectedProfile()
    {
        if (_profileCoordinator is null || SelectedInformationSelectionProfile is null)
        {
            return;
        }

        var result = _profileCoordinator.Load(SelectedInformationSelectionProfile);
        ProfileStatusText = result.Message;
        if (result.RequiresReselection)
        {
            InformationSelectionProfiles = _profileCoordinator.Inventory.Profiles;
            SelectedInformationSelectionProfile = null;
        }

        if (result.Succeeded && result.ChangedCount > 0)
        {
            ApplyActiveConfiguration();
        }

        NotifyProfileCommandsChanged();
    }

    private void SaveNewProfile()
    {
        if (_profileCoordinator is null)
        {
            return;
        }

        var result = _profileCoordinator.SaveNew(NewProfileName.Trim());
        ProfileStatusText = result.Message;
        InformationSelectionProfiles = _profileCoordinator.Inventory.Profiles;
        if (result.Succeeded)
        {
            SelectedInformationSelectionProfile = result.Profile;
            NewProfileName = string.Empty;
        }

        NotifyProfileCommandsChanged();
    }

    private void UpdateSelectedProfile()
    {
        if (_profileCoordinator is null || SelectedInformationSelectionProfile is null)
        {
            return;
        }

        var result = _profileCoordinator.Update(SelectedInformationSelectionProfile);
        ProfileStatusText = result.Message;
        InformationSelectionProfiles = _profileCoordinator.Inventory.Profiles;
        SelectedInformationSelectionProfile = result.RequiresReselection
            ? null
            : result.Profile ?? SelectedInformationSelectionProfile;
        NotifyProfileCommandsChanged();
    }

    private async Task DeleteSelectedProfileAsync()
    {
        if (_profileCoordinator is null || SelectedInformationSelectionProfile is null)
        {
            return;
        }

        var selectedProfile = SelectedInformationSelectionProfile;
        if (!await _profileDeleteConfirmation.ConfirmAsync(selectedProfile.Name))
        {
            ProfileStatusText = "Profile deletion cancelled.";
            return;
        }

        var result = _profileCoordinator.Delete(selectedProfile);
        ProfileStatusText = result.Message;
        InformationSelectionProfiles = _profileCoordinator.Inventory.Profiles;
        if (result.Succeeded || result.RequiresReselection)
        {
            SelectedInformationSelectionProfile = null;
        }

        NotifyProfileCommandsChanged();
    }

    private void RefreshBlacklistProfiles()
    {
        if (_blacklistProfileCoordinator is null)
        {
            return;
        }

        var previous = SelectedBlacklistProfile;
        var inventory = _blacklistProfileCoordinator.RefreshInventory();
        BlacklistProfiles = inventory.Profiles;
        SelectedBlacklistProfile = previous is null
            ? null
            : inventory.Profiles.FirstOrDefault(profile =>
                profile.ProfileId == previous.ProfileId
                && profile.Fingerprint == previous.Fingerprint);
        var problemCount = inventory.Problems.Count
            + (inventory.DefaultDesignationProblem is null ? 0 : 1);
        BlacklistProfileStatusText = problemCount == 0
            ? $"{inventory.Profiles.Count:N0} blacklist profiles available."
            : $"{inventory.Profiles.Count:N0} blacklist profiles available; {problemCount:N0} profile artifacts unavailable.";
        OnPropertyChanged(nameof(DefaultBlacklistProfileText));
        NotifyBlacklistProfileCommandsChanged();
    }

    private void LoadSelectedBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null || SelectedBlacklistProfile is null)
        {
            return;
        }

        var result = _blacklistProfileCoordinator.Load(SelectedBlacklistProfile);
        BlacklistProfileStatusText = result.Message;
        if (result.RequiresReselection)
        {
            BlacklistProfiles = _blacklistProfileCoordinator.Inventory.Profiles;
            SelectedBlacklistProfile = null;
        }

        if (result.Succeeded && result.ChangedCount > 0)
        {
            ApplyActiveConfiguration();
        }

        NotifyBlacklistProfileCommandsChanged();
    }

    private void SaveNewBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null)
        {
            return;
        }

        var result = _blacklistProfileCoordinator.SaveNew(NewBlacklistProfileName.Trim());
        ApplyBlacklistProfileResult(result, clearNameOnSuccess: true);
    }

    private void UpdateSelectedBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null || SelectedBlacklistProfile is null)
        {
            return;
        }

        var result = _blacklistProfileCoordinator.Update(SelectedBlacklistProfile);
        ApplyBlacklistProfileResult(result, clearNameOnSuccess: false);
    }

    private void CloneSelectedBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null || SelectedBlacklistProfile is null)
        {
            return;
        }

        var result = _blacklistProfileCoordinator.Clone(
            SelectedBlacklistProfile,
            NewBlacklistProfileName.Trim());
        ApplyBlacklistProfileResult(result, clearNameOnSuccess: true);
    }

    private async Task DeleteSelectedBlacklistProfileAsync()
    {
        if (_blacklistProfileCoordinator is null || SelectedBlacklistProfile is null)
        {
            return;
        }

        var selectedProfile = SelectedBlacklistProfile;
        if (!await _profileDeleteConfirmation.ConfirmAsync(selectedProfile.Name))
        {
            BlacklistProfileStatusText = "Profile deletion cancelled.";
            return;
        }

        var result = _blacklistProfileCoordinator.Delete(selectedProfile);
        BlacklistProfileStatusText = result.Message;
        BlacklistProfiles = _blacklistProfileCoordinator.Inventory.Profiles;
        if (result.Succeeded || result.RequiresReselection)
        {
            SelectedBlacklistProfile = null;
        }

        OnPropertyChanged(nameof(DefaultBlacklistProfileText));
        NotifyBlacklistProfileCommandsChanged();
    }

    private void SetDefaultBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null || SelectedBlacklistProfile is null)
        {
            return;
        }

        ApplyBlacklistProfileResult(
            _blacklistProfileCoordinator.SetDefault(SelectedBlacklistProfile),
            clearNameOnSuccess: false);
    }

    private void ClearDefaultBlacklistProfile()
    {
        if (_blacklistProfileCoordinator is null)
        {
            return;
        }

        ApplyBlacklistProfileResult(
            _blacklistProfileCoordinator.ClearDefault(),
            clearNameOnSuccess: false);
    }

    private void ApplyBlacklistProfileResult(
        BlacklistProfileOperationResult result,
        bool clearNameOnSuccess)
    {
        if (_blacklistProfileCoordinator is null)
        {
            return;
        }

        BlacklistProfileStatusText = result.Message;
        var previousSelection = SelectedBlacklistProfile;
        BlacklistProfiles = _blacklistProfileCoordinator.Inventory.Profiles;
        SelectedBlacklistProfile = result.RequiresReselection
            ? null
            : result.Profile
                ?? (previousSelection is null
                    ? null
                    : BlacklistProfiles.FirstOrDefault(profile =>
                        profile.ProfileId == previousSelection.ProfileId
                        && profile.Fingerprint == previousSelection.Fingerprint));
        if (result.Succeeded && clearNameOnSuccess)
        {
            NewBlacklistProfileName = string.Empty;
        }

        OnPropertyChanged(nameof(DefaultBlacklistProfileText));
        NotifyBlacklistProfileCommandsChanged();
    }

    private void ToggleSelection(DiscoveredInformationItemViewModel? information)
    {
        if (information is null || information.IsBlacklisted)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetSelection(
                information.DetailedIdentities,
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
                information.DetailedIdentities,
                !information.IsBlacklisted));
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
        var currentOverrides = _activeConfiguration.DatabaseTagOverridesByIdentity;
        if (information.DetailedIdentities.All(identity => next is null
                ? !currentOverrides.ContainsKey(identity)
                : string.Equals(
                    currentOverrides.GetValueOrDefault(identity),
                    next,
                    StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(SelectedDatabaseTag));
            return;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted
            || _activeConfiguration.SetDatabaseTagOverride(
                information.DetailedIdentities,
                next) == 0)
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
            .SelectMany(information => information.DetailedIdentities)
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
            information.ApplyDisposition(ResolveLogicalDisposition(
                information.DetailedIdentities.Select(identity => dispositions[identity])));
            information.ApplyDatabaseTagOverride(ResolveLogicalDatabaseTagOverride(
                information.DetailedIdentities.Select(identity =>
                    databaseTagOverrides.GetValueOrDefault(identity))));
        }

        OnPropertyChanged(nameof(SelectedDatabaseTag));
        OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        RefreshPresentation();
        NotifyProfileCommandsChanged();
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

    private static DiscoveryInformationDisposition ResolveLogicalDisposition(
        IEnumerable<DiscoveryInformationDisposition> dispositions)
    {
        var values = dispositions.Distinct().ToArray();
        return values.Length == 1
            ? values[0]
            : DiscoveryInformationDisposition.Neutral;
    }

    private static string? ResolveLogicalDatabaseTagOverride(IEnumerable<string?> overrides)
    {
        var values = overrides.Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private void RefreshPresentation()
    {
        IEnumerable<DiscoveredInformationItemViewModel> query = _allInformation;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(information =>
                information.InformationType.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || information.DatabaseTag.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
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
        BuildDatabaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DatabaseBuildAvailabilityReason));
        NotifyConfigurationCommandsChanged();
    }

    private IEnumerable<DiscoveredInformationItemViewModel> ApplySort(
        IEnumerable<DiscoveredInformationItemViewModel> information)
    {
        Func<DiscoveredInformationItemViewModel, object> keySelector = _sortColumn switch
        {
            "SourceSet" => item => item.SourceSetName,
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
                OnPropertyChanged(nameof(DatabaseBuildAvailabilityReason));
                NotifyConfigurationCommandsChanged();
                NotifyProfileCommandsChanged();
            });
    }

    private void OnProfileDeleteConfirmationChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(IsDeleteProfileConfirmationOpen));
            OnPropertyChanged(nameof(DeleteProfileConfirmationMessage));
            _confirmDeleteProfileCommand.NotifyCanExecuteChanged();
            _cancelDeleteProfileCommand.NotifyCanExecuteChanged();
            _deleteProfileCommand.NotifyCanExecuteChanged();
            _deleteBlacklistProfileCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnPublishedDatabaseGenerationChanged(
        object? sender,
        CIA.Contracts.Database.DatabaseGenerationSummary? generation)
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(DatabaseStateText));
            OnPropertyChanged(nameof(DatabaseBuildButtonText));
            OnPropertyChanged(nameof(DatabaseBuildAvailabilityReason));
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
        BuildDatabaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DatabaseBuildAvailabilityReason));
    }

    private void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(ProgressStage));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void ApplyDiscoveryProgress(DiscoveryProgressSnapshot progress)
    {
        if (!IsBusy
            || progress.TotalSourceCount != _progressTotalSourceCount
            || progress.CompletedSourceCount < _progressCompletedSourceCount)
        {
            return;
        }

        _progressCompletedSourceCount = progress.CompletedSourceCount;
        NotifyProgressChanged();
        if (_discoveryProgressUpdateId is { } updateId)
        {
            _globalProgress?.Report(
                updateId,
                $"Processing source {progress.CompletedSourceCount:N0} of {progress.TotalSourceCount:N0}",
                progress.CompletedSourceCount,
                progress.TotalSourceCount);
        }
    }

    private void CompleteDiscoveryGlobalProgress(
        GlobalOperationProgressState state,
        string terminalText)
    {
        if (_discoveryProgressUpdateId is not { } updateId)
        {
            return;
        }

        _globalProgress?.Complete(updateId, state, terminalText);
        _discoveryProgressUpdateId = null;
    }

    private void NavigateToWorkspaceIfConfigured(bool enabled, WorkspaceArea area)
    {
        if (!enabled || _shell is null)
        {
            return;
        }

        _shell.SelectedWorkspace = _shell.Workspaces.Single(workspace => workspace.Area == area);
    }

    private double ResolveColumnWidth(string key, double defaultWidth, double minimumWidth) =>
        ColumnWidthPreferences.Resolve(
            _settingsService,
            key,
            defaultWidth,
            minimumWidth,
            MaximumColumnWidth);

    private double GetColumnWidth(string key) => key switch
    {
        SourceSetColumnKey => SourceSetColumnWidth,
        TagColumnKey => TagColumnWidth,
        DatabaseTagColumnKey => DatabaseTagColumnWidth,
        OccurrencesColumnKey => OccurrencesColumnWidth,
        SourcesColumnKey => SourcesColumnWidth,
        SampleColumnKey => SampleColumnWidth,
        BlacklistColumnKey => BlacklistColumnWidth,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static double GetColumnMinimum(string key) => key switch
    {
        SourceSetColumnKey => 60,
        TagColumnKey => 55,
        DatabaseTagColumnKey => 64,
        OccurrencesColumnKey => 52,
        SourcesColumnKey => 44,
        SampleColumnKey => 72,
        BlacklistColumnKey => 88,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private void SetColumnWidth(string key, double width)
    {
        switch (key)
        {
            case SourceSetColumnKey:
                SourceSetColumnWidth = width;
                break;
            case TagColumnKey:
                TagColumnWidth = width;
                break;
            case DatabaseTagColumnKey:
                DatabaseTagColumnWidth = width;
                break;
            case OccurrencesColumnKey:
                OccurrencesColumnWidth = width;
                break;
            case SourcesColumnKey:
                SourcesColumnWidth = width;
                break;
            case SampleColumnKey:
                SampleColumnWidth = width;
                break;
            case BlacklistColumnKey:
                BlacklistColumnWidth = width;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    private void NotifyHeaderTextChanged()
    {
        OnPropertyChanged(nameof(SourceSetHeaderText));
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

    private void NotifyProfileCommandsChanged()
    {
        OnPropertyChanged(nameof(ProfileCaptureAvailabilityReason));
        _loadProfileCommand.NotifyCanExecuteChanged();
        _saveNewProfileCommand.NotifyCanExecuteChanged();
        _updateProfileCommand.NotifyCanExecuteChanged();
        _deleteProfileCommand.NotifyCanExecuteChanged();
        NotifyBlacklistProfileCommandsChanged();
    }

    private void NotifyBlacklistProfileCommandsChanged()
    {
        OnPropertyChanged(nameof(BlacklistCaptureAvailabilityReason));
        OnPropertyChanged(nameof(DefaultBlacklistProfileText));
        _loadBlacklistProfileCommand.NotifyCanExecuteChanged();
        _saveNewBlacklistProfileCommand.NotifyCanExecuteChanged();
        _updateBlacklistProfileCommand.NotifyCanExecuteChanged();
        _cloneBlacklistProfileCommand.NotifyCanExecuteChanged();
        _deleteBlacklistProfileCommand.NotifyCanExecuteChanged();
        _setDefaultBlacklistProfileCommand.NotifyCanExecuteChanged();
        _clearDefaultBlacklistProfileCommand.NotifyCanExecuteChanged();
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private readonly record struct PreviewRequest(
        int Version,
        CancellationToken CancellationToken);
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
        : this([information], sourceSetName, disposition, databaseTagOverride)
    {
    }

    private DiscoveredInformationItemViewModel(
        IReadOnlyList<DiscoveredInformation> information,
        string sourceSetName,
        DiscoveryInformationDisposition disposition,
        string? databaseTagOverride)
    {
        ArgumentNullException.ThrowIfNull(information);
        if (information.Count == 0)
        {
            throw new ArgumentException(
                "A logical Discovery item requires at least one detailed identity.",
                nameof(information));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        DetailedInformation = information;
        DetailedIdentities = information.Select(item => item.Identity).ToArray();
        Identity = information[0].Identity;
        _sourceSetName = sourceSetName;
        var candidate = databaseTagOverride?.Trim();
        _databaseTagOverride = string.IsNullOrWhiteSpace(candidate)
            || string.Equals(
                candidate,
                Identity.InformationType,
                StringComparison.Ordinal)
                ? null
                : candidate;
        TotalOccurrenceCount = information.Sum(item => item.TotalOccurrenceCount);
        ContributingSources = CreateLogicalContributions(information);
        SampleValue = DiscoverySampleValueFormatter.Format(information[0].SampleValue);
        _disposition = disposition;
    }

    public static IEnumerable<DiscoveredInformationItemViewModel> CreateLogicalItems(
        IEnumerable<DiscoveredInformation> information,
        Func<SourceSetId, string> resolveSourceSetName,
        Func<DiscoveryInformationIdentity, DiscoveryInformationDisposition> resolveDisposition,
        Func<DiscoveryInformationIdentity, string?> resolveDatabaseTagOverride)
    {
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(resolveSourceSetName);
        ArgumentNullException.ThrowIfNull(resolveDisposition);
        ArgumentNullException.ThrowIfNull(resolveDatabaseTagOverride);

        return information
            .GroupBy(item => LogicalDiscoveryIdentity.Create(item.Identity))
            .Select(group => group
                .OrderBy(item => item.Identity.StructuralIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Identity.StructuralPath, StringComparer.Ordinal)
                .ThenBy(item => item.Identity.InformationType, StringComparer.Ordinal)
                .ToArray())
            .Select(group => new DiscoveredInformationItemViewModel(
                group,
                resolveSourceSetName(group[0].SourceSetId),
                ResolveLogicalDisposition(group.Select(item => resolveDisposition(item.Identity))),
                ResolveLogicalDatabaseTagOverride(
                    group.Select(item => resolveDatabaseTagOverride(item.Identity)))));
    }

    public DiscoveryInformationIdentity Identity { get; }

    public IReadOnlyList<DiscoveredInformation> DetailedInformation { get; }

    public IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities { get; }

    public SourceSetId SourceSetId => Identity.SourceSetId;

    public string SourceSetName => _sourceSetName;

    public string StructuralPath => Identity.StructuralPath;

    public string StructuralIdentity => Identity.StructuralIdentity;

    public SourceValueCandidateKind CandidateKind => Identity.CandidateKind;

    public string CandidateKindText => CandidateKind switch
    {
        SourceValueCandidateKind.Attribute => "Attribute",
        SourceValueCandidateKind.Structural => "Structural",
        _ => "Element"
    };

    public string StructuralContextToolTip => $"{CandidateKindText}: {StructuralIdentity}";

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

    private static IReadOnlyList<DiscoveredSourceContributionViewModel>
        CreateLogicalContributions(IEnumerable<DiscoveredInformation> information)
    {
        var contributions = new List<DiscoveredSourceContributionViewModel>();
        var positionsBySource = new Dictionary<SourceId, int>();
        foreach (var source in information.SelectMany(item => item.ContributingSources))
        {
            if (positionsBySource.TryGetValue(source.SourceId, out var position))
            {
                var current = contributions[position];
                contributions[position] = new DiscoveredSourceContributionViewModel(
                    current.SourceId,
                    current.SourceName,
                    checked(current.OccurrenceCount + source.OccurrenceCount));
                continue;
            }

            positionsBySource.Add(source.SourceId, contributions.Count);
            contributions.Add(new DiscoveredSourceContributionViewModel(source));
        }

        return contributions;
    }

    private static DiscoveryInformationDisposition ResolveLogicalDisposition(
        IEnumerable<DiscoveryInformationDisposition> dispositions)
    {
        var values = dispositions.Distinct().ToArray();
        return values.Length == 1
            ? values[0]
            : DiscoveryInformationDisposition.Neutral;
    }

    private static string? ResolveLogicalDatabaseTagOverride(IEnumerable<string?> overrides)
    {
        var values = overrides.Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private readonly record struct LogicalDiscoveryIdentity(
        SourceSetId SourceSetId,
        SourceValueCandidateKind CandidateKind,
        string CanonicalFieldIdentity)
    {
        public static LogicalDiscoveryIdentity Create(DiscoveryInformationIdentity identity)
        {
            var canonicalFieldIdentity = identity.CandidateKind switch
            {
                SourceValueCandidateKind.Element => GetFinalStructuralSegment(
                    identity.StructuralPath),
                SourceValueCandidateKind.Attribute => GetFinalStructuralSegment(
                    identity.StructuralIdentity),
                _ => identity.StructuralIdentity
            };
            return new LogicalDiscoveryIdentity(
                identity.SourceSetId,
                identity.CandidateKind,
                canonicalFieldIdentity);
        }

        private static string GetFinalStructuralSegment(string path)
        {
            var namespaceDepth = 0;
            var finalSeparator = -1;
            for (var index = 0; index < path.Length; index++)
            {
                switch (path[index])
                {
                    case '{':
                        namespaceDepth++;
                        break;
                    case '}' when namespaceDepth > 0:
                        namespaceDepth--;
                        break;
                    case '/' when namespaceDepth == 0:
                        finalSeparator = index;
                        break;
                }
            }

            return finalSeparator < 0 ? path : path[(finalSeparator + 1)..];
        }
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

    internal DiscoveredSourceContributionViewModel(
        SourceId sourceId,
        string sourceName,
        int occurrenceCount)
    {
        SourceId = sourceId;
        SourceName = sourceName;
        OccurrenceCount = occurrenceCount;
    }

    public SourceId SourceId { get; }

    public string SourceIdText => SourceId.ToString();

    public string SourceName { get; }

    public int OccurrenceCount { get; }

    public string OccurrenceCountText => OccurrenceCount.ToString("N0", CultureInfo.CurrentCulture);
}
