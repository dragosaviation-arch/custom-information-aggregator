namespace CIA.Desktop.Presentation;

public enum GlobalOperationProgressState
{
    Idle = 0,
    Active = 1,
    CompletedSuccessfully = 2,
    CompletedWithIssues = 3,
    Failed = 4,
    Cancelled = 5
}

public sealed record GlobalOperationProgressSnapshot(
    Guid UpdateId,
    string OperationName,
    string StageText,
    double? CompletedWork,
    double? TotalWork,
    GlobalOperationProgressState State)
{
    public static GlobalOperationProgressSnapshot Idle { get; } = new(
        Guid.Empty,
        string.Empty,
        string.Empty,
        null,
        null,
        GlobalOperationProgressState.Idle);

    public bool IsActive => State == GlobalOperationProgressState.Active;

    public bool HasDeterminateProgress =>
        IsActive && CompletedWork is not null && TotalWork is > 0;
}

public sealed class GlobalOperationProgress
{
    private readonly object _gate = new();
    private GlobalOperationProgressSnapshot _current = GlobalOperationProgressSnapshot.Idle;

    public event EventHandler<GlobalOperationProgressSnapshot>? Changed;

    public GlobalOperationProgressSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Guid Begin(
        string operationName,
        string stageText,
        double? completedWork = null,
        double? totalWork = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageText);

        var updateId = Guid.NewGuid();
        Publish(new GlobalOperationProgressSnapshot(
            updateId,
            operationName,
            stageText,
            NormalizeCompleted(completedWork, totalWork),
            NormalizeTotal(totalWork),
            GlobalOperationProgressState.Active));
        return updateId;
    }

    public void Report(
        Guid updateId,
        string stageText,
        double? completedWork = null,
        double? totalWork = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageText);

        GlobalOperationProgressSnapshot? changed = null;
        lock (_gate)
        {
            if (!_current.IsActive || _current.UpdateId != updateId)
            {
                return;
            }

            var normalizedTotal = NormalizeTotal(totalWork) ?? _current.TotalWork;
            var normalizedCompleted = NormalizeCompleted(completedWork, normalizedTotal);
            if (normalizedCompleted is not null
                && _current.CompletedWork is not null
                && normalizedTotal == _current.TotalWork)
            {
                normalizedCompleted = Math.Max(
                    normalizedCompleted.Value,
                    _current.CompletedWork.Value);
            }

            changed = _current with
            {
                StageText = stageText,
                CompletedWork = normalizedCompleted,
                TotalWork = normalizedTotal
            };
            _current = changed;
        }

        Changed?.Invoke(this, changed);
    }

    public void Complete(
        Guid updateId,
        GlobalOperationProgressState state,
        string terminalText)
    {
        if (state is GlobalOperationProgressState.Idle or GlobalOperationProgressState.Active)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(terminalText);

        GlobalOperationProgressSnapshot? changed = null;
        lock (_gate)
        {
            if (!_current.IsActive || _current.UpdateId != updateId)
            {
                return;
            }

            changed = _current with
            {
                StageText = terminalText,
                CompletedWork = state is GlobalOperationProgressState.CompletedSuccessfully
                    or GlobalOperationProgressState.CompletedWithIssues
                    ? _current.TotalWork
                    : _current.CompletedWork,
                State = state
            };
            _current = changed;
        }

        Changed?.Invoke(this, changed);
    }

    private void Publish(GlobalOperationProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            _current = snapshot;
        }

        Changed?.Invoke(this, snapshot);
    }

    private static double? NormalizeTotal(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static double? NormalizeCompleted(double? value, double? total) =>
        value is not null && total is > 0 && double.IsFinite(value.Value)
            ? Math.Clamp(value.Value, 0, total.Value)
            : null;
}
