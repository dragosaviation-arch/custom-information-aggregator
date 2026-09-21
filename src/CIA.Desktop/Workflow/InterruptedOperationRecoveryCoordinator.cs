using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace CIA.Desktop.Workflow;

public sealed class InterruptedOperationRecoveryCoordinator :
    IInterruptedOperationRecovery,
    IHostedService,
    IDisposable
{
    internal const string StartupInterruptionContext =
        "CIA restarted before the operation reached a controlled terminal state.";

    private readonly IProcessingHistoryReader _historyReader;
    private readonly IProcessingHistoryRecorder _historyRecorder;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly object _stateGate = new();
    private readonly HashSet<OperationId> _reconciledOperationIds = [];
    private readonly HashSet<OperationId> _diagnosedOperationIds = [];
    private RecoverySubject? _recoverySubject;
    private InterruptedOperationRecoveryAvailability? _current;
    private int _disposed;

    public InterruptedOperationRecoveryCoordinator(
        IProcessingHistoryReader historyReader,
        IProcessingHistoryRecorder historyRecorder,
        IApplicationWorkflowCoordinator workflowCoordinator)
    {
        ArgumentNullException.ThrowIfNull(historyReader);
        ArgumentNullException.ThrowIfNull(historyRecorder);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _historyReader = historyReader;
        _historyRecorder = historyRecorder;
        _workflowCoordinator = workflowCoordinator;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
    }

    public InterruptedOperationRecoveryAvailability? Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<InterruptedOperationRecoveryAvailability?>? AvailabilityChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReconcileStartupHistory();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _workflowCoordinator.InterruptActiveOperationForShutdown();
        return Task.CompletedTask;
    }

    public void ReconcileStartupHistory()
    {
        var snapshot = _historyReader.Read();
        var terminalOperationIds = snapshot.Attempts
            .Select(attempt => attempt.Correlation.OperationId)
            .ToHashSet();
        ProcessingAttemptRecord? newestReconciledAttempt = null;

        lock (_stateGate)
        {
            _reconciledOperationIds.UnionWith(terminalOperationIds);
            _diagnosedOperationIds.UnionWith(snapshot.Diagnostics
                .Where(diagnostic => diagnostic.TerminalOutcome
                    == OperationOutcome.InterruptedIncomplete)
                .Select(diagnostic => diagnostic.Correlation.OperationId));
        }

        if (snapshot.RecoveryEvidenceComplete)
        {
            foreach (var start in snapshot.Starts
                         .GroupBy(item => item.Correlation.OperationId)
                         .Select(group => group.OrderByDescending(item => item.RecordedAtUtc).First())
                         .Where(start => !terminalOperationIds.Contains(
                             start.Correlation.OperationId))
                         .OrderBy(start => start.RecordedAtUtc))
            {
                lock (_stateGate)
                {
                    if (!_reconciledOperationIds.Add(start.Correlation.OperationId))
                    {
                        continue;
                    }
                }

                var recordedAtUtc = UtcNowNotBefore(start.Correlation.InitiatedAtUtc);
                var attempt = ProcessingAttemptRecord.FromTerminalOutcome(
                    start.Correlation,
                    start.OperationName,
                    finalStage: null,
                    recordedAtUtc,
                    OperationOutcome.InterruptedIncomplete);
                _historyRecorder.RecordAttempt(attempt);
                RecordInterruptionDiagnostic(
                    start.Correlation,
                    start.OperationName,
                    recordedAtUtc,
                    StartupInterruptionContext);
                lock (_stateGate)
                {
                    _diagnosedOperationIds.Add(start.Correlation.OperationId);
                }
                newestReconciledAttempt = attempt;
                terminalOperationIds.Add(start.Correlation.OperationId);
            }
        }

        EnsureInterruptedAttemptsHaveDiagnostics(snapshot);

        var interruptedAttempts = snapshot.Attempts
            .Where(attempt => attempt.TerminalOutcome
                == OperationOutcome.InterruptedIncomplete);
        if (newestReconciledAttempt is not null)
        {
            interruptedAttempts = interruptedAttempts.Append(newestReconciledAttempt);
        }

        var newestInterrupted = interruptedAttempts
            .OrderByDescending(attempt => attempt.RecordedAtUtc)
            .FirstOrDefault();

        if (newestInterrupted is null)
        {
            return;
        }

        var context = newestInterrupted == newestReconciledAttempt
            ? StartupInterruptionContext
            : "Processing history records an interrupted or incomplete operation.";
        SetRecoverySubject(
            new RecoverySubject(
                newestInterrupted.Correlation,
                newestInterrupted.OperationName,
                newestInterrupted.RecordedAtUtc,
                context));

        if (newestInterrupted == newestReconciledAttempt
            && TryParseOperationKind(newestInterrupted.OperationName, out var operationKind))
        {
            _workflowCoordinator.RestoreInterruptedOperationStatus(
                operationKind,
                newestInterrupted.Correlation,
                StartupInterruptionContext);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
    }

    private void EnsureInterruptedAttemptsHaveDiagnostics(
        ProcessingHistorySnapshot snapshot)
    {
        foreach (var attempt in snapshot.Attempts.Where(attempt =>
                     attempt.TerminalOutcome == OperationOutcome.InterruptedIncomplete))
        {
            lock (_stateGate)
            {
                if (!_diagnosedOperationIds.Add(attempt.Correlation.OperationId))
                {
                    continue;
                }
            }

            RecordInterruptionDiagnostic(
                attempt.Correlation,
                attempt.OperationName,
                UtcNowNotBefore(attempt.Correlation.InitiatedAtUtc),
                "The operation was interrupted before completion.");
        }
    }

    private void RecordInterruptionDiagnostic(
        OperationCorrelation correlation,
        string operationName,
        DateTimeOffset recordedAtUtc,
        string description)
    {
        _historyRecorder.RecordDiagnostic(
            new ProcessingDiagnosticRecord(
                DiagnosticRecordId.CreateNew(),
                correlation,
                operationName,
                processingStage: null,
                sourceId: null,
                itemId: null,
                itemState: null,
                recordedAtUtc,
                OperationOutcome.InterruptedIncomplete,
                description,
                "operation-interrupted",
                technicalDetail: null));
    }

    private void OnWorkflowStateChanged(
        object? sender,
        WorkflowStateSnapshot state)
    {
        if (state.LatestOperation is
            {
                State: WorkflowOperationState.InterruptedIncomplete
            } interrupted)
        {
            RecoverySubject? existing;
            lock (_stateGate)
            {
                existing = _recoverySubject;
            }

            SetRecoverySubject(
                existing?.Correlation.OperationId == interrupted.Correlation.OperationId
                    ? existing with
                    {
                        Context = interrupted.Detail ?? existing.Context
                    }
                    : new RecoverySubject(
                        interrupted.Correlation,
                        interrupted.Kind.ToString(),
                        UtcNowNotBefore(interrupted.Correlation.InitiatedAtUtc),
                        interrupted.Detail
                            ?? "The operation was interrupted before completion."));
            return;
        }

        RefreshAvailability();
    }

    private void SetRecoverySubject(RecoverySubject subject)
    {
        lock (_stateGate)
        {
            _recoverySubject = subject;
        }

        RefreshAvailability();
    }

    private void RefreshAvailability()
    {
        RecoverySubject? subject;
        lock (_stateGate)
        {
            subject = _recoverySubject;
        }

        if (subject is null)
        {
            return;
        }

        WorkflowOperationKind? operationKind = null;
        WorkflowCommandResult? prerequisiteResult = null;
        if (TryParseOperationKind(subject.OperationName, out var parsedKind))
        {
            operationKind = parsedKind;
            prerequisiteResult = _workflowCoordinator.EvaluateOperationPrerequisites(parsedKind);
        }

        var current = new InterruptedOperationRecoveryAvailability(
            subject.Correlation,
            subject.OperationName,
            operationKind,
            subject.InterruptedAtUtc,
            subject.Context,
            prerequisiteResult?.Accepted == true,
            operationKind is null
                ? "The recorded operation kind is not recognized by this version of CIA."
                : prerequisiteResult!.Rejection?.Reason);

        lock (_stateGate)
        {
            if (_current == current)
            {
                return;
            }

            _current = current;
        }

        AvailabilityChanged?.Invoke(this, current);
    }

    private static bool TryParseOperationKind(
        string operationName,
        out WorkflowOperationKind operationKind)
    {
        return Enum.TryParse(operationName, ignoreCase: false, out operationKind)
            && Enum.IsDefined(operationKind);
    }

    private static DateTimeOffset UtcNowNotBefore(DateTimeOffset minimum)
    {
        var now = DateTimeOffset.UtcNow;
        return now < minimum ? minimum : now;
    }

    private sealed record RecoverySubject(
        OperationCorrelation Correlation,
        string OperationName,
        DateTimeOffset InterruptedAtUtc,
        string Context);
}
