using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;

namespace CIA.ProcessingHost.Operations;

public sealed record ProcessingItemPlan
{
    public ProcessingItemPlan(string itemId)
        : this(itemId, [])
    {
    }

    public ProcessingItemPlan(string itemId, IReadOnlyList<string> requiredItemIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(requiredItemIds);

        var dependencies = requiredItemIds.ToArray();

        if (dependencies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Required item IDs cannot be empty or whitespace.",
                nameof(requiredItemIds));
        }

        if (dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
        {
            throw new ArgumentException(
                "Required item IDs must be unique.",
                nameof(requiredItemIds));
        }

        if (dependencies.Contains(itemId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A processing item cannot depend on itself.",
                nameof(requiredItemIds));
        }

        ItemId = itemId;
        RequiredItemIds = Array.AsReadOnly(dependencies);
    }

    public string ItemId { get; }

    public IReadOnlyList<string> RequiredItemIds { get; }
}

public enum OperationCancellationRequestStatus
{
    Accepted = 1,
    AlreadyAccepted = 2,
    NoActiveOperation = 3,
    OperationMismatch = 4
}

public sealed record OperationCancellationRequest(
    OperationCancellationRequestStatus Status,
    Task<OperationCompletion>? Completion)
{
    public bool Accepted => Status is OperationCancellationRequestStatus.Accepted
        or OperationCancellationRequestStatus.AlreadyAccepted;
}

public sealed class CooperativeOperationCancellation
{
    private readonly IProcessingHistoryRecorder _historyRecorder;
    private readonly object _stateGate = new();
    private CooperativeProcessingOperation? _activeOperation;

    public CooperativeOperationCancellation(IProcessingHistoryRecorder historyRecorder)
    {
        _historyRecorder = historyRecorder
            ?? throw new ArgumentNullException(nameof(historyRecorder));
    }

    public OperationId? ActiveOperationId
    {
        get
        {
            lock (_stateGate)
            {
                return _activeOperation?.Correlation.OperationId;
            }
        }
    }

    public CooperativeProcessingOperation BeginOperation(
        OperationCorrelation correlation,
        string operationName,
        string? processingStage,
        IEnumerable<ProcessingItemPlan> items)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(items);

        if (processingStage is not null && string.IsNullOrWhiteSpace(processingStage))
        {
            throw new ArgumentException(
                "A processing stage cannot be empty or whitespace.",
                nameof(processingStage));
        }

        var itemArray = items.ToArray();
        ValidateItemPlans(itemArray);

        lock (_stateGate)
        {
            if (_activeOperation is not null)
            {
                throw new InvalidOperationException(
                    "Only one Processing Host operation can be active at a time.");
            }

            _activeOperation = new CooperativeProcessingOperation(
                correlation,
                operationName,
                processingStage,
                itemArray,
                _historyRecorder,
                ClearCompletedOperation);
            return _activeOperation;
        }
    }

    public OperationCancellationRequest RequestCancellation(OperationId operationId)
    {
        CooperativeProcessingOperation? activeOperation;

        lock (_stateGate)
        {
            activeOperation = _activeOperation;
        }

        if (activeOperation is null)
        {
            return new OperationCancellationRequest(
                OperationCancellationRequestStatus.NoActiveOperation,
                Completion: null);
        }

        if (activeOperation.Correlation.OperationId != operationId)
        {
            return new OperationCancellationRequest(
                OperationCancellationRequestStatus.OperationMismatch,
                Completion: null);
        }

        return activeOperation.RequestCancellation();
    }

    private static void ValidateItemPlans(IReadOnlyList<ProcessingItemPlan> items)
    {
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Processing item plans cannot contain null values.", nameof(items));
        }

        var itemIds = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);

        if (itemIds.Count != items.Count)
        {
            throw new ArgumentException("Processing item IDs must be unique.", nameof(items));
        }

        foreach (var item in items)
        {
            var unknownDependency = item.RequiredItemIds.FirstOrDefault(
                dependencyId => !itemIds.Contains(dependencyId));

            if (unknownDependency is not null)
            {
                throw new ArgumentException(
                    $"Processing item '{item.ItemId}' references unknown required item '{unknownDependency}'.",
                    nameof(items));
            }
        }
    }

    private void ClearCompletedOperation(CooperativeProcessingOperation completedOperation)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeOperation, completedOperation))
            {
                _activeOperation = null;
            }
        }
    }
}

public sealed class CooperativeProcessingOperation
{
    private const string CancelledBeforeStartFailureCode = "operation-cancelled-before-start";

    private readonly string _operationName;
    private readonly string? _processingStage;
    private readonly IProcessingHistoryRecorder _historyRecorder;
    private readonly Action<CooperativeProcessingOperation> _onCompleted;
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, TrackedItem> _items;
    private readonly TaskCompletionSource<OperationCompletion> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cancellationAccepted;
    private bool _terminal;
    private int _inFlightCount;

    internal CooperativeProcessingOperation(
        OperationCorrelation correlation,
        string operationName,
        string? processingStage,
        IEnumerable<ProcessingItemPlan> items,
        IProcessingHistoryRecorder historyRecorder,
        Action<CooperativeProcessingOperation> onCompleted)
    {
        Correlation = correlation;
        _operationName = operationName;
        _processingStage = processingStage;
        _historyRecorder = historyRecorder;
        _onCompleted = onCompleted;
        _items = items.ToDictionary(
            item => item.ItemId,
            item => new TrackedItem(item),
            StringComparer.Ordinal);
    }

