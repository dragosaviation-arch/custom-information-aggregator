using System.Diagnostics;
using System.IO;
using System.Reflection;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Workflow;
using CIA.Desktop.WorkingState;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed record SettingsWorkspaceRuntimePaths(
    ApplicationPaths ApplicationPaths,
    string LogDirectory);

public sealed class SettingsWorkspaceViewModel : ObservableObject
{
    public const string NoHistoryMessage = "No processing history has been recorded yet.";
    public const string NoIssuesMessage = "No recorded processing issues.";
    public const string AllEntryTypes = "All types";
    public const string AllSeverities = "All severities";
    public const string AllAreas = "All areas / operations";
    public const string AllOutcomes = "All outcomes";
    public const string AllStreams = "All streams";
    private readonly IProcessingHistoryReader _historyReader;
    private readonly string _logDirectory;
    private readonly ApplicationSettingsService _settingsService;
    private readonly ISettingsFolderPicker? _settingsFolderPicker;
    private readonly SavedWorkingStateLibrary _savedStateLibrary;
    private readonly IWorkingStateCoordinator? _workingStateCoordinator;
    private readonly IApplicationWorkflowCoordinator? _workflowCoordinator;
    private readonly ISavedWorkingStateDeleteConfirmation _deleteConfirmation;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly AsyncRelayCommand _saveStateCommand;
    private readonly AsyncRelayCommand _restoreStateCommand;
    private readonly AsyncRelayCommand _deleteStateCommand;
    private readonly RelayCommand _confirmDeleteStateCommand;
    private readonly RelayCommand _cancelDeleteStateCommand;
    private IReadOnlyList<ProcessingAttemptPresentation> _attempts = [];
    private IReadOnlyList<ProcessingIssuePresentation> _issues = [];
    private IReadOnlyList<SettingsLogEntryPresentation> _entries = [];
    private IReadOnlyList<SettingsLogEntryPresentation> _visibleEntries = [];
    private IReadOnlyList<string> _areaOptions = [AllAreas];
    private IReadOnlyList<string> _outcomeOptions = [AllOutcomes];
    private ProcessingAttemptPresentation? _selectedAttempt;
    private ProcessingIssuePresentation? _selectedIssue;
    private SettingsLogEntryPresentation? _selectedEntry;
    private string? _readProblem;
    private string? _folderOpenProblem;
    private string _searchText = string.Empty;
    private string _selectedEntryType = AllEntryTypes;
    private string _selectedSeverity = AllSeverities;
    private string _selectedArea = AllAreas;
    private string _selectedOutcome = AllOutcomes;
    private string _selectedStream = AllStreams;
    private bool _wrapLongMessages;
    private bool _compactRows = true;
    private string _temporaryDirectory;
    private string _workingDirectory;
    private string _profilesDirectory;
    private string _settingsDirectory;
    private bool _traverseSubfolders;
    private int _maximumArchiveNestingDepth;
    private bool _persistentArchiveExtractionEnabled;
    private string _persistentArchiveExtractionDirectory;
    private PostExportBehaviorPresentation _selectedPostExportBehavior;
    private string _settingsStatusText;
    private IReadOnlyList<SavedWorkingStateEntry> _savedStates = [];
    private SavedWorkingStateEntry? _selectedSavedState;
    private string _savedStateName = string.Empty;
    private string? _savedStateNameProblem;
    private string _savedStateStatusText = "No saved states have been created yet.";
    private bool _isSavedStateActionRunning;

