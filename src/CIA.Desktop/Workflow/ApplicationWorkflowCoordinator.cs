using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Workflow;

public sealed class ApplicationWorkflowCoordinator :
    IApplicationWorkflowCoordinator,
    IDisposable
{
    private readonly IProcessingHostSupervisor _processingHostSupervisor;
    private readonly ILogger<ApplicationWorkflowCoordinator> _logger;
    private readonly object _stateGate = new();
    private WorkflowStateSnapshot _current = WorkflowStateSnapshot.Empty;
    private int _disposed;

    public ApplicationWorkflowCoordinator(
        IProcessingHostSupervisor processingHostSupervisor,
        ILogger<ApplicationWorkflowCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(processingHostSupervisor);
        ArgumentNullException.ThrowIfNull(logger);

        _processingHostSupervisor = processingHostSupervisor;
        _logger = logger;
        _processingHostSupervisor.StateChanged += OnProcessingHostStateChanged;
    }

    public WorkflowStateSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<WorkflowStateSnapshot>? StateChanged;

    public WorkflowCommandResult RecordSourceSelectionChanged(bool hasValidSourceSelection)
    {
        WorkflowStateSnapshot changedState;

        lock (_stateGate)
        {
            var conflict = RejectIfOperationActive();
            if (conflict is not null)
            {
                return conflict;
            }

            _current = _current with
            {
                HasValidSourceSelection = hasValidSourceSelection,
                Discovery = MakeStaleIfAvailable(_current.Discovery),
                Database = MakeStaleIfAvailable(_current.Database),
                Extraction = MakeStaleIfAvailable(_current.Extraction)
            };
            changedState = _current;
        }

        PublishStateChanged(changedState);
        return WorkflowCommandResult.Accept();
    }

    public WorkflowCommandResult RecordDiscoveryConfigurationChanged()
    {
        WorkflowStateSnapshot changedState;

        lock (_stateGate)
        {
            var conflict = RejectIfOperationActive();
            if (conflict is not null)
            {
                return conflict;
            }

            if (_current.Discovery != WorkflowArtifactStatus.Current)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.DiscoveryNotCurrent,
                    "Discovery configuration can change only when Discovery is current.");
            }

            _current = _current with
            {
                Database = MakeStaleIfAvailable(_current.Database),
                Extraction = MakeStaleIfAvailable(_current.Extraction)
            };
            changedState = _current;
        }

        PublishStateChanged(changedState);
        return WorkflowCommandResult.Accept();
    }

    public async Task<WorkflowCommandResult> BeginOperationAsync(
        WorkflowOperationKind operationKind,
        CancellationToken cancellationToken = default)
    {
        OperationCorrelation correlation;
        WorkflowStateSnapshot changedState;

        lock (_stateGate)
        {
            var rejection = ValidateOperation(operationKind);
            if (rejection is not null)
            {
                return rejection;
            }

            correlation = OperationCorrelation.CreateNew();
            _current = _current with
            {
                ActiveOperation = new ActiveWorkflowOperation(operationKind, correlation),
                LatestOperation = new WorkflowOperationStatus(
                    operationKind,
                    correlation,
                    WorkflowOperationState.Active,
                    Detail: null)
            };
            changedState = _current;
        }

        PublishStateChanged(changedState);

        try
        {
            var host = await _processingHostSupervisor
                .EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);

            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                MarkActiveOperationTerminal(
                    correlation.OperationId,
                    WorkflowOperationState.Failed,
                    "The Processing Host is unavailable.");
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.ProcessingHostUnavailable,
                    "The Processing Host is not available for this operation.");
            }

            if (!IsActiveOperation(correlation.OperationId))
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.ProcessingHostUnavailable,
                    "The operation was interrupted before it could start.");
            }

            return WorkflowCommandResult.Accept(correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkActiveOperationTerminal(
                correlation.OperationId,
                WorkflowOperationState.Cancelled,
                "Operation start was cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            MarkActiveOperationTerminal(
                correlation.OperationId,
                WorkflowOperationState.Failed,
                "The Processing Host is unavailable.");
            _logger.LogWarning(
                exception,
                "The Processing Host was unavailable for {OperationKind}.",
                operationKind);

            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.ProcessingHostUnavailable,
                "The Processing Host is not available for this operation.");
        }
    }

    public WorkflowCommandResult CompleteOperation(
        OperationId operationId,
        OperationOutcome outcome)
    {
        WorkflowStateSnapshot changedState;
        ActiveWorkflowOperation activeOperation;

        lock (_stateGate)
        {
            if (!Enum.IsDefined(outcome))
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.InvalidOutcome,
                    "The operation outcome is not supported.");
            }

            var currentActiveOperation = _current.ActiveOperation;
            if (currentActiveOperation is null
                || currentActiveOperation.Correlation.OperationId != operationId)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationMismatch,
                    "The completion does not match the active operation.");
            }

            activeOperation = currentActiveOperation;
            _current = ApplyCompletion(_current, activeOperation.Kind, outcome) with
            {
                ActiveOperation = null,
                LatestOperation = CreateTerminalStatus(activeOperation, outcome)
            };
            changedState = _current;
        }

        PublishStateChanged(changedState);
        return WorkflowCommandResult.Accept(activeOperation.Correlation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _processingHostSupervisor.StateChanged -= OnProcessingHostStateChanged;
    }

    private WorkflowCommandResult? ValidateOperation(WorkflowOperationKind operationKind)
    {
        var conflict = RejectIfOperationActive();
        if (conflict is not null)
        {
            return conflict;
        }

        return operationKind switch
        {
            WorkflowOperationKind.Discovery when !_current.HasValidSourceSelection =>
                WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.MissingSourceSelection,
                    "Discovery requires a valid active source selection."),
            WorkflowOperationKind.Discovery => null,
            WorkflowOperationKind.DatabaseBuild when
                _current.Discovery != WorkflowArtifactStatus.Current =>
                WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.DiscoveryNotCurrent,
                    "Database generation requires current Discovery results."),
            WorkflowOperationKind.DatabaseBuild => null,
            WorkflowOperationKind.Extraction when
                _current.Database != WorkflowArtifactStatus.Current =>
                WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.DatabaseNotCurrent,
                    "Extraction requires a current Database generation."),
            WorkflowOperationKind.Extraction => null,
            WorkflowOperationKind.Export when
                _current.Extraction != WorkflowArtifactStatus.Current =>
                WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.ExtractionNotCurrent,
                    "Export requires current extracted results."),
            WorkflowOperationKind.Export => null,
            _ => WorkflowCommandResult.Reject(
                WorkflowRejectionCode.UnsupportedOperation,
                "The requested workflow operation is not supported.")
        };
    }

    private WorkflowCommandResult? RejectIfOperationActive()
    {
        return _current.ActiveOperation is null
            ? null
            : WorkflowCommandResult.Reject(
                WorkflowRejectionCode.ConflictingOperation,
                $"{_current.ActiveOperation.Kind} is already active.");
    }

    private bool IsActiveOperation(OperationId operationId)
    {
        lock (_stateGate)
        {
            return _current.ActiveOperation?.Correlation.OperationId == operationId;
        }
    }

    private void OnProcessingHostStateChanged(
        object? sender,
        ProcessingHostLifecycleSnapshot hostState)
    {
        switch (hostState.State)
        {
            case ProcessingHostLifecycleState.Recreating:
                MarkActiveOperationTerminal(
                    operationId: null,
                    WorkflowOperationState.InterruptedIncomplete,
                    "The Processing Host connection was lost.");
                break;
            case ProcessingHostLifecycleState.Faulted:
                MarkActiveOperationTerminal(
                    operationId: null,
                    WorkflowOperationState.Failed,
                    "The Processing Host is unavailable.");
                break;
        }
    }

    private void MarkActiveOperationTerminal(
        OperationId? operationId,
        WorkflowOperationState terminalState,
        string detail)
    {
        WorkflowStateSnapshot? changedState = null;

        lock (_stateGate)
        {
            var activeOperation = _current.ActiveOperation;
            if (activeOperation is null
                || (operationId is not null
                    && activeOperation.Correlation.OperationId != operationId.Value))
            {
                return;
            }

            _current = _current with
            {
                ActiveOperation = null,
                LatestOperation = new WorkflowOperationStatus(
                    activeOperation.Kind,
                    activeOperation.Correlation,
                    terminalState,
                    detail)
            };
            changedState = _current;
        }

        PublishStateChanged(changedState);
    }

    private void PublishStateChanged(WorkflowStateSnapshot state)
    {
        StateChanged?.Invoke(this, state);
    }

    private static WorkflowOperationStatus CreateTerminalStatus(
        ActiveWorkflowOperation activeOperation,
        OperationOutcome outcome)
    {
        return outcome switch
        {
            OperationOutcome.CompletedSuccessfully => new WorkflowOperationStatus(
                activeOperation.Kind,
                activeOperation.Correlation,
                WorkflowOperationState.CompletedSuccessfully,
                Detail: null),
            OperationOutcome.CompletedWithIssues => new WorkflowOperationStatus(
                activeOperation.Kind,
                activeOperation.Correlation,
                WorkflowOperationState.CompletedWithIssues,
                "The operation completed with issues."),
            OperationOutcome.Failed => new WorkflowOperationStatus(
                activeOperation.Kind,
                activeOperation.Correlation,
                WorkflowOperationState.Failed,
                "The operation did not complete."),
            OperationOutcome.Cancelled => new WorkflowOperationStatus(
                activeOperation.Kind,
                activeOperation.Correlation,
                WorkflowOperationState.Cancelled,
                "The operation was cancelled."),
            OperationOutcome.InterruptedIncomplete => new WorkflowOperationStatus(
                activeOperation.Kind,
                activeOperation.Correlation,
                WorkflowOperationState.InterruptedIncomplete,
                "The operation was interrupted before completion."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }

    private static WorkflowStateSnapshot ApplyCompletion(
        WorkflowStateSnapshot current,
        WorkflowOperationKind operationKind,
        OperationOutcome outcome)
    {
        if (outcome is not (
            OperationOutcome.CompletedSuccessfully or
            OperationOutcome.CompletedWithIssues))
        {
            return current;
        }

        return operationKind switch
        {
            WorkflowOperationKind.Discovery => current with
            {
                Discovery = WorkflowArtifactStatus.Current,
                Database = MakeStaleIfAvailable(current.Database),
                Extraction = MakeStaleIfAvailable(current.Extraction)
            },
            WorkflowOperationKind.DatabaseBuild => current with
            {
                Database = WorkflowArtifactStatus.Current,
                Extraction = MakeStaleIfAvailable(current.Extraction)
            },
            WorkflowOperationKind.Extraction => current with
            {
                Extraction = WorkflowArtifactStatus.Current
            },
            WorkflowOperationKind.Export => current,
            _ => current
        };
    }

    private static WorkflowArtifactStatus MakeStaleIfAvailable(
        WorkflowArtifactStatus status)
    {
        return status == WorkflowArtifactStatus.Unavailable
            ? WorkflowArtifactStatus.Unavailable
            : WorkflowArtifactStatus.Stale;
    }
}
