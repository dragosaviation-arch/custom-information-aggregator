using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Workflow;

public sealed class ApplicationWorkflowCoordinator : IApplicationWorkflowCoordinator
{
    private readonly IProcessingHostSupervisor _processingHostSupervisor;
    private readonly ILogger<ApplicationWorkflowCoordinator> _logger;
    private readonly object _stateGate = new();
    private WorkflowStateSnapshot _current = WorkflowStateSnapshot.Empty;

    public ApplicationWorkflowCoordinator(
        IProcessingHostSupervisor processingHostSupervisor,
        ILogger<ApplicationWorkflowCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(processingHostSupervisor);
        ArgumentNullException.ThrowIfNull(logger);

        _processingHostSupervisor = processingHostSupervisor;
        _logger = logger;
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

    public WorkflowCommandResult RecordSourceSelectionChanged(bool hasValidSourceSelection)
    {
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

            return WorkflowCommandResult.Accept();
        }
    }

    public WorkflowCommandResult RecordDiscoveryConfigurationChanged()
    {
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

            return WorkflowCommandResult.Accept();
        }
    }

    public async Task<WorkflowCommandResult> BeginOperationAsync(
        WorkflowOperationKind operationKind,
        CancellationToken cancellationToken = default)
    {
        OperationCorrelation correlation;

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
                ActiveOperation = new ActiveWorkflowOperation(operationKind, correlation)
            };
        }

        try
        {
            var host = await _processingHostSupervisor
                .EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);

            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                ClearActiveOperation(correlation.OperationId);
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.ProcessingHostUnavailable,
                    "The Processing Host is not available for this operation.");
            }

            return WorkflowCommandResult.Accept(correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearActiveOperation(correlation.OperationId);
            throw;
        }
        catch (Exception exception)
        {
            ClearActiveOperation(correlation.OperationId);
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
        lock (_stateGate)
        {
            if (!Enum.IsDefined(outcome))
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.InvalidOutcome,
                    "The operation outcome is not supported.");
            }

            var activeOperation = _current.ActiveOperation;
            if (activeOperation is null || activeOperation.Correlation.OperationId != operationId)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationMismatch,
                    "The completion does not match the active operation.");
            }

            _current = ApplyCompletion(_current, activeOperation.Kind, outcome) with
            {
                ActiveOperation = null
            };

            return WorkflowCommandResult.Accept(activeOperation.Correlation);
        }
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

    private void ClearActiveOperation(OperationId operationId)
    {
        lock (_stateGate)
        {
            if (_current.ActiveOperation?.Correlation.OperationId == operationId)
            {
                _current = _current with { ActiveOperation = null };
            }
        }
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