    public SettingsWorkspaceViewModel(
        IProcessingHistoryReader historyReader,
        SettingsWorkspaceRuntimePaths runtimePaths,
        ApplicationSettingsService? settingsService = null,
        ISettingsFolderPicker? settingsFolderPicker = null,
        SavedWorkingStateLibrary? savedStateLibrary = null,
        IWorkingStateCoordinator? workingStateCoordinator = null,
        IApplicationWorkflowCoordinator? workflowCoordinator = null,
        ISavedWorkingStateDeleteConfirmation? deleteConfirmation = null)
    {
        _historyReader = historyReader ?? throw new ArgumentNullException(nameof(historyReader));
        ArgumentNullException.ThrowIfNull(runtimePaths);
        ArgumentNullException.ThrowIfNull(runtimePaths.ApplicationPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePaths.LogDirectory);

        _logDirectory = Path.GetFullPath(runtimePaths.LogDirectory);
        _settingsService = settingsService ?? new ApplicationSettingsService(
            new ApplicationSettingsStore(
                runtimePaths.ApplicationPaths.LocalApplicationDataDirectory));
        _settingsFolderPicker = settingsFolderPicker;
        _savedStateLibrary = savedStateLibrary
            ?? new SavedWorkingStateLibrary(runtimePaths.ApplicationPaths);
        _workingStateCoordinator = workingStateCoordinator;
        _workflowCoordinator = workflowCoordinator;
        _deleteConfirmation = deleteConfirmation
            ?? new InApplicationSavedWorkingStateDeleteConfirmation();
        _uiSynchronizationContext = SynchronizationContext.Current;
        _deleteConfirmation.Changed += OnDeleteConfirmationChanged;
        if (_workflowCoordinator is not null)
        {
            _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
        }

        var settings = _settingsService.Current;
        _temporaryDirectory = settings.TemporaryDirectory;
        _workingDirectory = settings.WorkingDirectory;
        _profilesDirectory = settings.ProfilesDirectory;
        _settingsDirectory = settings.SettingsDirectory;
        _traverseSubfolders = settings.TraverseSubfolders;
        _maximumArchiveNestingDepth = settings.MaximumArchiveNestingDepth.Value;
        _persistentArchiveExtractionEnabled = settings.PersistentArchiveExtractionEnabled;
        _persistentArchiveExtractionDirectory =
            settings.PersistentArchiveExtractionDirectory ?? string.Empty;
        PostExportBehaviorOptions =
        [
            new(PostExportBehavior.StatusOnly, "Show success/status only"),
            new(PostExportBehavior.OpenExportedFile, "Open exported file"),
            new(PostExportBehavior.OpenContainingFolder, "Open containing folder"),
            new(PostExportBehavior.AskEachTime, "Ask each time")
        ];
        _selectedPostExportBehavior = PostExportBehaviorOptions.Single(option =>
            option.Value == settings.PostExportBehavior);
        _settingsStatusText = CreateInitialSettingsStatus(_settingsService.Startup);
        ManagedStoragePaths = CreateManagedStoragePaths(
            runtimePaths.ApplicationPaths,
            _logDirectory);
        RefreshCommand = new RelayCommand(Refresh);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        RefreshSavedStatesCommand = new RelayCommand(
            () => RefreshSavedStates(updateStatus: true));
        _saveStateCommand = new AsyncRelayCommand(SaveStateAsync, CanSaveState);
        _restoreStateCommand = new AsyncRelayCommand(RestoreStateAsync, CanRestoreState);
        _deleteStateCommand = new AsyncRelayCommand(DeleteStateAsync, CanDeleteState);
        _confirmDeleteStateCommand = new RelayCommand(
            _deleteConfirmation.Accept,
            () => _deleteConfirmation.IsOpen);
        _cancelDeleteStateCommand = new RelayCommand(
            _deleteConfirmation.Decline,
            () => _deleteConfirmation.IsOpen);
        BrowseTemporaryDirectoryCommand = CreateBrowseCommand(
            "Choose CIA Temporary directory",
            () => TemporaryDirectory,
            value => TemporaryDirectory = value);
        BrowseWorkingDirectoryCommand = CreateBrowseCommand(
            "Choose CIA Working directory",
            () => WorkingDirectory,
            value => WorkingDirectory = value);
        BrowseProfilesDirectoryCommand = CreateBrowseCommand(
            "Choose CIA Profiles directory",
            () => ProfilesDirectory,
            value => ProfilesDirectory = value);
        BrowseSettingsDirectoryCommand = CreateBrowseCommand(
            "Choose CIA Settings directory",
            () => SettingsDirectory,
            value => SettingsDirectory = value);
        BrowsePersistentExtractionDirectoryCommand = CreateBrowseCommand(
            "Choose persistent archive extraction directory",
            () => PersistentArchiveExtractionDirectory,
            value => PersistentArchiveExtractionDirectory = value);
        EntryTypeOptions =
        [
            AllEntryTypes,
            SettingsLogEntryPresentation.ActivityType,
            SettingsLogEntryPresentation.IssueType,
            SettingsLogEntryPresentation.RawLogType
        ];
        SeverityOptions = [AllSeverities, "Info", "Warning", "Error", "Debug"];
        StreamOptions =
        [
            AllStreams,
            SettingsLogEntryPresentation.StructuredStream,
            "Desktop",
            "Processing Host"
        ];
        VersionText = CreateVersionText();
        CopyrightText = $"© {DateTime.UtcNow.Year} Dragos";
        Refresh();
    }

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand OpenLogsFolderCommand { get; }

    public IRelayCommand SaveSettingsCommand { get; }

    public IRelayCommand RefreshSavedStatesCommand { get; }

    public IAsyncRelayCommand SaveStateCommand => _saveStateCommand;

    public IAsyncRelayCommand RestoreStateCommand => _restoreStateCommand;

    public IAsyncRelayCommand DeleteStateCommand => _deleteStateCommand;

    public IRelayCommand ConfirmDeleteStateCommand => _confirmDeleteStateCommand;

    public IRelayCommand CancelDeleteStateCommand => _cancelDeleteStateCommand;

    public IRelayCommand BrowseTemporaryDirectoryCommand { get; }

    public IRelayCommand BrowseWorkingDirectoryCommand { get; }

    public IRelayCommand BrowseProfilesDirectoryCommand { get; }

    public IRelayCommand BrowseSettingsDirectoryCommand { get; }

    public IRelayCommand BrowsePersistentExtractionDirectoryCommand { get; }

    public IReadOnlyList<string> EntryTypeOptions { get; }

    public IReadOnlyList<string> SeverityOptions { get; }

    public IReadOnlyList<string> StreamOptions { get; }

    public IReadOnlyList<PostExportBehaviorPresentation> PostExportBehaviorOptions { get; }

    public IReadOnlyList<string> AreaOptions
    {
        get => _areaOptions;
        private set => SetProperty(ref _areaOptions, value);
    }

    public IReadOnlyList<string> OutcomeOptions
    {
        get => _outcomeOptions;
        private set => SetProperty(ref _outcomeOptions, value);
    }

    public IReadOnlyList<ManagedStoragePathPresentation> ManagedStoragePaths { get; }

