using CIA.Desktop.Hosting;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class GlobalStatusViewModel : ObservableObject, IDisposable
{
    private readonly IProcessingHostSupervisor _processingHostSupervisor;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private string _hostStatusText;
    private string _operationStatusText;
    private readonly AsyncRelayCommand _cancelActiveOperationCommand;
    private int _disposed;

    public GlobalStatusViewModel(
        IProcessingHostSupervisor processingHostSupervisor,
        IApplicationWorkflowCoordinator workflowCoordinator)
    {
        ArgumentNullException.ThrowIfNull(processingHostSupervisor);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _processingHostSupervisor = processingHostSupervisor;
        _workflowCoordinator = workflowCoordinator;
        _uiSynchronizationContext = SynchronizationContext.Current;
        _hostStatusText = FormatHostStatus(_processingHostSupervisor.Current);
        _operationStatusText = FormatOperationStatus(_workflowCoordinator.Current.LatestOperation);
        _cancelActiveOperationCommand = new AsyncRelayCommand(
            RequestCancellationAsync,
            CanRequestCancellation);

        _processingHostSupervisor.StateChanged += OnProcessingHostStateChanged;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
    }

    public string HostStatusText
    {
        get => _hostStatusText;
        private set => SetProperty(ref _hostStatusText, value);
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        private set => SetProperty(ref _operationStatusText, value);
    }

    public IAsyncRelayCommand CancelActiveOperationCommand => _cancelActiveOperationCommand;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _processingHostSupervisor.StateChanged -= OnProcessingHostStateChanged;
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
    }

    private void OnProcessingHostStateChanged(
        object? sender,
        ProcessingHostLifecycleSnapshot state)
    {
        DispatchToUi(() => HostStatusText = FormatHostStatus(state));
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot state)
    {
        DispatchToUi(
            () =>
            {
                OperationStatusText = FormatOperationStatus(state.LatestOperation);
                _cancelActiveOperationCommand.NotifyCanExecuteChanged();
            });
    }

    private bool CanRequestCancellation()
    {
        var current = _workflowCoordinator.Current;
        return current.ActiveOperation is not null
            && current.LatestOperation is
            {
                State: WorkflowOperationState.Active
            } latestOperation
            && latestOperation.Correlation.OperationId
                == current.ActiveOperation.Correlation.OperationId;
    }

    private async Task RequestCancellationAsync()
    {
        await _workflowCoordinator.RequestCancellationAsync().ConfigureAwait(true);
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

    private static string FormatHostStatus(ProcessingHostLifecycleSnapshot host)
    {
        return host.State switch
        {
            ProcessingHostLifecycleState.Stopped => "Stopped",
            ProcessingHostLifecycleState.Starting => "Starting",
            ProcessingHostLifecycleState.Ready => "Available",
            ProcessingHostLifecycleState.Recreating => "Recreating",
            ProcessingHostLifecycleState.Stopping => "Stopping",
            ProcessingHostLifecycleState.Faulted => "Unavailable",
            _ => "Unavailable"
        };
    }

    private static string FormatOperationStatus(WorkflowOperationStatus? operation)
    {
        if (operation is null)
        {
            return "No operation";
        }

        var operationName = operation.Kind switch
        {
            WorkflowOperationKind.Discovery => "Discovery",
            WorkflowOperationKind.DatabaseBuild => "Database build",
            WorkflowOperationKind.Extraction => "Extraction",
            WorkflowOperationKind.Export => "Export",
            _ => "Operation"
        };
        var state = operation.State switch
        {
            WorkflowOperationState.Active => "Active",
            WorkflowOperationState.Cancelling => "Cancelling",
            WorkflowOperationState.CompletedSuccessfully => "Completed successfully",
            WorkflowOperationState.CompletedWithIssues => "Completed with issues",
            WorkflowOperationState.Failed => "Failed",
            WorkflowOperationState.Cancelled => "Cancelled",
            WorkflowOperationState.InterruptedIncomplete => "Interrupted / incomplete",
            _ => "Unknown"
        };
        var detail = string.IsNullOrWhiteSpace(operation.Detail)
            ? string.Empty
            : $". {operation.Detail}";

        return $"{operationName} — {state}{detail}";
    }
}
