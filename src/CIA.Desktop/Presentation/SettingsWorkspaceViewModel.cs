using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class SettingsWorkspaceViewModel : ObservableObject
{
    public const string NoHistoryMessage = "No processing history has been recorded yet.";
    public const string NoIssuesMessage = "No recorded processing issues.";
    private readonly IProcessingHistoryReader _historyReader;
    private IReadOnlyList<ProcessingAttemptPresentation> _attempts = [];
    private IReadOnlyList<ProcessingIssuePresentation> _issues = [];
    private ProcessingAttemptPresentation? _selectedAttempt;
    private ProcessingIssuePresentation? _selectedIssue;
    private string? _readProblem;
    private bool _isHistorySelected = true;

    public SettingsWorkspaceViewModel(IProcessingHistoryReader historyReader)
    {
        _historyReader = historyReader ?? throw new ArgumentNullException(nameof(historyReader));
        RefreshCommand = new RelayCommand(Refresh);
        ShowHistoryCommand = new RelayCommand(() => IsHistorySelected = true);
        ShowIssuesCommand = new RelayCommand(() => IsHistorySelected = false);
        Refresh();
    }

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand ShowHistoryCommand { get; }

    public IRelayCommand ShowIssuesCommand { get; }

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

    public ProcessingAttemptPresentation? SelectedAttempt
    {
        get => _selectedAttempt;
        set => SetProperty(ref _selectedAttempt, value);
    }

    public ProcessingIssuePresentation? SelectedIssue
    {
        get => _selectedIssue;
        set => SetProperty(ref _selectedIssue, value);
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

    public bool IsHistorySelected
    {
        get => _isHistorySelected;
        private set
        {
            if (SetProperty(ref _isHistorySelected, value))
            {
                OnPropertyChanged(nameof(IsIssuesSelected));
            }
        }
    }

    public bool IsIssuesSelected => !IsHistorySelected;

    public bool HasHistory => Attempts.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public bool HasReadProblem => ReadProblem is not null;

    public string HistoryEmptyMessage => NoHistoryMessage;

    public string IssuesEmptyMessage => NoIssuesMessage;

    private void Refresh()
    {
        var selectedAttemptKey = SelectedAttempt?.IdentityKey;
        var selectedIssueId = SelectedIssue?.DiagnosticId;
        var snapshot = _historyReader.Read();

        Attempts = snapshot.Attempts
            .Select(record => new ProcessingAttemptPresentation(record))
            .ToArray();
        Issues = snapshot.Diagnostics
            .Select(record => new ProcessingIssuePresentation(record))
            .ToArray();
        ReadProblem = snapshot.ReadProblem;

        SelectedAttempt = Attempts.FirstOrDefault(
                attempt => attempt.IdentityKey == selectedAttemptKey)
            ?? Attempts.FirstOrDefault();
        SelectedIssue = Issues.FirstOrDefault(issue => issue.DiagnosticId == selectedIssueId)
            ?? Issues.FirstOrDefault();
    }
}

public sealed class ProcessingAttemptPresentation
{
    public ProcessingAttemptPresentation(ProcessingAttemptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        OperationId = record.Correlation.OperationId.ToString();
        RecordedAtLocal = record.RecordedAtUtc.ToLocalTime();
        OperationName = record.OperationName;
        Outcome = HistoryPresentationText.ForOutcome(record.TerminalOutcome);
        Stage = record.FinalStage ?? "Not recorded";
        SuccessfulItems = CreateItems(record.Items, OperationItemState.ProcessedSuccessfully);
        FailedItems = CreateItems(record.Items, OperationItemState.Failed);
        UnprocessedItems = CreateItems(record.Items, OperationItemState.Unprocessed);
        IdentityKey = $"{OperationId}|{record.RecordedAtUtc:O}|{OperationName}";
    }

    public string IdentityKey { get; }

    public string OperationId { get; }

    public DateTimeOffset RecordedAtLocal { get; }

    public string OperationName { get; }

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
    }

    public string DiagnosticId { get; }

    public string OperationId { get; }

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