    public IReadOnlyList<ProcessingAttemptPresentation> Attempts
    {
        get => _attempts;
        private set
        {
            if (SetProperty(ref _attempts, value))
            {
                OnPropertyChanged(nameof(HasHistory));
            }
        }
    }

    public IReadOnlyList<ProcessingIssuePresentation> Issues
    {
        get => _issues;
        private set
        {
            if (SetProperty(ref _issues, value))
            {
                OnPropertyChanged(nameof(HasIssues));
            }
        }
    }

    public IReadOnlyList<SettingsLogEntryPresentation> Entries
    {
        get => _entries;
        private set
        {
            if (SetProperty(ref _entries, value))
            {
                OnPropertyChanged(nameof(HasRawLogEntries));
            }
        }
    }

    public IReadOnlyList<SettingsLogEntryPresentation> VisibleEntries
    {
        get => _visibleEntries;
        private set
        {
            if (SetProperty(ref _visibleEntries, value))
            {
                OnPropertyChanged(nameof(VisibleEntryCountText));
                OnPropertyChanged(nameof(HasVisibleEntries));
                OnPropertyChanged(nameof(EmptyEntriesMessage));
            }
        }
    }

    public ProcessingAttemptPresentation? SelectedAttempt
    {
        get => _selectedAttempt;
        private set => SetProperty(ref _selectedAttempt, value);
    }

    public ProcessingIssuePresentation? SelectedIssue
    {
        get => _selectedIssue;
        private set => SetProperty(ref _selectedIssue, value);
    }

