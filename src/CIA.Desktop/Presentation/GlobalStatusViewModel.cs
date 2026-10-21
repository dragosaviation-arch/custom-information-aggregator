using CIA.Desktop.Hosting;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class GlobalStatusViewModel : ObservableObject, IDisposable
{
    private readonly IProcessingHostSupervisor _processingHostSupervisor;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly GlobalOperationProgress _operationProgress;
    private readonly IExternalLinkLauncher _externalLinkLauncher;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private string _hostStatusText;
    private string _operationStatusText;
    private GlobalOperationProgressSnapshot _progress;
    private readonly AsyncRelayCommand _cancelActiveOperationCommand;
    private readonly RelayCommand _openOriginalRepositoryCommand;
    private int _disposed;

    public GlobalStatusViewModel(
        IProcessingHostSupervisor processingHostSupervisor,
        IApplicationWorkflowCoordinator workflowCoordinator,
        GlobalOperationProgress? operationProgress = null,
        IExternalLinkLauncher? externalLinkLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(processingHostSupervisor);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _processingHostSupervisor = processingHostSupervisor;
        _workflowCoordinator = workflowCoordinator;
        _operationProgress = operationProgress ?? new GlobalOperationProgress();
        _externalLinkLauncher = externalLinkLauncher ?? new WindowsExternalLinkLauncher();
        _uiSynchronizationContext = SynchronizationContext.Current;
        _hostStatusText = FormatHostStatus(_processingHostSupervisor.Current);
        _operationStatusText = FormatOperationStatus(_workflowCoordinator.Current.LatestOperation);
        _progress = _operationProgress.Current;
        _cancelActiveOperationCommand = new AsyncRelayCommand(
            RequestCancellationAsync,
            CanRequestCancellation);
        _openOriginalRepositoryCommand = new RelayCommand(OpenOriginalRepository);

        _processingHostSupervisor.StateChanged += OnProcessingHostStateChanged;
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
        _operationProgress.Changed += OnOperationProgressChanged;
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

    public bool IsProgressVisible => IsOperationActive;

    public bool IsOperationActive =>
        _progress.IsActive || _workflowCoordinator.Current.ActiveOperation is not null;

    public bool HasDeterminateProgress => _progress.HasDeterminateProgress;

    public bool IsProgressIndeterminate => IsOperationActive && !HasDeterminateProgress;

    public bool IsOriginalAttributionVisible => !IsOperationActive;

    public string OriginalAuthorIdentityText =>
        $"{CiaProductIdentity.OriginalAuthor.Name} · "
        + CiaProductIdentity.OriginalAuthor.ProfessionalIdentification;

    public string OriginalAuthorDisplayUrl => CiaProductIdentity.OriginalAuthor.DisplayUrl;

    public string OriginalAuthorAccessibleName =>
        $"Original CIA author: {OriginalAuthorIdentityText}, "
        + $"{OriginalAuthorDisplayUrl}. Open the original CIA repository.";

    public double ProgressMaximum => _progress.TotalWork ?? 1;

    public double ProgressValue => _progress.CompletedWork ?? 0;

    public string ProgressPercentText => _progress.HasDeterminateProgress
        ? $"{Math.Clamp(ProgressValue / ProgressMaximum, 0, 1):P0}"
        : string.Empty;

    public IAsyncRelayCommand CancelActiveOperationCommand => _cancelActiveOperationCommand;

    public IRelayCommand OpenOriginalRepositoryCommand => _openOriginalRepositoryCommand;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _processingHostSupervisor.StateChanged -= OnProcessingHostStateChanged;
        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
        _operationProgress.Changed -= OnOperationProgressChanged;
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
                if (!_progress.IsActive
                    || state.LatestOperation?.State == WorkflowOperationState.Cancelling)
                {
                    OperationStatusText = FormatOperationStatus(state.LatestOperation);
                }
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(IsOperationActive));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
                OnPropertyChanged(nameof(IsOriginalAttributionVisible));
                _cancelActiveOperationCommand.NotifyCanExecuteChanged();
            });
    }

    private void OnOperationProgressChanged(
        object? sender,
        GlobalOperationProgressSnapshot progress)
    {
        DispatchToUi(() =>
        {
            _progress = progress;
            OperationStatusText = $"{progress.OperationName} - {progress.StageText}";
            OnPropertyChanged(nameof(IsProgressVisible));
            OnPropertyChanged(nameof(IsOperationActive));
            OnPropertyChanged(nameof(HasDeterminateProgress));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(IsOriginalAttributionVisible));
            OnPropertyChanged(nameof(ProgressMaximum));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ProgressPercentText));
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

    private void OpenOriginalRepository()
    {
        try
        {
            _externalLinkLauncher.TryOpen(CiaProductIdentity.OriginalAuthor.Destination);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
        {
            // Browser availability must not affect CIA operation or attribution visibility.
        }
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
            WorkflowOperationKind.WorkingStateSave => "Save state",
            WorkflowOperationKind.WorkingStateRestore => "Restore state",
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
