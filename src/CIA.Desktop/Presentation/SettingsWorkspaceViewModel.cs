using System.Diagnostics;
using System.IO;
using System.Reflection;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
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

    public SettingsWorkspaceViewModel(
        IProcessingHistoryReader historyReader,
        SettingsWorkspaceRuntimePaths runtimePaths)
    {
        _historyReader = historyReader ?? throw new ArgumentNullException(nameof(historyReader));
        ArgumentNullException.ThrowIfNull(runtimePaths);
        ArgumentNullException.ThrowIfNull(runtimePaths.ApplicationPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePaths.LogDirectory);

        _logDirectory = Path.GetFullPath(runtimePaths.LogDirectory);
        ManagedStoragePaths = CreateManagedStoragePaths(
            runtimePaths.ApplicationPaths,
            _logDirectory);
        RefreshCommand = new RelayCommand(Refresh);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
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

    public IReadOnlyList<string> EntryTypeOptions { get; }

    public IReadOnlyList<string> SeverityOptions { get; }

    public IReadOnlyList<string> StreamOptions { get; }

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

    public bool IsPersistentSettingsAvailable => false;

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
        "Settings shown here use current application defaults. Persistent Settings are not available yet.";

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