    public SettingsLogEntryPresentation? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value))
            {
                return;
            }

            if (value?.Attempt is not null)
            {
                SelectedAttempt = value.Attempt;
            }

            if (value?.Issue is not null)
            {
                SelectedIssue = value.Issue;
            }

            OnPropertyChanged(nameof(HasSelectedEntry));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilters();
            }
        }
    }

    public string SelectedEntryType
    {
        get => _selectedEntryType;
        set => SetFilter(ref _selectedEntryType, value, AllEntryTypes);
    }

    public string SelectedSeverity
    {
        get => _selectedSeverity;
        set => SetFilter(ref _selectedSeverity, value, AllSeverities);
    }

    public string SelectedArea
    {
        get => _selectedArea;
        set => SetFilter(ref _selectedArea, value, AllAreas);
    }

    public string SelectedOutcome
    {
        get => _selectedOutcome;
        set => SetFilter(ref _selectedOutcome, value, AllOutcomes);
    }

    public string SelectedStream
    {
        get => _selectedStream;
        set => SetFilter(ref _selectedStream, value, AllStreams);
    }

    public bool WrapLongMessages
    {
        get => _wrapLongMessages;
        set => SetProperty(ref _wrapLongMessages, value);
    }

    public bool CompactRows
    {
        get => _compactRows;
        set => SetProperty(ref _compactRows, value);
    }

    public string? ReadProblem
    {
        get => _readProblem;
        private set
        {
            if (SetProperty(ref _readProblem, value))
            {
                OnPropertyChanged(nameof(HasReadProblem));
            }
        }
    }

    public string? FolderOpenProblem
    {
        get => _folderOpenProblem;
        private set
        {
            if (SetProperty(ref _folderOpenProblem, value))
            {
                OnPropertyChanged(nameof(HasFolderOpenProblem));
            }
        }
    }

    public bool HasHistory => Attempts.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public bool HasRawLogEntries => Entries.Any(
        entry => entry.EntryType == SettingsLogEntryPresentation.RawLogType);

    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public bool HasSelectedEntry => SelectedEntry is not null;

    public bool HasReadProblem => ReadProblem is not null;

    public bool HasFolderOpenProblem => FolderOpenProblem is not null;

    public bool IsRawLogCollectionAvailable => false;

    public bool IsExportVisibleAvailable => false;

    public bool IsPersistentSettingsAvailable => true;

    public bool AreFutureSettingsActionsAvailable => false;

    public int DefaultArchiveNestingDepth => ArchiveNestingDepth.DefaultValue;

    public string DefaultBlacklistText => "Not configured";

    public string HistoryEmptyMessage => NoHistoryMessage;

    public string IssuesEmptyMessage => NoIssuesMessage;

    public string VisibleEntryCountText =>
        $"{VisibleEntries.Count} visible / {Entries.Count} total entries";

    public string EmptyEntriesMessage => Entries.Count == 0
        ? NoHistoryMessage
        : "No entries match the current filters.";

    public string SettingsPersistenceText =>
        $"Persistent settings file: {ConfiguredSettingsFilePath}. " +
        $"Current runtime Settings directory: {_settingsService.RuntimePaths.SettingsDirectory}. " +
        "Managed-storage path changes take effect after CIA restarts.";

    public string ConfiguredSettingsFilePath => _settingsService.CurrentSettingsFilePath;

    public string TemporaryDirectory
    {
        get => _temporaryDirectory;
        set => SetProperty(ref _temporaryDirectory, value ?? string.Empty);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value ?? string.Empty);
    }

    public string ProfilesDirectory
    {
        get => _profilesDirectory;
        set => SetProperty(ref _profilesDirectory, value ?? string.Empty);
    }

    public string SettingsDirectory
    {
        get => _settingsDirectory;
        set => SetProperty(ref _settingsDirectory, value ?? string.Empty);
    }

    public bool TraverseSubfolders
    {
        get => _traverseSubfolders;
        set => SetProperty(ref _traverseSubfolders, value);
    }

    public int MaximumArchiveNestingDepth
    {
        get => _maximumArchiveNestingDepth;
        set => SetProperty(ref _maximumArchiveNestingDepth, value);
    }

    public bool PersistentArchiveExtractionEnabled
    {
        get => _persistentArchiveExtractionEnabled;
        set => SetProperty(ref _persistentArchiveExtractionEnabled, value);
    }

    public string PersistentArchiveExtractionDirectory
    {
        get => _persistentArchiveExtractionDirectory;
        set => SetProperty(ref _persistentArchiveExtractionDirectory, value ?? string.Empty);
    }

    public PostExportBehaviorPresentation SelectedPostExportBehavior
    {
        get => _selectedPostExportBehavior;
        set => SetProperty(ref _selectedPostExportBehavior, value);
    }

    public string SettingsStatusText
    {
        get => _settingsStatusText;
        private set => SetProperty(ref _settingsStatusText, value);
    }

    public IReadOnlyList<SavedWorkingStateEntry> SavedStates
    {
        get => _savedStates;
        private set
        {
            if (SetProperty(ref _savedStates, value))
            {
                OnPropertyChanged(nameof(HasSavedStates));
            }
        }
    }

    public SavedWorkingStateEntry? SelectedSavedState
    {
        get => _selectedSavedState;
        set
        {
            if (SetProperty(ref _selectedSavedState, value))
            {
                NotifySavedStateCommandsChanged();
            }
        }
    }

    public string SavedStateName
    {
        get => _savedStateName;
        set
        {
            if (!SetProperty(ref _savedStateName, value ?? string.Empty))
            {
                return;
            }

            SavedStateNameProblem = EvaluateSavedStateNameProblem(_savedStateName);
            NotifySavedStateCommandsChanged();
        }
    }

    public string? SavedStateNameProblem
    {
        get => _savedStateNameProblem;
        private set
        {
            if (SetProperty(ref _savedStateNameProblem, value))
            {
                OnPropertyChanged(nameof(HasSavedStateNameProblem));
                OnPropertyChanged(nameof(SaveStateAvailabilityText));
            }
        }
    }

    public string SavedStateStatusText
    {
        get => _savedStateStatusText;
        private set => SetProperty(ref _savedStateStatusText, value);
    }

    public bool IsSavedStateActionRunning
    {
        get => _isSavedStateActionRunning;
        private set
        {
            if (SetProperty(ref _isSavedStateActionRunning, value))
            {
                NotifySavedStateCommandsChanged();
            }
        }
    }

    public bool HasSavedStates => SavedStates.Count > 0;

    public bool HasSavedStateNameProblem => SavedStateNameProblem is not null;

    public string SavedStatesDirectory => _savedStateLibrary.DirectoryPath;

    public string SaveStateAvailabilityText
    {
        get
        {
            if (SavedStateNameProblem is not null)
            {
                return SavedStateNameProblem;
            }

            if (_workingStateCoordinator is null)
            {
                return "Working-state save is unavailable.";
            }

            var readiness = _workingStateCoordinator.EvaluateSaveReadiness();
            return readiness.NormalOperationReady
                ? "Save the current working state as a managed .cia package."
                : readiness.UnavailableReason ?? "Working-state save is unavailable.";
        }
    }

    public string RestoreStateAvailabilityText
    {
        get
        {
            if (_workingStateCoordinator is null || SelectedSavedState is null)
            {
                return "Select an existing saved state to restore.";
            }

            var readiness = _workingStateCoordinator.EvaluateRestoreReadiness(
                SelectedSavedState.Path);
            return readiness.NormalOperationReady
                ? $"Restore '{SelectedSavedState.Name}'."
                : readiness.UnavailableReason ?? "Working-state restore is unavailable.";
        }
    }

    public bool IsDeleteStateConfirmationOpen => _deleteConfirmation.IsOpen;

    public string DeleteStateConfirmationMessage => _deleteConfirmation.StateName is { } name
        ? $"Delete saved state '{name}'?"
        : "Delete the selected saved state?";

    public bool IsSettingsRestartRequired => _settingsService.IsRestartRequired;

    public string VersionText { get; }

    public string CopyrightText { get; }

    private void Refresh()
    {
        var selectedKey = SelectedEntry?.IdentityKey;
        var selectedAttemptKey = SelectedAttempt?.IdentityKey;
        var selectedIssueKey = SelectedIssue?.IdentityKey;
        var snapshot = _historyReader.Read();

        Attempts = snapshot.Attempts
            .Select(record => new ProcessingAttemptPresentation(record))
            .ToArray();
        Issues = snapshot.Diagnostics
            .Select(record => new ProcessingIssuePresentation(record))
            .ToArray();
        Entries = Attempts
            .Select(SettingsLogEntryPresentation.FromAttempt)
            .Concat(Issues.Select(SettingsLogEntryPresentation.FromIssue))
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .ToArray();
        ReadProblem = snapshot.ReadProblem;

        AreaOptions = CreateFilterOptions(AllAreas, Entries.Select(entry => entry.Area));
        OutcomeOptions = CreateFilterOptions(
            AllOutcomes,
            Entries.Select(entry => entry.Outcome).Where(outcome => outcome != "Not recorded"));
        SelectedAttempt = Attempts.FirstOrDefault(
                attempt => attempt.IdentityKey == selectedAttemptKey)
            ?? Attempts.FirstOrDefault();
        SelectedIssue = Issues.FirstOrDefault(issue => issue.IdentityKey == selectedIssueKey)
            ?? Issues.FirstOrDefault();
        EnsureDynamicFilterSelectionIsValid();
        ApplyFilters(selectedKey);
        RefreshSavedStates(updateStatus: false);
    }

    private void ApplyFilters(string? preferredSelectionKey = null)
    {
        var currentSelectionKey = preferredSelectionKey ?? SelectedEntry?.IdentityKey;
        var search = SearchText.Trim();
        VisibleEntries = Entries
            .Where(entry =>
                SelectedEntryType == AllEntryTypes || entry.EntryType == SelectedEntryType)
            .Where(entry =>
                SelectedSeverity == AllSeverities || entry.Severity == SelectedSeverity)
            .Where(entry => SelectedArea == AllAreas || entry.Area == SelectedArea)
            .Where(entry => SelectedOutcome == AllOutcomes || entry.Outcome == SelectedOutcome)
            .Where(entry => SelectedStream == AllStreams || entry.Stream == SelectedStream)
            .Where(entry => search.Length == 0 || entry.Contains(search))
            .ToArray();

        SelectedEntry = VisibleEntries.FirstOrDefault(
                entry => entry.IdentityKey == currentSelectionKey)
            ?? VisibleEntries.FirstOrDefault();
    }

    private void SetFilter(ref string field, string? value, string defaultValue)
    {
        if (SetProperty(ref field, string.IsNullOrWhiteSpace(value) ? defaultValue : value))
        {
            ApplyFilters();
        }
    }

    private void EnsureDynamicFilterSelectionIsValid()
    {
        if (!AreaOptions.Contains(SelectedArea, StringComparer.Ordinal))
        {
            _selectedArea = AllAreas;
            OnPropertyChanged(nameof(SelectedArea));
        }

        if (!OutcomeOptions.Contains(SelectedOutcome, StringComparer.Ordinal))
        {
            _selectedOutcome = AllOutcomes;
            OnPropertyChanged(nameof(SelectedOutcome));
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = _logDirectory,
                    UseShellExecute = true
                });
            FolderOpenProblem = null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception)
        {
            FolderOpenProblem = "The Logs folder could not be opened.";
        }
    }

    private RelayCommand CreateBrowseCommand(
        string title,
        Func<string> getCurrentValue,
        Action<string> setValue)
    {
        return new RelayCommand(
            () =>
            {
                var selected = _settingsFolderPicker?.Browse(title, getCurrentValue());
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    setValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected)));
                }
            },
            () => _settingsFolderPicker is not null);
    }

    private void SaveSettings()
    {
        try
        {
            var candidate = _settingsService.Current with
            {
                SchemaVersion = ApplicationSettings.CurrentSchemaVersion,
                TemporaryDirectory = TemporaryDirectory,
                WorkingDirectory = WorkingDirectory,
                ProfilesDirectory = ProfilesDirectory,
                SettingsDirectory = SettingsDirectory,
                TraverseSubfolders = TraverseSubfolders,
                MaximumArchiveNestingDepth = ArchiveNestingDepth.From(
                    MaximumArchiveNestingDepth),
                PersistentArchiveExtractionEnabled = PersistentArchiveExtractionEnabled,
                PersistentArchiveExtractionDirectory =
                    string.IsNullOrWhiteSpace(PersistentArchiveExtractionDirectory)
                        ? null
                        : PersistentArchiveExtractionDirectory,
                PostExportBehavior = SelectedPostExportBehavior.Value
            };
            var result = _settingsService.Save(candidate);
            SettingsStatusText = result.Succeeded
                ? _settingsService.IsRestartRequired
                    ? "Settings saved. Restart CIA to apply managed-storage path changes."
                    : "Settings saved. New sessions will use the updated configuration."
                : result.FailureDescription ?? "Settings could not be saved.";
            OnPropertyChanged(nameof(IsSettingsRestartRequired));
            if (result.Succeeded)
            {
                OnPropertyChanged(nameof(ConfiguredSettingsFilePath));
                OnPropertyChanged(nameof(SettingsPersistenceText));
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException)
        {
            SettingsStatusText = exception.Message;
        }
    }

    private bool CanSaveState()
    {
        if (_workingStateCoordinator is null
            || IsSavedStateActionRunning
            || EvaluateSavedStateNameProblem(SavedStateName) is not null)
        {
            return false;
        }

        return _workingStateCoordinator.EvaluateSaveReadiness().NormalOperationReady;
    }

    private bool CanRestoreState()
    {
        if (_workingStateCoordinator is null
            || IsSavedStateActionRunning
            || SelectedSavedState is null)
        {
            return false;
        }

        return _savedStateLibrary.Revalidate(SelectedSavedState).Succeeded
            && _workingStateCoordinator
            .EvaluateRestoreReadiness(SelectedSavedState.Path)
            .NormalOperationReady;
    }

    private bool CanDeleteState() =>
        !IsSavedStateActionRunning
        && SelectedSavedState is not null
        && _savedStateLibrary.Revalidate(SelectedSavedState).Succeeded;

    private async Task SaveStateAsync()
    {
        if (_workingStateCoordinator is null)
        {
            return;
        }

        var target = _savedStateLibrary.ResolveNewTarget(SavedStateName);
        if (!target.Succeeded || target.Path is null)
        {
            SavedStateStatusText = target.Problem ?? "The saved-state target is unavailable.";
            RefreshSavedStates(updateStatus: false);
            return;
        }

        IsSavedStateActionRunning = true;
        try
        {
            var result = await _workingStateCoordinator.SaveAsync(target.Path);
            if (!result.Accepted)
            {
                SavedStateStatusText = result.FailureDescription
                    ?? "The working state could not be saved.";
                RefreshSavedStates(updateStatus: false);
                return;
            }

            RefreshSavedStates(target.Path, updateStatus: false);
            SavedStateStatusText = SelectedSavedState is not null
                && string.Equals(SelectedSavedState.Path, target.Path, StringComparison.OrdinalIgnoreCase)
                    ? $"Saved state '{SelectedSavedState.Name}'."
                    : "The working-state operation completed, but the saved file is unavailable.";
        }
        finally
        {
            IsSavedStateActionRunning = false;
        }
    }

    private async Task RestoreStateAsync()
    {
        if (_workingStateCoordinator is null || SelectedSavedState is null)
        {
            return;
        }

        var selected = SelectedSavedState;
        var validation = _savedStateLibrary.Revalidate(selected);
        if (!validation.Succeeded || validation.Path is null)
        {
            SavedStateStatusText = validation.Problem
                ?? "The selected saved state is unavailable.";
            RefreshSavedStates(updateStatus: false);
            return;
        }

        IsSavedStateActionRunning = true;
        try
        {
            var result = await _workingStateCoordinator.RestoreAsync(validation.Path);
            RefreshSavedStates(validation.Path, updateStatus: false);
            SavedStateStatusText = result.Accepted
                ? $"Restored saved state '{selected.Name}'."
                : result.FailureDescription ?? "The working state could not be restored.";
        }
        finally
        {
            IsSavedStateActionRunning = false;
        }
    }

    private async Task DeleteStateAsync()
    {
        if (SelectedSavedState is null)
        {
            return;
        }

        var selected = SelectedSavedState;
        IsSavedStateActionRunning = true;
        try
        {
            if (!await _deleteConfirmation.ConfirmAsync(selected.Name))
            {
                SavedStateStatusText = "Saved-state deletion cancelled.";
                return;
            }

            var result = _savedStateLibrary.Delete(selected);
            RefreshSavedStates(updateStatus: false);
            SavedStateStatusText = result.Succeeded
                ? $"Deleted saved state '{selected.Name}'."
                : result.Problem ?? "The saved state could not be deleted.";
        }
        finally
        {
            IsSavedStateActionRunning = false;
        }
    }

    private void RefreshSavedStates(
        string? preferredPath = null,
        bool updateStatus = true)
    {
        var selectionPath = preferredPath ?? SelectedSavedState?.Path;
        var inventory = _savedStateLibrary.CreateInventory();
        SavedStates = inventory.States;
        SelectedSavedState = SavedStates.FirstOrDefault(state =>
                selectionPath is not null
                && string.Equals(state.Path, selectionPath, StringComparison.OrdinalIgnoreCase))
            ?? SavedStates.FirstOrDefault();
        SavedStateNameProblem = EvaluateSavedStateNameProblem(SavedStateName);
        if (updateStatus)
        {
            SavedStateStatusText = inventory.Problems.Count > 0
                ? inventory.Problems[0].Description
                : SavedStates.Count == 0
                    ? "No saved states have been created yet."
                    : $"{SavedStates.Count} saved state(s) available.";
        }

        NotifySavedStateCommandsChanged();
    }

    private string? EvaluateSavedStateNameProblem(string stateName)
    {
        var validationProblem = SavedWorkingStateLibrary.ValidateStateName(stateName);
        if (validationProblem is not null)
        {
            return validationProblem;
        }

        return SavedStates.Any(state =>
            string.Equals(state.Name, stateName, StringComparison.OrdinalIgnoreCase))
                ? $"A saved state named '{stateName}' already exists."
                : null;
    }

    private void OnDeleteConfirmationChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(IsDeleteStateConfirmationOpen));
            OnPropertyChanged(nameof(DeleteStateConfirmationMessage));
            _confirmDeleteStateCommand.NotifyCanExecuteChanged();
            _cancelDeleteStateCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot e) =>
        DispatchToUi(NotifySavedStateCommandsChanged);

    private void NotifySavedStateCommandsChanged()
    {
        OnPropertyChanged(nameof(SaveStateAvailabilityText));
        OnPropertyChanged(nameof(RestoreStateAvailabilityText));
        _saveStateCommand.NotifyCanExecuteChanged();
        _restoreStateCommand.NotifyCanExecuteChanged();
        _deleteStateCommand.NotifyCanExecuteChanged();
    }

    private void DispatchToUi(Action action)
    {
        if (_uiSynchronizationContext is null
            || SynchronizationContext.Current == _uiSynchronizationContext)
        {
            action();
            return;
        }

        _uiSynchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
    }

    private static string CreateInitialSettingsStatus(ApplicationSettingsLoadResult result)
    {
        if (result.Diagnostic is not null)
        {
            return result.Diagnostic;
        }

        return result.State == ApplicationSettingsReadState.DefaultsBecauseFileMissing
            ? "No saved settings were found. Controlled defaults are active until you save."
            : "Persistent settings loaded.";
    }

    private static IReadOnlyList<string> CreateFilterOptions(
        string allLabel,
        IEnumerable<string> values)
    {
        return new[] { allLabel }
            .Concat(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            .ToArray();
    }

    private static IReadOnlyList<ManagedStoragePathPresentation> CreateManagedStoragePaths(
        ApplicationPaths paths,
        string logDirectory)
    {
        return
        [
            new("Temporary", paths.TempDirectory, CanOpen: false),
            new("Working", paths.WorkingDirectory, CanOpen: false),
            new("Database", paths.DatabaseDirectory, CanOpen: false),
            new("Logs", logDirectory, CanOpen: true),
            new("Profiles", paths.ProfilesDirectory, CanOpen: false),
            new("Settings", paths.SettingsDirectory, CanOpen: false)
        ];
    }

    private static string CreateVersionText()
    {
        var assembly = typeof(SettingsWorkspaceViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "Unavailable"
            : informationalVersion;
        return $"Version {version} · Development build";
    }
}

public sealed record ManagedStoragePathPresentation(string Name, string Path, bool CanOpen);

public sealed record PostExportBehaviorPresentation(
    PostExportBehavior Value,
    string DisplayName);

public sealed class SettingsLogEntryPresentation
{
    public const string ActivityType = "Activity";
    public const string IssueType = "Issue";
    public const string RawLogType = "Raw Log";
    public const string StructuredStream = "Structured";

    private SettingsLogEntryPresentation(
        string identityKey,
        DateTimeOffset recordedAtUtc,
        string entryType,
        string severity,
        string area,
        string outcome,
        string stage,
        string itemOrSource,
        string message,
        string primaryId,
        string operationId,
        string? itemState,
        string? failureCode,
        string? technicalDetail,
        ProcessingAttemptPresentation? attempt,
        ProcessingIssuePresentation? issue)
    {
        IdentityKey = identityKey;
        RecordedAtUtc = recordedAtUtc;
        RecordedAtLocal = recordedAtUtc.ToLocalTime();
        EntryType = entryType;
        Severity = severity;
        Area = area;
        Outcome = outcome;
        Stage = stage;
        Stream = StructuredStream;
        ItemOrSource = itemOrSource;
        Message = message;
        PrimaryId = primaryId;
        OperationId = operationId;
        ItemState = itemState;
        FailureCode = failureCode;
        TechnicalDetail = technicalDetail;
        Attempt = attempt;
        Issue = issue;
    }

    public string IdentityKey { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public DateTimeOffset RecordedAtLocal { get; }

    public string EntryType { get; }

    public string Severity { get; }

    public string Area { get; }

    public string Outcome { get; }

    public string Stage { get; }

    public string Stream { get; }

    public string ItemOrSource { get; }

    public string Message { get; }

    public string PrimaryId { get; }

    public string OperationId { get; }

    public string? ItemState { get; }

    public string? FailureCode { get; }

    public string? FailureCodeDisplay => FailureCode is null
        ? null
        : $"Failure code: {FailureCode}";

    public string? TechnicalDetail { get; }

    public ProcessingAttemptPresentation? Attempt { get; }

    public ProcessingIssuePresentation? Issue { get; }

    public bool IsActivity => Attempt is not null;

    public bool IsIssue => Issue is not null;

    public static SettingsLogEntryPresentation FromAttempt(
        ProcessingAttemptPresentation attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var itemCount = attempt.SuccessfulCount + attempt.FailedCount + attempt.UnprocessedCount;
        var itemContext = itemCount switch
        {
            0 => "No retained items",
            1 => attempt.SuccessfulItems
                .Concat(attempt.FailedItems)
                .Concat(attempt.UnprocessedItems)
                .Single().Identity,
            _ => $"{itemCount} retained items"
        };

        return new SettingsLogEntryPresentation(
            attempt.IdentityKey,
            attempt.RecordedAtUtc,
            ActivityType,
            SeverityForOutcome(attempt.TerminalOutcome),
            attempt.OperationName,
            attempt.Outcome,
            attempt.Stage,
            itemContext,
            $"{attempt.OperationName} {attempt.Outcome.ToLowerInvariant()}.",
            attempt.OperationId,
            attempt.OperationId,
            itemState: null,
            failureCode: null,
            technicalDetail: null,
            attempt,
            issue: null);
    }

    public static SettingsLogEntryPresentation FromIssue(ProcessingIssuePresentation issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new SettingsLogEntryPresentation(
            issue.IdentityKey,
            issue.RecordedAtUtc,
            IssueType,
            issue.Outcome == "Failed" ? "Error" : "Warning",
            issue.OperationName,
            issue.Outcome ?? "Not recorded",
            issue.Stage,
            issue.AffectedItem,
            issue.Description,
            issue.DiagnosticId,
            issue.OperationId,
            issue.ItemState,
            issue.FailureCode,
            issue.TechnicalDetail,
            attempt: null,
            issue);
    }

    public bool Contains(string search)
    {
        return SearchableValues().Any(
            value => value.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> SearchableValues()
    {
        yield return EntryType;
        yield return Severity;
        yield return Area;
        yield return Outcome;
        yield return Stage;
        yield return Stream;
        yield return ItemOrSource;
        yield return Message;
        yield return PrimaryId;
        yield return OperationId;
        if (ItemState is not null)
        {
            yield return ItemState;
        }

        if (TechnicalDetail is not null)
        {
            yield return TechnicalDetail;
        }

        if (FailureCode is not null)
        {
            yield return FailureCode;
        }
    }

    private static string SeverityForOutcome(OperationOutcome outcome)
    {
        return outcome switch
        {
            OperationOutcome.CompletedSuccessfully => "Info",
            OperationOutcome.CompletedWithIssues => "Warning",
            OperationOutcome.Failed => "Error",
            OperationOutcome.Cancelled => "Warning",
            OperationOutcome.InterruptedIncomplete => "Warning",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }
}

public sealed class ProcessingAttemptPresentation
{
    public ProcessingAttemptPresentation(ProcessingAttemptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        OperationId = record.Correlation.OperationId.ToString();
        RecordedAtUtc = record.RecordedAtUtc;
        RecordedAtLocal = record.RecordedAtUtc.ToLocalTime();
        OperationName = record.OperationName;
        TerminalOutcome = record.TerminalOutcome;
        Outcome = HistoryPresentationText.ForOutcome(record.TerminalOutcome);
        Stage = record.FinalStage ?? "Not recorded";
        SuccessfulItems = CreateItems(record.Items, OperationItemState.ProcessedSuccessfully);
        FailedItems = CreateItems(record.Items, OperationItemState.Failed);
        UnprocessedItems = CreateItems(record.Items, OperationItemState.Unprocessed);
        IdentityKey = $"{OperationId}|{record.RecordedAtUtc:O}|{OperationName}";
    }

    public string IdentityKey { get; }

    public string OperationId { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public DateTimeOffset RecordedAtLocal { get; }

    public string OperationName { get; }

    public OperationOutcome TerminalOutcome { get; }

    public string Outcome { get; }

    public string Stage { get; }

    public IReadOnlyList<ProcessingItemPresentation> SuccessfulItems { get; }

    public IReadOnlyList<ProcessingItemPresentation> FailedItems { get; }

    public IReadOnlyList<ProcessingItemPresentation> UnprocessedItems { get; }

    public int SuccessfulCount => SuccessfulItems.Count;

    public int FailedCount => FailedItems.Count;

    public int UnprocessedCount => UnprocessedItems.Count;

    private static IReadOnlyList<ProcessingItemPresentation> CreateItems(
        IReadOnlyList<OperationItemStatus> items,
        OperationItemState state)
    {
        return items
            .Where(item => item.State == state)
            .Select(item => new ProcessingItemPresentation(item))
            .ToArray();
    }
}

public sealed class ProcessingItemPresentation
{
    public ProcessingItemPresentation(OperationItemStatus item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Identity = item.ItemId;
        State = HistoryPresentationText.ForItemState(item.State);
        FailureCode = item.FailureCode;
    }

    public string Identity { get; }

    public string State { get; }

    public string? FailureCode { get; }
}

public sealed class ProcessingIssuePresentation
{
    public ProcessingIssuePresentation(ProcessingDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        DiagnosticId = record.DiagnosticId.ToString();
        OperationId = record.Correlation.OperationId.ToString();
        RecordedAtUtc = record.RecordedAtUtc;
        RecordedAtLocal = record.RecordedAtUtc.ToLocalTime();
        OperationName = record.OperationName;
        Stage = record.ProcessingStage ?? "Not recorded";
        SourceId = record.SourceId;
        ItemId = record.ItemId;
        AffectedItem = record.ItemId ?? record.SourceId ?? "Not recorded";
        ItemState = record.ItemState is { } itemState
            ? HistoryPresentationText.ForItemState(itemState)
            : null;
        Outcome = record.TerminalOutcome is { } outcome
            ? HistoryPresentationText.ForOutcome(outcome)
            : null;
        Description = record.UserFacingDescription;
        FailureCode = record.FailureCode;
        TechnicalDetail = record.TechnicalDetail;
        IdentityKey = $"{DiagnosticId}|{record.RecordedAtUtc:O}";
    }

    public string IdentityKey { get; }

    public string DiagnosticId { get; }

    public string OperationId { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public DateTimeOffset RecordedAtLocal { get; }

    public string OperationName { get; }

    public string Stage { get; }

    public string? SourceId { get; }

    public string? ItemId { get; }

    public string AffectedItem { get; }

    public string? ItemState { get; }

    public string? Outcome { get; }

    public string Description { get; }

    public string? FailureCode { get; }

    public string? TechnicalDetail { get; }

    public bool HasTechnicalDetails => FailureCode is not null
        || TechnicalDetail is not null
        || SourceId is not null
        || ItemId is not null;
}

internal static class HistoryPresentationText
{
    public static string ForOutcome(OperationOutcome outcome)
    {
        return outcome switch
        {
            OperationOutcome.CompletedSuccessfully => "Completed successfully",
            OperationOutcome.CompletedWithIssues => "Completed with issues",
            OperationOutcome.Failed => "Failed",
            OperationOutcome.Cancelled => "Cancelled",
            OperationOutcome.InterruptedIncomplete => "Interrupted / incomplete",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }

    public static string ForItemState(OperationItemState state)
    {
        return state switch
        {
            OperationItemState.ProcessedSuccessfully => "Processed successfully",
            OperationItemState.Failed => "Failed",
            OperationItemState.Unprocessed => "Unprocessed",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }
}