    public OperationCorrelation Correlation { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool IsCancellationAccepted
    {
        get
        {
            lock (_stateGate)
            {
                return _cancellationAccepted;
            }
        }
    }

    public bool TryStartItem(string itemId, out ProcessingItemExecution? execution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        lock (_stateGate)
        {
            if (_cancellationAccepted || _terminal)
            {
                execution = null;
                return false;
            }

            if (!_items.TryGetValue(itemId, out var item))
            {
                throw new ArgumentException(
                    $"Processing item '{itemId}' is not part of this operation.",
                    nameof(itemId));
            }

            if (item.State != TrackedItemState.Pending
                || item.Plan.RequiredItemIds.Any(
                    dependencyId =>
                        _items[dependencyId].State != TrackedItemState.ProcessedSuccessfully))
            {
                execution = null;
                return false;
            }

            item.State = TrackedItemState.InFlight;
            _inFlightCount++;
            execution = new ProcessingItemExecution(this, itemId);
            return true;
        }
    }

    internal OperationCancellationRequest RequestCancellation()
    {
        lock (_stateGate)
        {
            if (_cancellationAccepted)
            {
                return new OperationCancellationRequest(
                    OperationCancellationRequestStatus.AlreadyAccepted,
                    _completion.Task);
            }

            _cancellationAccepted = true;

            foreach (var item in _items.Values.Where(
                         candidate => candidate.State == TrackedItemState.Pending))
            {
                item.State = TrackedItemState.Unprocessed;
                item.FailureCode = CancelledBeforeStartFailureCode;
            }
        }

        _cancellation.Cancel();
        FinalizeCancellationIfReady();
        return new OperationCancellationRequest(
            OperationCancellationRequestStatus.Accepted,
            _completion.Task);
    }

    internal void CompleteItem(
        string itemId,
        OperationItemState state,
        string? failureCode)
    {
        lock (_stateGate)
        {
            if (!_items.TryGetValue(itemId, out var item)
                || item.State != TrackedItemState.InFlight)
            {
                throw new InvalidOperationException(
                    $"Processing item '{itemId}' is not currently in flight.");
            }

            item.State = state switch
            {
                OperationItemState.ProcessedSuccessfully =>
                    TrackedItemState.ProcessedSuccessfully,
                OperationItemState.Failed => TrackedItemState.Failed,
                OperationItemState.Unprocessed => TrackedItemState.Unprocessed,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
            };
            item.FailureCode = failureCode;
            _inFlightCount--;
        }

        FinalizeCancellationIfReady();
    }

    private void FinalizeCancellationIfReady()
    {
        OperationCompletion? completion = null;

        lock (_stateGate)
        {
            if (!_cancellationAccepted || _inFlightCount != 0 || _terminal)
            {
                return;
            }

            _terminal = true;
            completion = OperationCompletion.FromTerminalOutcome(
                Correlation,
                OperationOutcome.Cancelled,
                _items.Values.Select(CreateItemStatus).ToArray());
        }

        _historyRecorder.RecordAttempt(
            ProcessingAttemptRecord.FromCompletion(
                _operationName,
                _processingStage,
                DateTimeOffset.UtcNow,
                completion));
        _onCompleted(this);
        _completion.TrySetResult(completion);
    }

    private static OperationItemStatus CreateItemStatus(TrackedItem item)
    {
        return item.State switch
        {
            TrackedItemState.ProcessedSuccessfully =>
                OperationItemStatus.ProcessedSuccessfully(
                    item.Plan.ItemId,
                    item.Plan.RequiredItemIds),
            TrackedItemState.Failed => OperationItemStatus.Failed(
                item.Plan.ItemId,
                item.FailureCode,
                item.Plan.RequiredItemIds),
            TrackedItemState.Unprocessed => OperationItemStatus.Unprocessed(
                item.Plan.ItemId,
                item.FailureCode,
                item.Plan.RequiredItemIds),
            _ => throw new InvalidOperationException(
                $"Processing item '{item.Plan.ItemId}' has not reached a safe boundary.")
        };
    }

    private enum TrackedItemState
    {
        Pending,
        InFlight,
        ProcessedSuccessfully,
        Failed,
        Unprocessed
    }

    private sealed class TrackedItem(ProcessingItemPlan plan)
    {
        public ProcessingItemPlan Plan { get; } = plan;

        public TrackedItemState State { get; set; }

        public string? FailureCode { get; set; }
    }
}

public sealed class ProcessingItemExecution : IDisposable
{
    private const string StoppedBeforeCommitFailureCode = "operation-cancelled-before-commit";

    private readonly CooperativeProcessingOperation _operation;
    private readonly string _itemId;
    private int _completed;

    internal ProcessingItemExecution(
        CooperativeProcessingOperation operation,
        string itemId)
    {
        _operation = operation;
        _itemId = itemId;
    }

    public CancellationToken CancellationToken => _operation.CancellationToken;

    public void CommitCompletedResult()
    {
        Complete(OperationItemState.ProcessedSuccessfully, failureCode: null);
    }

    public void RecordFailure(string? failureCode = null)
    {
        if (failureCode is not null && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "A processing failure code cannot be empty or whitespace.",
                nameof(failureCode));
        }

        Complete(OperationItemState.Failed, failureCode);
    }

    public void StopBeforeCommit(string failureCode = StoppedBeforeCommitFailureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        Complete(OperationItemState.Unprocessed, failureCode);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _completed) == 0)
        {
            StopBeforeCommit();
        }
    }

    private void Complete(OperationItemState state, string? failureCode)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"Processing item '{_itemId}' has already reached its safe boundary.");
        }

        _operation.CompleteItem(_itemId, state, failureCode);
    }
}
