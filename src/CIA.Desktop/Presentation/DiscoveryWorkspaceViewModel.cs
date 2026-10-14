using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
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

public enum DiscoveryMode
{
    Input,
    Output
}

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
    private readonly ActiveDiscoveryOutputPlan _outputPlan;
    private readonly ActiveLoadedSourceSet _sourceSet;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly DatabaseBuildCoordinator? _databaseBuildCoordinator;
    private readonly GlobalOperationProgress? _globalProgress;
    private readonly ObservableCollection<DiscoveredInformationItemViewModel> _visibleInformation = [];
    private readonly ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> _readOnlyInformation;
    private readonly ObservableCollection<DiscoveredSourceContributionViewModel>
        _inspectedSources = [];
    private readonly ReadOnlyObservableCollection<DiscoveredSourceContributionViewModel>
        _readOnlyInspectedSources;
    private readonly ObservableCollection<SourceSetLayoutItemViewModel> _sourceSetLayouts = [];
    private readonly ReadOnlyObservableCollection<SourceSetLayoutItemViewModel>
        _readOnlySourceSetLayouts;
    private readonly ObservableCollection<DiscoveryOutputSetItemViewModel> _outputSets = [];
    private readonly ReadOnlyObservableCollection<DiscoveryOutputSetItemViewModel>
        _readOnlyOutputSets;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _showInputModeCommand;
    private readonly RelayCommand _showOutputModeCommand;
    private readonly RelayCommand _previousOutputColumnsCommand;
    private readonly RelayCommand _nextOutputColumnsCommand;
    private readonly AsyncRelayCommand<DiscoveredInformationItemViewModel?> _selectInformationCommand;
    private readonly RelayCommand<DiscoveredInformationItemViewModel> _selectDetailedInformationCommand;
    private readonly AsyncRelayCommand _previousOccurrenceCommand;
    private readonly AsyncRelayCommand _nextOccurrenceCommand;
    private readonly AsyncRelayCommand _jumpToOccurrenceCommand;
    private readonly AsyncRelayCommand<DiscoveredInformationItemViewModel> _inspectSourcesCommand;
    private readonly AsyncRelayCommand _previousSourceInspectionPageCommand;
    private readonly AsyncRelayCommand _nextSourceInspectionPageCommand;
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
    private readonly SemaphoreSlim _outputPreviewRequestGate = new(1, 1);
    private readonly object _previewRequestStateGate = new();
    private readonly object _outputPreviewRequestStateGate = new();
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private IReadOnlyList<DiscoveredInformationItemViewModel> _allInformation = [];
    private IReadOnlyList<DiscoveredInformationItemViewModel> _filteredInformation = [];
    private DiscoveryMode _mode = DiscoveryMode.Input;
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
    private DiscoveredInformationItemViewModel? _previewInformation;
    private DiscoveredInformationItemViewModel? _inspectedInformation;
    private OperationId? _publishedDiscoveryOperationId;
    private int _sourceInspectionStartIndex;
    private int _sourceInspectionTotalOccurrenceCount;
    private bool _isSourceInspectionLoading;
    private string? _sourceInspectionFailure;
    private string _occurrencePreviewText =
        "Select a discovered tag to inspect its occurrence value.";
    private string _occurrencePreviewContextText = "No structural context selected";
    private string _occurrenceOrdinalInput = string.Empty;
    private int _currentOccurrenceOrdinal;
    private int _occurrenceTotal;
    private bool _isOccurrenceLoading;
    private int _previewRequestVersion;
    private CancellationTokenSource _previewRequestCancellation = new();
    private int _outputPreviewRequestVersion;
    private CancellationTokenSource _outputPreviewRequestCancellation = new();
    private DiscoverySelectionImpactPresentation _selectionImpact =
        DiscoverySelectionImpactPresentation.Empty;
    private IReadOnlyList<DiscoveryOutputPreviewGroupPresentation> _outputPreviewGroups = [];
    private DiscoveryOutputPreviewGroupPresentation? _selectedOutputPreviewGroup;
    private DiscoveryOutputSetItemViewModel? _selectedOutputSet;
    private bool _isOutputPreviewLoading;
    private string _outputPreviewStatusText =
        "Select output fields and an occurrence to preview the projected result.";
    private string _outputPreviewSourceText = string.Empty;
    private int _outputPreviewColumnStartIndex;
    private int _outputPreviewTotalColumnCount;
    private string _outputPlanStatusText = "Select information to create the Baseline output plan.";
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
    private DiscoveryFamilyCatalog? _familyCatalog;

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
        GlobalOperationProgress? globalProgress = null,
        ActiveDiscoveryOutputPlan? outputPlan = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryClient);
        ArgumentNullException.ThrowIfNull(activeConfiguration);
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryClient = discoveryClient;
        _activeConfiguration = activeConfiguration;
        _outputPlan = outputPlan ?? new ActiveDiscoveryOutputPlan();
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
        _readOnlyInspectedSources =
            new ReadOnlyObservableCollection<DiscoveredSourceContributionViewModel>(
                _inspectedSources);
        _readOnlySourceSetLayouts = new ReadOnlyObservableCollection<SourceSetLayoutItemViewModel>(
            _sourceSetLayouts);
        _readOnlyOutputSets = new ReadOnlyObservableCollection<DiscoveryOutputSetItemViewModel>(
            _outputSets);

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
        _showInputModeCommand = new RelayCommand(
            () => Mode = DiscoveryMode.Input);
        _showOutputModeCommand = new RelayCommand(
            () => Mode = DiscoveryMode.Output);
        _previousOutputColumnsCommand = new RelayCommand(
            MoveToPreviousOutputColumns,
            () => _outputPreviewColumnStartIndex > 0 && !IsOutputPreviewLoading);
        _nextOutputColumnsCommand = new RelayCommand(
            MoveToNextOutputColumns,
            () => _outputPreviewColumnStartIndex
                + DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage
                < _outputPreviewTotalColumnCount
                && !IsOutputPreviewLoading);
        _selectInformationCommand = new AsyncRelayCommand<DiscoveredInformationItemViewModel?>(
            SelectInformationAsync,
            _ => true,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        _selectDetailedInformationCommand = new RelayCommand<DiscoveredInformationItemViewModel>(
            SelectDetailedInformation,
            information => information is not null && !information.IsLogicalParent);
        _previousOccurrenceCommand = new AsyncRelayCommand(
            () => NavigateOccurrenceAsync(CurrentOccurrenceOrdinal - 1),
            CanNavigateToPreviousOccurrence);
        _nextOccurrenceCommand = new AsyncRelayCommand(
            () => NavigateOccurrenceAsync(CurrentOccurrenceOrdinal + 1),
            CanNavigateToNextOccurrence);
        _jumpToOccurrenceCommand = new AsyncRelayCommand(
            JumpToOccurrenceAsync,
            CanJumpToOccurrence);
        _inspectSourcesCommand = new AsyncRelayCommand<DiscoveredInformationItemViewModel>(
            InspectSourcesAsync,
            information => information?.SourceCount > 0);
        _previousSourceInspectionPageCommand = new AsyncRelayCommand(
            () => LoadSourceInspectionPageAsync(
                Math.Max(
                    0,
                    _sourceInspectionStartIndex - DiscoveryContributorPaging.DefaultPageSize)),
            () => !IsSourceInspectionLoading && _sourceInspectionStartIndex > 0);
        _nextSourceInspectionPageCommand = new AsyncRelayCommand(
            () => LoadSourceInspectionPageAsync(
                _sourceInspectionStartIndex + DiscoveryContributorPaging.DefaultPageSize),
            () => !IsSourceInspectionLoading
                && _sourceInspectionStartIndex + _inspectedSources.Count
                    < (InspectedInformation?.SourceCount ?? 0));
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
        RefreshSelectionImpact();
    }

    public ReadOnlyObservableCollection<DiscoveredInformationItemViewModel> Information =>
        _readOnlyInformation;

    public ReadOnlyObservableCollection<SourceSetLayoutItemViewModel> SourceSetLayouts =>
        _readOnlySourceSetLayouts;

    public ReadOnlyObservableCollection<DiscoveryOutputSetItemViewModel> OutputSets =>
        _readOnlyOutputSets;

    public IReadOnlyList<DiscoveryOutputSetGroupPresentation> OutputSetGroups => OutputSets
        .GroupBy(item => new { item.ParentSourceSetId, item.ParentSourceSetName })
        .Select(group => new DiscoveryOutputSetGroupPresentation(
            group.Key.ParentSourceSetId,
            group.Key.ParentSourceSetName,
            group.ToArray(),
            _outputPlan.GetProjectionMode(group.Key.ParentSourceSetId) ==
                DiscoveryOutputProjectionMode.OneRowPerSourceFile,
            SetOneRowPerSourceFile))
        .ToArray();

    public DiscoveryOutputPlanSnapshot OutputPlan => _outputPlan.Current;

    public IReadOnlyList<RepeatedDataLayoutOption> RepeatedDataLayoutOptions { get; } =
    [
        new(RepeatedDataLayout.AlignRepeatedGroupsByPosition, "Align repeated groups by position"),
        new(RepeatedDataLayout.StructuralRows, "Structural rows"),
        new(RepeatedDataLayout.AllCombinations, "All combinations"),
        new(RepeatedDataLayout.NumberRepeatedValuesIntoColumns, "Number repeated values into columns")
    ];

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 250];

    public DiscoveryMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsInputMode));
                OnPropertyChanged(nameof(IsOutputMode));
                if (value == DiscoveryMode.Output)
                {
                    RefreshSelectionImpact();
                    ScheduleOutputPreview();
                }
                else
                {
                    SupersedeOutputPreviewRequest(clearPresentation: false);
                }
            }
        }
    }

    public bool IsInputMode => Mode == DiscoveryMode.Input;

    public bool IsOutputMode => Mode == DiscoveryMode.Output;

    public DiscoverySelectionImpactPresentation SelectionImpact
    {
        get => _selectionImpact;
        private set
        {
            if (SetProperty(ref _selectionImpact, value))
            {
                OnPropertyChanged(nameof(HasSelectionImpact));
                OnPropertyChanged(nameof(SelectionSummary));
                OnPropertyChanged(nameof(SelectionFanoutSummary));
            }
        }
    }

    public bool HasSelectionImpact => SelectionImpact.Groups.Count > 0;

    public IReadOnlyList<DiscoveryOutputPreviewGroupPresentation> OutputPreviewGroups
    {
        get => _outputPreviewGroups;
        private set
        {
            if (SetProperty(ref _outputPreviewGroups, value))
            {
                OnPropertyChanged(nameof(HasOutputPreviewGroups));
            }
        }
    }

    public bool HasOutputPreviewGroups => OutputPreviewGroups.Count > 0;

    public DiscoveryOutputPreviewGroupPresentation? SelectedOutputPreviewGroup
    {
        get => _selectedOutputPreviewGroup;
        set => SetProperty(ref _selectedOutputPreviewGroup, value);
    }

    public DiscoveryOutputSetItemViewModel? SelectedOutputSet
    {
        get => _selectedOutputSet;
        set
        {
            if (SetProperty(ref _selectedOutputSet, value))
            {
                ResetOutputColumnPage();
                ScheduleOutputPreview();
            }
        }
    }

    public bool IsOutputPreviewLoading
    {
        get => _isOutputPreviewLoading;
        private set
        {
            if (SetProperty(ref _isOutputPreviewLoading, value))
            {
                _previousOutputColumnsCommand.NotifyCanExecuteChanged();
                _nextOutputColumnsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string OutputPreviewStatusText
    {
        get => _outputPreviewStatusText;
        private set => SetProperty(ref _outputPreviewStatusText, value);
    }

    public string OutputPreviewSourceText
    {
        get => _outputPreviewSourceText;
        private set => SetProperty(ref _outputPreviewSourceText, value);
    }

    public string OutputPlanStatusText
    {
        get => _outputPlanStatusText;
        private set => SetProperty(ref _outputPlanStatusText, value);
    }

    public IAsyncRelayCommand RunDiscoveryCommand { get; }

    public IAsyncRelayCommand BuildDatabaseCommand { get; }

    public IRelayCommand ShowInputModeCommand => _showInputModeCommand;

    public IRelayCommand ShowOutputModeCommand => _showOutputModeCommand;

    public IRelayCommand PreviousOutputColumnsCommand => _previousOutputColumnsCommand;

    public IRelayCommand NextOutputColumnsCommand => _nextOutputColumnsCommand;

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

    public IRelayCommand<DiscoveredInformationItemViewModel> SelectDetailedInformationCommand =>
        _selectDetailedInformationCommand;

    public IReadOnlyList<RowFamilyDefinition> RowFamilies => _familyCatalog?.Families ?? [];

    public IAsyncRelayCommand PreviousOccurrenceCommand => _previousOccurrenceCommand;

    public IAsyncRelayCommand NextOccurrenceCommand => _nextOccurrenceCommand;

    public IAsyncRelayCommand JumpToOccurrenceCommand => _jumpToOccurrenceCommand;

    public IAsyncRelayCommand<DiscoveredInformationItemViewModel> InspectSourcesCommand =>
        _inspectSourcesCommand;

    public IAsyncRelayCommand PreviousSourceInspectionPageCommand =>
        _previousSourceInspectionPageCommand;

    public IAsyncRelayCommand NextSourceInspectionPageCommand =>
        _nextSourceInspectionPageCommand;

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
        _activeConfiguration.Current.Items.Count(item =>
            item.Disposition == DiscoveryInformationDisposition.Selected
            && item.Identity.CandidateKind != SourceValueCandidateKind.ElementScope),
        _activeConfiguration.Current.Items.Count(item =>
            item.Identity.CandidateKind != SourceValueCandidateKind.ElementScope));

    public string SelectionFanoutSummary => string.Format(
        CultureInfo.CurrentCulture,
        "{0:N0} logical tags → {1:N0} detailed fields → {2:N0} output tables",
        _allInformation.Count(information =>
            !information.IsStructuralScope && information.HasAnySelectedChild),
        _activeConfiguration.Current.Items.Count(item =>
            item.Disposition == DiscoveryInformationDisposition.Selected
            && item.Identity.CandidateKind != SourceValueCandidateKind.ElementScope),
        SelectionImpact.Groups.Count);

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
                _previewInformation = value;
                OnPropertyChanged(nameof(PreviewInformation));
                SelectOutputSetForParent(value?.SourceSetId);
                OnPropertyChanged(nameof(SelectedDatabaseTag));
                OnPropertyChanged(nameof(CanEditDatabaseTagOverride));
                OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
                OnPropertyChanged(nameof(OccurrencePreviewContextText));
                _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
                PrepareOccurrenceSelection(value);
                _selectInformationCommand.Execute(value);
            }
        }
    }

    public DiscoveredInformationItemViewModel? PreviewInformation => _previewInformation;

    public string SelectedDatabaseTag
    {
        get => PreviewInformation?.DatabaseTag ?? string.Empty;
        set
        {
            if (PreviewInformation is not null)
            {
                SetDatabaseTagOverride(PreviewInformation, value);
            }
        }
    }

    public bool CanEditDatabaseTagOverride =>
        PreviewInformation is not null
        && !PreviewInformation.IsStructuralScope
        && CanChangeConfiguration();

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
        PreviewInformation?.HasAnyDatabaseTagOverride == true ? "Revert" : "Default";

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
                OnPropertyChanged(nameof(SourceInspectionPageText));
                _previousSourceInspectionPageCommand.NotifyCanExecuteChanged();
                _nextSourceInspectionPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsSourceInspectionOpen
    {
        get => _isSourceInspectionOpen;
        set => SetProperty(ref _isSourceInspectionOpen, value);
    }

    public IReadOnlyList<DiscoveredSourceContributionViewModel> InspectedSources =>
        _readOnlyInspectedSources;

    public bool IsSourceInspectionLoading
    {
        get => _isSourceInspectionLoading;
        private set
        {
            if (SetProperty(ref _isSourceInspectionLoading, value))
            {
                _previousSourceInspectionPageCommand.NotifyCanExecuteChanged();
                _nextSourceInspectionPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SourceInspectionPageText => InspectedInformation is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            "Sources {0:N0}-{1:N0} of {2:N0}",
            _inspectedSources.Count == 0 ? 0 : _sourceInspectionStartIndex + 1,
            _sourceInspectionStartIndex + _inspectedSources.Count,
            InspectedInformation.SourceCount);

    public string SourceInspectionSummary => InspectedInformation is null
        ? string.Empty
        : _sourceInspectionFailure is not null
            ? _sourceInspectionFailure
        : string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} sources · {1:N0} occurrences · matches tag aggregate",
            InspectedInformation.SourceCount,
            _sourceInspectionTotalOccurrenceCount);

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

        lock (_outputPreviewRequestStateGate)
        {
            Interlocked.Increment(ref _outputPreviewRequestVersion);
            _outputPreviewRequestCancellation.Cancel();
        }
    }

    public string OccurrencePreviewContextText
    {
        get => _occurrencePreviewContextText;
        private set => SetProperty(ref _occurrencePreviewContextText, value);
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
                result.Information,
                result.FamilyCatalog
                    ?? throw new InvalidOperationException(
                        "A successful Discovery result requires a Row Family catalog."));
            _activeConfiguration.SynchronizeSourceSets(
                _sourceSet.SourceSets.Select(sourceSet => sourceSet.SourceSetId)
                    .Concat(result.Information.Select(information => information.SourceSetId)));
            _reusableProfileSessionState.ApplyTo(_activeConfiguration);
            var dispositions = _activeConfiguration.Current.Items.ToDictionary(
                item => item.Identity,
                item => item.Disposition);
            var databaseTagOverrides = _activeConfiguration.DatabaseTagOverridesByIdentity;
            IsSourceInspectionOpen = false;
            InspectedInformation = null;
            _inspectedSources.Clear();
            _allInformation = DiscoveredInformationItemViewModel
                .CreateLogicalItems(
                    result.Information,
                    ResolveSourceSetName,
                    identity => dispositions[identity],
                    identity => databaseTagOverrides.GetValueOrDefault(identity))
                .ToArray();
            _familyCatalog = result.FamilyCatalog;
            OnPropertyChanged(nameof(RowFamilies));
            RefreshSourceSetLayouts();
            _publishedDiscoveryOperationId = result.Completion.Correlation.OperationId;
            _issueCount = result.Issues.Count;
            _progressStage = "Stage: Complete";
            _progressCompletedSourceCount = _progressTotalSourceCount;
            _hasCompletedDiscovery = true;
            _currentPage = 1;
            RefreshPresentation();
            RefreshSelectionImpact();
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
            || !ReferenceEquals(PreviewInformation, information))
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
        SupersedeOutputPreviewRequest(clearPresentation: true);
        var canLoadOccurrence = information is not null
            && _publishedDiscoveryOperationId is not null;
        IsOccurrenceLoading = canLoadOccurrence;
        CurrentOccurrenceOrdinal = 0;
        OccurrenceTotal = information?.TotalOccurrenceCount ?? 0;
        SetOccurrenceOrdinalInput(string.Empty);

        if (information is null)
        {
            OccurrencePreviewText = "Select a discovered tag to inspect its occurrence value.";
            OccurrencePreviewContextText = "No structural context selected";
            return;
        }

        OccurrencePreviewContextText = information.DetailedIdentities.Count > 1
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0} · {1:N0} detailed structural contexts",
                information.ElementRoleText,
                information.DetailedIdentities.Count)
            : $"{information.ElementRoleText} · {information.StructuralIdentity}";

        if (!canLoadOccurrence)
        {
            OccurrencePreviewText = "The selected occurrence could not be retrieved.";
            return;
        }

        OccurrencePreviewText = "Loading occurrence...";
    }

    private Task NavigateOccurrenceAsync(int ordinal)
    {
        var information = PreviewInformation;
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
                || !lookup.DetailedIdentities.Contains(occurrence.Identity)
                || occurrence.Ordinal != ordinal
                || occurrence.TotalOccurrenceCount != information.TotalOccurrenceCount)
            {
                if (CurrentOccurrenceOrdinal == 0)
                {
                    OccurrencePreviewText = result.FailureDescription
                        ?? "The selected occurrence could not be retrieved.";
                }

                RestoreOccurrenceOrdinalInput();
                return;
            }

            OccurrencePreviewText = information.IsStructuralScope
                ? "Structural container — no scalar value. Selecting it applies to its materializable descendant fields."
                : occurrence.Value;
            OccurrencePreviewContextText =
                $"{information.ElementRoleText} · {occurrence.Identity.StructuralIdentity}";
            OccurrenceTotal = occurrence.TotalOccurrenceCount;
            CurrentOccurrenceOrdinal = occurrence.Ordinal;
            SetOccurrenceOrdinalInput(
                occurrence.Ordinal.ToString(CultureInfo.InvariantCulture));
            if (!information.IsStructuralScope)
            {
                ScheduleOutputPreview();
            }
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

    private void RefreshSelectionImpact()
    {
        var readyParentIds = _sourceSet.CreateIncludedReadySnapshot()
            .Select(source => source.SourceSetId)
            .ToHashSet();
        var parents = _sourceSet.SourceSets
            .Where(parent => readyParentIds.Contains(parent.SourceSetId))
            .Select(parent => parent.SourceSetId)
            .ToArray();
        if (TryCreateCurrentBuildSpecification(out var specification))
        {
            SelectionImpact = DiscoverySelectionImpactBuilder.Create(specification);
            var projectionModes = parents.ToDictionary(
                parent => parent,
                _outputPlan.GetProjectionMode);
            _outputPlan.Synchronize(
                parents,
                DiscoveryOutputPlanBuilder.Create(specification, projectionModes),
                _sourceSet.SourceSets.Count);
        }
        else
        {
            SelectionImpact = DiscoverySelectionImpactPresentation.Empty;
            _outputPlan.Synchronize(parents, [], _sourceSet.SourceSets.Count);
        }

        RefreshOutputSetPresentation();
    }

    private void RefreshOutputSetPresentation()
    {
        var selectedOutputSetId = SelectedOutputSet?.OutputSetId;
        var impactByFamily = SelectionImpact.Groups.ToDictionary(group => group.RowFamilyId);
        var items = _outputPlan.Current.OutputSets.Select(definition =>
        {
            var familyImpacts = definition.RowFamilyIds
                .Select(familyId => impactByFamily.GetValueOrDefault(familyId))
                .Where(candidate => candidate is not null)
                .Cast<DiscoverySelectionImpactGroupPresentation>()
                .ToArray();
            if (familyImpacts.Length == 0)
            {
                throw new InvalidOperationException(
                    "Every proposed Output Set requires current selection-impact metadata.");
            }

            var impact = definition.ProjectionMode ==
                DiscoveryOutputProjectionMode.OneRowPerSourceFile
                ? new DiscoverySelectionImpactGroupPresentation(
                    familyImpacts[0].RowFamilyId,
                    definition.Name,
                    "Whole source file",
                    "One row per source file",
                    familyImpacts.SelectMany(item => item.ProjectedColumns)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray())
                : familyImpacts[0];
            return new DiscoveryOutputSetItemViewModel(
                definition,
                ResolveSourceSetName(definition.ParentSourceSetId),
                impact,
                SetOutputSetEnabled,
                RenameOutputSet);
        }).ToArray();

        _outputSets.Clear();
        foreach (var item in items)
        {
            _outputSets.Add(item);
        }

        OnPropertyChanged(nameof(OutputSetGroups));

        var selected = selectedOutputSetId is { } retainedId
            ? items.FirstOrDefault(item => item.OutputSetId == retainedId)
            : null;
        selected ??= PreviewInformation is { } preview
            ? items.FirstOrDefault(item => item.ParentSourceSetId == preview.SourceSetId)
            : null;
        selected ??= items.FirstOrDefault();
        if (!ReferenceEquals(_selectedOutputSet, selected))
        {
            _selectedOutputSet = selected;
            OnPropertyChanged(nameof(SelectedOutputSet));
        }

        OnPropertyChanged(nameof(OutputPlan));
        OutputPlanStatusText = items.Length == 0
            ? "Select information to create the Baseline output plan."
            : string.Format(
                CultureInfo.CurrentCulture,
                "Baseline: {0:N0} of {1:N0} proposed Output Sets enabled.",
                items.Count(item => item.IsEnabled),
                items.Length);
    }

    private void SelectOutputSetForParent(SourceSetId? parentSourceSetId)
    {
        if (parentSourceSetId is null
            || SelectedOutputSet?.ParentSourceSetId == parentSourceSetId)
        {
            return;
        }

        SelectedOutputSet = OutputSets.FirstOrDefault(item =>
            item.ParentSourceSetId == parentSourceSetId);
    }

    private bool SetOneRowPerSourceFile(SourceSetId parentSourceSetId, bool enabled)
    {
        if (!CanChangeConfiguration())
        {
            return false;
        }

        var mode = enabled
            ? DiscoveryOutputProjectionMode.OneRowPerSourceFile
            : DiscoveryOutputProjectionMode.PreserveRelationships;
        if (_outputPlan.GetProjectionMode(parentSourceSetId) == mode)
        {
            return false;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted || !_outputPlan.SetProjectionMode(parentSourceSetId, mode))
        {
            return false;
        }

        ResetOutputColumnPage();
        RefreshSelectionImpact();
        OnPropertyChanged(nameof(DatabaseStateText));
        ScheduleOutputPreview();
        return true;
    }

    private bool SetOutputSetEnabled(OutputSetId outputSetId, bool isEnabled)
    {
        var current = _outputPlan.Current.OutputSets.FirstOrDefault(set =>
            set.OutputSetId == outputSetId);
        if (current is null || current.IsEnabled == isEnabled || !CanChangeConfiguration())
        {
            return false;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted || !_outputPlan.SetEnabled(outputSetId, isEnabled))
        {
            return false;
        }

        OnPropertyChanged(nameof(OutputPlan));
        OutputPlanStatusText = string.Format(
            CultureInfo.CurrentCulture,
            "Baseline: {0:N0} of {1:N0} proposed Output Sets enabled.",
            _outputPlan.Current.OutputSets.Count(set => set.IsEnabled),
            _outputPlan.Current.OutputSets.Count);
        OnPropertyChanged(nameof(DatabaseStateText));
        return true;
    }

    private bool RenameOutputSet(OutputSetId outputSetId, string name)
    {
        if (!CanChangeConfiguration())
        {
            return false;
        }

        if (!_outputPlan.CanRename(
                outputSetId,
                name,
                _sourceSet.SourceSets.Select(sourceSet => sourceSet.Name),
                out var normalizedName,
                out var failureReason))
        {
            if (failureReason is not null)
            {
                OutputPlanStatusText = failureReason;
            }

            return false;
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted || !_outputPlan.Rename(outputSetId, normalizedName))
        {
            return false;
        }

        OnPropertyChanged(nameof(OutputPlan));
        OutputPlanStatusText = $"Set name updated to {normalizedName}.";
        OnPropertyChanged(nameof(DatabaseStateText));
        return true;
    }

    private bool TryCreateCurrentBuildSpecification(out DatabaseBuildSpecification specification)
    {
        try
        {
            if (_databaseBuildCoordinator is null)
            {
                specification = null!;
                return false;
            }

            specification = _databaseBuildCoordinator.CreateDiscoveryProjectionSpecification();
            return specification.Datasets.Count > 0;
        }
        catch (ArgumentException)
        {
            specification = null!;
            return false;
        }
        catch (InvalidOperationException)
        {
            specification = null!;
            return false;
        }
    }

    private void ScheduleOutputPreview()
    {
        var requestState = SupersedeOutputPreviewRequest(clearPresentation: true);
        if (!IsOutputMode)
        {
            return;
        }

        if (!TryCreateOutputPreviewRequest(out var request, out var unavailableReason))
        {
            OutputPreviewStatusText = unavailableReason;
            return;
        }

        IsOutputPreviewLoading = true;
        OutputPreviewStatusText = "Occurrence output preview processing…";
        _ = LoadOutputPreviewAsync(
            request,
            requestState.Version,
            requestState.CancellationToken);
    }

    private bool TryCreateOutputPreviewRequest(
        out DiscoveryOutputPreviewRequest request,
        out string unavailableReason)
    {
        var selectedOutputSet = SelectedOutputSet;
        if (!HasSelectionImpact
            || selectedOutputSet is null
            || !TryCreateCurrentBuildSpecification(out var specification))
        {
            request = null!;
            unavailableReason = "Select at least one field to preview the projected output.";
            return false;
        }

        var information = PreviewInformation;
        var discoveryOperationId = _publishedDiscoveryOperationId;
        if (information is null
            || discoveryOperationId is null
            || CurrentOccurrenceOrdinal < 1
            || !TryCreateOccurrenceLookup(
                discoveryOperationId.Value,
                information,
                CurrentOccurrenceOrdinal,
                out var lookup))
        {
            request = null!;
            unavailableReason = "Select a valid occurrence to preview the projected output.";
            return false;
        }

        if (selectedOutputSet.ParentSourceSetId != information.SourceSetId)
        {
            request = null!;
            unavailableReason =
                "Select an occurrence from the parent Source Set to preview this Output Set.";
            return false;
        }

        DatabaseBuildSpecification approvedSpecification;
        try
        {
            approvedSpecification = DatabaseBuildSpecificationFactory.CreateApprovedOutputSets(
                _activeConfiguration.Current,
                _activeConfiguration.DatabaseTagOverridesByIdentity,
                _sourceSet.CreateIncludedReadySnapshot(),
                _sourceSet.SourceSets.ToDictionary(
                    definition => definition.SourceSetId,
                    definition => definition.Name),
                _activeConfiguration.FamilyCatalog!,
                _activeConfiguration.FamilyAssignments,
                _outputPlan.Current);
        }
        catch (ArgumentException)
        {
            request = null!;
            unavailableReason = "The selected Output Set is no longer available.";
            return false;
        }

        var dataset = approvedSpecification.Datasets.FirstOrDefault(candidate =>
            candidate.OutputSetId == selectedOutputSet.OutputSetId
            && candidate.SourceSetId == information.SourceSetId);
        if (dataset is null)
        {
            request = null!;
            unavailableReason =
                "The selected occurrence contributes to no selected output table.";
            return false;
        }

        var rowFamilies = dataset.RowFamilies.ToArray();
        var fields = dataset.Fields.ToArray();
        if (rowFamilies.Length == 0 || fields.Length == 0)
        {
            request = null!;
            unavailableReason = "The selected Output Set is no longer available.";
            return false;
        }

        request = new DiscoveryOutputPreviewRequest(
            selectedOutputSet.OutputSetId,
            lookup,
            new DiscoveryOutputProjectionSpecification(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.RepeatedDataLayout,
                rowFamilies,
                fields,
                dataset.ProjectionMode,
                dataset.SourceProjectionRepeatedPaths),
            columnStartIndex: _outputPreviewColumnStartIndex);
        unavailableReason = string.Empty;
        return true;
    }

    private async Task LoadOutputPreviewAsync(
        DiscoveryOutputPreviewRequest request,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            await Task.Delay(125, cancellationToken).ConfigureAwait(false);
            await _outputPreviewRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            if (!IsCurrentOutputPreviewRequest(request, requestVersion))
            {
                return;
            }

            // Once framed IPC begins, drain the response before another request uses the shared
            // connection. Superseded work is discarded by version instead of abandoning a frame.
            var result = await _discoveryClient.GetOutputPreviewAsync(request)
                .ConfigureAwait(false);
            if (!IsCurrentOutputPreviewRequest(request, requestVersion))
            {
                return;
            }

            DispatchToUi(() => ApplyOutputPreviewResult(request, requestVersion, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (IsCurrentOutputPreviewRequest(request, requestVersion))
            {
                DispatchToUi(() =>
                {
                    OutputPreviewStatusText =
                        "The occurrence output preview could not be generated.";
                    OutputPreviewGroups = [];
                    SelectedOutputPreviewGroup = null;
                });
            }
        }
        finally
        {
            if (enteredGate)
            {
                _outputPreviewRequestGate.Release();
            }

            if (requestVersion == Volatile.Read(ref _outputPreviewRequestVersion))
            {
                DispatchToUi(() => IsOutputPreviewLoading = false);
            }
        }
    }

    private void ApplyOutputPreviewResult(
        DiscoveryOutputPreviewRequest request,
        int requestVersion,
        DiscoveryOutputPreviewClientResult result)
    {
        if (!IsCurrentOutputPreviewRequest(request, requestVersion))
        {
            return;
        }

        var preview = result.Preview;
        if (!result.Accepted
            || preview is null
            || preview.OutputSetId != request.OutputSetId
            || preview.DiscoveryOperationId != request.Occurrence.DiscoveryOperationId
            || preview.OccurrenceOrdinal != request.Occurrence.GlobalOrdinal
            || !request.Occurrence.DetailedIdentities.Contains(preview.OccurrenceIdentity))
        {
            OutputPreviewGroups = [];
            SelectedOutputPreviewGroup = null;
            OutputPreviewSourceText = string.Empty;
            OutputPreviewStatusText = result.FailureDescription
                ?? "The occurrence output preview could not be generated.";
            return;
        }

        var impactByFamily = SelectionImpact.Groups.ToDictionary(group => group.RowFamilyId);
        var selectedDefinition = _outputPlan.Current.OutputSets.First(set =>
            set.OutputSetId == request.OutputSetId);
        var groups = preview.Groups
            .Where(group => preview.ProjectionMode ==
                    DiscoveryOutputProjectionMode.OneRowPerSourceFile
                || impactByFamily.ContainsKey(group.RowFamilyId))
            .Select(group => new DiscoveryOutputPreviewGroupPresentation(
                preview.ProjectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile
                    ? new DiscoverySelectionImpactGroupPresentation(
                        group.RowFamilyId,
                        selectedDefinition.Name,
                        "Whole source file",
                        "One row per source file",
                        group.Columns.Select(column => column.Header).ToArray())
                    : impactByFamily[group.RowFamilyId],
                group))
            .ToArray();
        var previousFamily = SelectedOutputPreviewGroup?.RowFamilyId;
        OutputPreviewGroups = groups;
        SelectedOutputPreviewGroup = previousFamily is { } selectedFamily
            ? groups.FirstOrDefault(group => group.RowFamilyId == selectedFamily)
                ?? groups.FirstOrDefault()
            : groups.FirstOrDefault();
        var previewGroup = preview.Groups.FirstOrDefault();
        _outputPreviewColumnStartIndex = previewGroup?.ColumnStartIndex ?? 0;
        _outputPreviewTotalColumnCount = previewGroup?.TotalColumnCount ?? 0;
        _previousOutputColumnsCommand.NotifyCanExecuteChanged();
        _nextOutputColumnsCommand.NotifyCanExecuteChanged();
        OutputPreviewSourceText = preview.ProjectionMode
                == DiscoveryOutputProjectionMode.OneRowPerSourceFile
            ? $"Source: {preview.SourceName} ({preview.SourceId})"
            : $"Occurrence {preview.OccurrenceOrdinal:N0} · {preview.SourceName}";
        OutputPreviewStatusText = groups.Length == 0
            ? "The selected occurrence contributes to no selected output table."
            : preview.ProjectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile
                ? CreateSourceFilePreviewStatus(previewGroup)
                : "Projected with Preserve relationships.";
    }

    private bool IsCurrentOutputPreviewRequest(
        DiscoveryOutputPreviewRequest request,
        int requestVersion)
    {
        return Volatile.Read(ref _disposed) == 0
            && IsOutputMode
            && requestVersion == Volatile.Read(ref _outputPreviewRequestVersion)
            && SelectedOutputSet?.OutputSetId == request.OutputSetId
            && _publishedDiscoveryOperationId == request.Occurrence.DiscoveryOperationId
            && CurrentOccurrenceOrdinal == request.Occurrence.GlobalOrdinal;
    }

    private void MoveToPreviousOutputColumns()
    {
        _outputPreviewColumnStartIndex = Math.Max(
            0,
            _outputPreviewColumnStartIndex
                - DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage);
        ScheduleOutputPreview();
    }

    private void MoveToNextOutputColumns()
    {
        _outputPreviewColumnStartIndex += DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage;
        ScheduleOutputPreview();
    }

    private void ResetOutputColumnPage()
    {
        _outputPreviewColumnStartIndex = 0;
        _outputPreviewTotalColumnCount = 0;
        _previousOutputColumnsCommand.NotifyCanExecuteChanged();
        _nextOutputColumnsCommand.NotifyCanExecuteChanged();
    }

    internal static string CreateSourceFilePreviewStatus(DiscoveryOutputPreviewGroup? group)
    {
        if (group is null || group.TotalColumnCount == 0)
        {
            return "The selected source has no values for the selected fields.";
        }

        var first = group.ColumnStartIndex + 1;
        var last = group.ColumnStartIndex + group.Columns.Count;
        var range = $"Data columns {first:N0}-{last:N0} of {group.TotalColumnCount:N0} for this source sample. Final dataset and export metadata width are validated during build/export.";
        return group.TotalColumnCount > DiscoveryOutputPreviewLimits.WideColumnWarningThreshold
            ? $"{range} Wide output warning: the selected source exceeds 500 data columns."
            : range;
    }

    private PreviewRequest SupersedeOutputPreviewRequest(bool clearPresentation)
    {
        lock (_outputPreviewRequestStateGate)
        {
            _outputPreviewRequestCancellation.Cancel();
            _outputPreviewRequestCancellation.Dispose();
            _outputPreviewRequestCancellation = new CancellationTokenSource();
            var request = new PreviewRequest(
                Interlocked.Increment(ref _outputPreviewRequestVersion),
                _outputPreviewRequestCancellation.Token);
            if (clearPresentation)
            {
                OutputPreviewGroups = [];
                SelectedOutputPreviewGroup = null;
                OutputPreviewSourceText = string.Empty;
                IsOutputPreviewLoading = false;
            }

            return request;
        }
    }

    private static bool TryCreateOccurrenceLookup(
        OperationId discoveryOperationId,
        DiscoveredInformationItemViewModel information,
        int globalOrdinal,
        out DiscoveryOccurrenceLookup lookup)
    {
        if (globalOrdinal < 1 || globalOrdinal > information.TotalOccurrenceCount)
        {
            lookup = null!;
            return false;
        }

        lookup = new DiscoveryOccurrenceLookup(
            discoveryOperationId,
            information.DetailedIdentities,
            globalOrdinal,
            information.TotalOccurrenceCount);
        return true;
    }

    private bool IsCurrentPreviewRequest(
        int requestVersion,
        DiscoveredInformationItemViewModel information)
    {
        return Volatile.Read(ref _disposed) == 0
            && requestVersion == Volatile.Read(ref _previewRequestVersion)
            && ReferenceEquals(PreviewInformation, information);
    }

    private void SelectDetailedInformation(DiscoveredInformationItemViewModel? information)
    {
        if (information is null || information.IsLogicalParent)
        {
            return;
        }

        _previewInformation = information;
        OnPropertyChanged(nameof(PreviewInformation));
        SelectOutputSetForParent(information.SourceSetId);
        OnPropertyChanged(nameof(SelectedDatabaseTag));
        OnPropertyChanged(nameof(CanEditDatabaseTagOverride));
        OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
        OnPropertyChanged(nameof(OccurrencePreviewContextText));
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        PrepareOccurrenceSelection(information);
        _selectInformationCommand.Execute(information);
    }

    internal void SelectLogicalInformation(DiscoveredInformationItemViewModel information)
    {
        ArgumentNullException.ThrowIfNull(information);
        if (!information.IsLogicalParent)
        {
            return;
        }

        if (!ReferenceEquals(SelectedInformation, information))
        {
            SelectedInformation = information;
            return;
        }

        _previewInformation = information;
        OnPropertyChanged(nameof(PreviewInformation));
        OnPropertyChanged(nameof(OccurrencePreviewContextText));
        SelectOutputSetForParent(information.SourceSetId);
        PrepareOccurrenceSelection(information);
        _selectInformationCommand.Execute(information);
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
            && information.SelectionTargetIdentities.Count > 0
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
            && PreviewInformation?.HasAnyDatabaseTagOverride == true;
    }

    private bool CanSelectVisible()
    {
        return CanChangeConfiguration()
            && GetDetailedItems(_filteredInformation).Any(
                information => !information.IsSelected && !information.IsBlacklisted);
    }

    private bool CanDeselectVisible()
    {
        return CanChangeConfiguration()
            && GetDetailedItems(_filteredInformation).Any(information => information.IsSelected);
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
                information.SelectionTargetIdentities,
                information.SelectionState != true));
    }

    private void ToggleBlacklist(DiscoveredInformationItemViewModel? information)
    {
        if (information is null)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetBlacklisted(
                information.SelectionTargetIdentities,
                !information.IsBlacklisted));
    }

    private void ClearDatabaseTagOverride()
    {
        if (PreviewInformation is not null)
        {
            SetDatabaseTagOverride(PreviewInformation, databaseTagOverride: null);
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

        ApplyActiveConfiguration();
    }

    private void SetVisibleSelection(bool isSelected)
    {
        var targets = GetDetailedItems(_filteredInformation)
            .Where(information => isSelected
                ? !information.IsSelected && !information.IsBlacklisted
                : information.IsSelected)
            .SelectMany(information => information.SelectionTargetIdentities)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        ApplyConfigurationChange(
            () => _activeConfiguration.SetSelection(targets, isSelected));
    }

    private static IEnumerable<DiscoveredInformationItemViewModel> GetDetailedItems(
        IEnumerable<DiscoveredInformationItemViewModel> logicalItems)
    {
        return logicalItems.SelectMany(information => information.Children.Count == 0
            ? [information]
            : information.Children);
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
            information.ApplyDetailedConfiguration(dispositions, databaseTagOverrides);
        }

        OnPropertyChanged(nameof(SelectedDatabaseTag));
        OnPropertyChanged(nameof(DatabaseTagOverrideActionText));
        _clearDatabaseTagOverrideCommand.NotifyCanExecuteChanged();
        RefreshPresentation();
        RefreshSelectionImpact();
        ScheduleOutputPreview();
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
        RefreshSelectionImpact();
        ScheduleOutputPreview();
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
            query = query.Where(information => information.HasAnySelectedChild);
        }

        if (ShowOnlyDatabaseTagOverrides)
        {
            query = query.Where(information => information.HasAnyDatabaseTagOverride);
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
        OnPropertyChanged(nameof(SelectionFanoutSummary));
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

    private async Task InspectSourcesAsync(DiscoveredInformationItemViewModel? information)
    {
        if (information is null || _publishedDiscoveryOperationId is null)
        {
            return;
        }

        InspectedInformation = information;
        _sourceInspectionStartIndex = 0;
        _sourceInspectionTotalOccurrenceCount = information.TotalOccurrenceCount;
        _sourceInspectionFailure = null;
        _inspectedSources.Clear();
        IsSourceInspectionOpen = true;
        OnPropertyChanged(nameof(SourceInspectionSummary));
        OnPropertyChanged(nameof(SourceInspectionPageText));
        await LoadSourceInspectionPageAsync(0);
    }

    private async Task LoadSourceInspectionPageAsync(int startIndex)
    {
        var information = InspectedInformation;
        var operationId = _publishedDiscoveryOperationId;
        if (information is null || operationId is null || startIndex < 0)
        {
            return;
        }

        IsSourceInspectionLoading = true;
        _sourceInspectionFailure = null;
        try
        {
            var result = await _discoveryClient.GetContributorsAsync(
                new DiscoveryContributorPageQuery(
                    operationId.Value,
                    information.DetailedIdentities,
                    startIndex,
                    DiscoveryContributorPaging.DefaultPageSize,
                    information.SourceCount));
            if (!ReferenceEquals(InspectedInformation, information)
                || _publishedDiscoveryOperationId != operationId)
            {
                return;
            }

            if (!result.Accepted || result.Page is null)
            {
                _sourceInspectionFailure = result.FailureDescription
                    ?? "The contributing sources could not be retrieved.";
                _inspectedSources.Clear();
                return;
            }

            _sourceInspectionStartIndex = result.Page.StartIndex;
            _sourceInspectionTotalOccurrenceCount = result.Page.TotalOccurrenceCount;
            _inspectedSources.Clear();
            foreach (var source in result.Page.Sources)
            {
                _inspectedSources.Add(new DiscoveredSourceContributionViewModel(source));
            }
        }
        catch (Exception)
        {
            _sourceInspectionFailure = "The contributing sources could not be retrieved.";
            _inspectedSources.Clear();
        }
        finally
        {
            if (ReferenceEquals(InspectedInformation, information))
            {
                IsSourceInspectionLoading = false;
                OnPropertyChanged(nameof(SourceInspectionSummary));
                OnPropertyChanged(nameof(SourceInspectionPageText));
                _previousSourceInspectionPageCommand.NotifyCanExecuteChanged();
                _nextSourceInspectionPageCommand.NotifyCanExecuteChanged();
            }
        }
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
        RefreshSelectionImpact();
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
    private int _selectedTargetCount;
    private int _blacklistedTargetCount;
    private string? _databaseTagOverride;
    private string _sourceSetName;
    private bool _isExpanded;

    public DiscoveredInformationItemViewModel(
        DiscoveredInformation information,
        string sourceSetName,
        DiscoveryInformationDisposition disposition,
        string? databaseTagOverride = null)
        : this(
            [information],
            sourceSetName,
            disposition,
            databaseTagOverride,
            isLogicalParent: true,
            children: [],
            selectionTargetIdentities: [information.Identity],
            targetDispositions: [disposition])
    {
    }

    private DiscoveredInformationItemViewModel(
        IReadOnlyList<DiscoveredInformation> information,
        string sourceSetName,
        DiscoveryInformationDisposition disposition,
        string? databaseTagOverride,
        bool isLogicalParent,
        IReadOnlyList<DiscoveredInformationItemViewModel> children,
        IReadOnlyList<DiscoveryInformationIdentity> selectionTargetIdentities,
        IReadOnlyList<DiscoveryInformationDisposition> targetDispositions)
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
        SelectionTargetIdentities = selectionTargetIdentities.Distinct().ToArray();
        Identity = information[0].Identity;
        RowFamilyId = information[0].RowFamilyId;
        IsLogicalParent = isLogicalParent;
        Children = children;
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
        SourceCount = isLogicalParent
            ? information.Select(item => item.LogicalSourceCount).Distinct().Single()
            : information[0].DetailedSourceCount;
        SampleValue = DiscoverySampleValueFormatter.Format(information[0].SampleValue);
        _disposition = disposition;
        UpdateTargetCounts(targetDispositions);
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

        var detailedInformation = information.ToArray();
        var targetsByIdentity = CreateSelectionTargets(detailedInformation);

        return detailedInformation
            .GroupBy(item => DiscoveryLogicalIdentity.Create(item.Identity))
            .Select(group => group
                .OrderBy(item => item.Identity.StructuralIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Identity.StructuralPath, StringComparer.Ordinal)
                .ThenBy(item => item.Identity.InformationType, StringComparer.Ordinal)
                .ToArray())
            .Select(group =>
            {
                var sourceSetName = resolveSourceSetName(group[0].SourceSetId);
                var children = group.Select(item =>
                {
                    var targets = targetsByIdentity[item.Identity];
                    var targetDispositions = targets.Select(resolveDisposition).ToArray();
                    return new DiscoveredInformationItemViewModel(
                        [item],
                        sourceSetName,
                        ResolveLogicalDisposition(targetDispositions),
                        item.Identity.CandidateKind == SourceValueCandidateKind.ElementScope
                            ? null
                            : resolveDatabaseTagOverride(item.Identity),
                        isLogicalParent: false,
                        children: [],
                        targets,
                        targetDispositions);
                }).ToArray();
                var parentTargets = children
                    .SelectMany(child => child.SelectionTargetIdentities)
                    .Distinct()
                    .ToArray();
                var parentTargetDispositions = parentTargets.Select(resolveDisposition).ToArray();
                return new DiscoveredInformationItemViewModel(
                    group,
                    sourceSetName,
                    ResolveLogicalDisposition(parentTargetDispositions),
                    ResolveLogicalDatabaseTagOverride(
                        group.Select(item => resolveDatabaseTagOverride(item.Identity))),
                    isLogicalParent: true,
                    children,
                    parentTargets,
                    parentTargetDispositions);
            });
    }

    public DiscoveryInformationIdentity Identity { get; }

    public IReadOnlyList<DiscoveredInformation> DetailedInformation { get; }

    public IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities { get; }

    public IReadOnlyList<DiscoveryInformationIdentity> SelectionTargetIdentities { get; }

    public IReadOnlyList<DiscoveredInformationItemViewModel> Children { get; }

    public bool IsLogicalParent { get; }

    public bool HasDetailedChildren => Children.Count > 0;

    public string StructuralContextCountText => DetailedIdentities.Count == 1
        ? "1 context"
        : string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} contexts",
            DetailedIdentities.Count);

    public RowFamilyId? RowFamilyId { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public SourceSetId SourceSetId => Identity.SourceSetId;

    public string SourceSetName => _sourceSetName;

    public string StructuralPath => Identity.StructuralPath;

    public string StructuralIdentity => Identity.StructuralIdentity;

    public SourceValueCandidateKind CandidateKind => Identity.CandidateKind;

    public bool IsStructuralScope => CandidateKind == SourceValueCandidateKind.ElementScope;

    public SourceElementRole? ElementRole => ResolveElementRole(DetailedInformation);

    public string ElementRoleText => ElementRole switch
    {
        SourceElementRole.Structural => "Structural container",
        SourceElementRole.Mixed => "Mixed-content element",
        SourceElementRole.ValueBearing => "Value-bearing element",
        _ => CandidateKindText
    };

    public string CandidateKindText => CandidateKind switch
    {
        SourceValueCandidateKind.Attribute => "Attribute",
        SourceValueCandidateKind.Structural => "Structural",
        SourceValueCandidateKind.ElementScope => "Structural container",
        _ => "Element"
    };

    public string StructuralContextToolTip => $"{CandidateKindText}: {StructuralIdentity}";

    public string InformationType => Identity.InformationType;

    public string DatabaseTag => IsStructuralScope
        ? "Descendant fields"
        : DatabaseTagOverride ?? InformationType;

    public string? DatabaseTagOverride => _databaseTagOverride;

    public bool HasDatabaseTagOverride => DatabaseTagOverride is not null;

    public bool HasAnyDatabaseTagOverride => IsLogicalParent && Children.Count > 0
        ? Children.Any(child => child.HasDatabaseTagOverride)
        : HasDatabaseTagOverride;

    public int TotalOccurrenceCount { get; }

    public string TotalOccurrenceText => TotalOccurrenceCount.ToString("N0", CultureInfo.CurrentCulture);

    public int SourceCount { get; }

    public string SourceCountText => SourceCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SampleValue { get; }

    public DiscoveryInformationDisposition Disposition => _disposition;

    public bool IsSelected => SelectionState == true;

    public bool HasAnySelectedChild => _selectedTargetCount > 0;

    public bool? SelectionState
    {
        get
        {
            if (SelectionTargetIdentities.Count == 0)
            {
                return false;
            }

            var eligibleCount = SelectionTargetIdentities.Count - _blacklistedTargetCount;
            if (_selectedTargetCount == 0)
            {
                return false;
            }

            return _selectedTargetCount == eligibleCount ? true : null;
        }
    }

    public bool IsBlacklisted => SelectionTargetIdentities.Count > 0
        && _blacklistedTargetCount == SelectionTargetIdentities.Count;

    public string DispositionText => SelectionState switch
    {
        null => "Partially selected",
        true => "Selected",
        _ when IsBlacklisted => "Excluded",
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
        OnPropertyChanged(nameof(HasAnySelectedChild));
        OnPropertyChanged(nameof(SelectionState));
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
        OnPropertyChanged(nameof(HasAnyDatabaseTagOverride));
    }

    internal void ApplyDetailedConfiguration(
        IReadOnlyDictionary<DiscoveryInformationIdentity, DiscoveryInformationDisposition>
            dispositions,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> databaseTagOverrides)
    {
        ArgumentNullException.ThrowIfNull(dispositions);
        ArgumentNullException.ThrowIfNull(databaseTagOverrides);
        if (!IsLogicalParent)
        {
            ApplyTargetDispositions(dispositions);
            ApplyDatabaseTagOverride(IsStructuralScope
                ? null
                : databaseTagOverrides.GetValueOrDefault(Identity));
            return;
        }

        foreach (var child in Children)
        {
            child.ApplyDetailedConfiguration(dispositions, databaseTagOverrides);
        }

        ApplyTargetDispositions(dispositions);
        ApplyDatabaseTagOverride(ResolveLogicalDatabaseTagOverride(
            DetailedIdentities.Select(identity => databaseTagOverrides.GetValueOrDefault(identity))));
        OnPropertyChanged(nameof(HasAnySelectedChild));
        OnPropertyChanged(nameof(HasAnyDatabaseTagOverride));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(DispositionText));
    }

    internal void ApplySourceSetName(string sourceSetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        SetProperty(ref _sourceSetName, sourceSetName, nameof(SourceSetName));
        foreach (var child in Children)
        {
            child.ApplySourceSetName(sourceSetName);
        }
    }

    private static DiscoveryInformationDisposition ResolveLogicalDisposition(
        IEnumerable<DiscoveryInformationDisposition> dispositions)
    {
        var values = dispositions.Distinct().ToArray();
        if (values.Length == 0)
        {
            return DiscoveryInformationDisposition.Neutral;
        }

        return values.Length == 1
            ? values[0]
            : DiscoveryInformationDisposition.Neutral;
    }

    private static string? ResolveLogicalDatabaseTagOverride(IEnumerable<string?> overrides)
    {
        var values = overrides.Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static SourceElementRole? ResolveElementRole(
        IEnumerable<DiscoveredInformation> information)
    {
        var roles = information
            .Select(item => item.ElementRole)
            .Where(role => role is not null)
            .Distinct()
            .ToArray();
        return roles.Length switch
        {
            0 => null,
            1 => roles[0],
            _ => SourceElementRole.Mixed
        };
    }

    private void ApplyTargetDispositions(
        IReadOnlyDictionary<DiscoveryInformationIdentity, DiscoveryInformationDisposition>
            dispositions)
    {
        var values = SelectionTargetIdentities.Select(identity => dispositions[identity]).ToArray();
        ApplyDisposition(ResolveLogicalDisposition(values));
        UpdateTargetCounts(values);
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(HasAnySelectedChild));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsBlacklisted));
        OnPropertyChanged(nameof(DispositionText));
        OnPropertyChanged(nameof(BlacklistActionText));
    }

    private void UpdateTargetCounts(
        IEnumerable<DiscoveryInformationDisposition> dispositions)
    {
        var values = dispositions.ToArray();
        _selectedTargetCount = values.Count(value =>
            value == DiscoveryInformationDisposition.Selected);
        _blacklistedTargetCount = values.Count(value =>
            value == DiscoveryInformationDisposition.Blacklisted);
    }

    private static IReadOnlyDictionary<
        DiscoveryInformationIdentity,
        IReadOnlyList<DiscoveryInformationIdentity>> CreateSelectionTargets(
        IReadOnlyList<DiscoveredInformation> information)
    {
        var scopeTargets = information
            .Where(item => item.Identity.CandidateKind == SourceValueCandidateKind.ElementScope)
            .ToDictionary(
                item => item.Identity,
                _ => new List<DiscoveryInformationIdentity>());
        var scopesByPath = scopeTargets.Keys
            .GroupBy(identity => (identity.SourceSetId, identity.StructuralPath))
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var candidate in information.Where(item =>
                     item.Identity.CandidateKind != SourceValueCandidateKind.ElementScope))
        {
            foreach (var path in EnumerateStructuralPathPrefixes(candidate.StructuralPath))
            {
                if (!scopesByPath.TryGetValue((candidate.SourceSetId, path), out var scopes))
                {
                    continue;
                }

                foreach (var scope in scopes)
                {
                    scopeTargets[scope].Add(candidate.Identity);
                }
            }
        }

        return information.ToDictionary(
            item => item.Identity,
            item => (IReadOnlyList<DiscoveryInformationIdentity>)(
                item.Identity.CandidateKind == SourceValueCandidateKind.ElementScope
                    ? scopeTargets[item.Identity].Distinct().ToArray()
                    : [item.Identity]));
    }

    private static IEnumerable<string> EnumerateStructuralPathPrefixes(string structuralPath)
    {
        var namespaceDepth = 0;
        for (var index = 1; index < structuralPath.Length; index++)
        {
            switch (structuralPath[index])
            {
                case '{':
                    namespaceDepth++;
                    break;
                case '}' when namespaceDepth > 0:
                    namespaceDepth--;
                    break;
                case '/' when namespaceDepth == 0:
                    yield return structuralPath[..index];
                    break;
            }
        }

        yield return structuralPath;
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
