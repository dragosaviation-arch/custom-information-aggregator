using System.Collections.Concurrent;
using CIA.Contracts.Operations;
using CIA.Core;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class GlobalStatusViewModelTests
{
    [TestMethod]
    public async Task HostAvailabilityAndWorkloadStateRemainDistinct()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Ready);
        using var coordinator = CreateCoordinator(supervisor);
        using var status = new GlobalStatusViewModel(supervisor, coordinator);

        Assert.AreEqual("Available", status.HostStatusText);
        Assert.AreEqual("No operation", status.OperationStatusText);

        coordinator.RecordSourceSelectionChanged(true);
        var operation = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsTrue(operation.Accepted);
        Assert.AreEqual("Available", status.HostStatusText);
        Assert.AreEqual("Discovery — Active", status.OperationStatusText);
    }

    [TestMethod]
    public async Task ActiveCompletedAndFailedStatesAreCommunicatedExplicitly()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Ready);
        using var coordinator = CreateCoordinator(supervisor);
        using var status = new GlobalStatusViewModel(supervisor, coordinator);
        coordinator.RecordSourceSelectionChanged(true);

        var completedOperation = await coordinator.BeginOperationAsync(
            WorkflowOperationKind.Discovery);
        Assert.AreEqual("Discovery — Active", status.OperationStatusText);

        coordinator.CompleteOperation(
            completedOperation.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);
        Assert.AreEqual("Discovery — Completed successfully", status.OperationStatusText);

        var failedOperation = await coordinator.BeginOperationAsync(
            WorkflowOperationKind.Discovery);
        coordinator.CompleteOperation(
            failedOperation.Operation!.OperationId,
            OperationOutcome.Failed);

        Assert.AreEqual(
            "Discovery — Failed. The operation did not complete.",
            status.OperationStatusText);
    }

    [TestMethod]
    public async Task GlobalStatusPersistsAcrossEveryPrincipalWorkspace()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Ready);
        using var coordinator = CreateCoordinator(supervisor);
        using var status = new GlobalStatusViewModel(supervisor, coordinator);
        var shell = new MainWindowViewModel(new ApplicationSession());
        coordinator.RecordSourceSelectionChanged(true);
        await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        foreach (var workspace in shell.Workspaces)
        {
            shell.SelectedWorkspace = workspace;

            Assert.AreEqual("Available", status.HostStatusText);
            Assert.AreEqual("Discovery — Active", status.OperationStatusText);
        }

        CollectionAssert.AreEqual(
            new[] { "Load", "Discovery", "Database", "Settings" },
            shell.Workspaces.Select(workspace => workspace.Title).ToArray());
    }

    [TestMethod]
    public async Task HostLossInterruptsWorkloadWithoutRetryAndHostRecoveryRemainsDistinct()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Ready);
        using var coordinator = CreateCoordinator(supervisor);
        using var status = new GlobalStatusViewModel(supervisor, coordinator);
        coordinator.RecordSourceSelectionChanged(true);
        await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        supervisor.Publish(ProcessingHostLifecycleState.Recreating);

        Assert.AreEqual("Recreating", status.HostStatusText);
        Assert.AreEqual(
            "Discovery — Interrupted / incomplete. The Processing Host connection was lost.",
            status.OperationStatusText);
        Assert.IsNull(coordinator.Current.ActiveOperation);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);

        supervisor.Publish(ProcessingHostLifecycleState.Ready);

        Assert.AreEqual("Available", status.HostStatusText);
        Assert.AreEqual(
            WorkflowOperationState.InterruptedIncomplete,
            coordinator.Current.LatestOperation?.State);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task HostRecreationDuringAvailabilityDoesNotAcceptInterruptedWorkload()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Stopped)
        {
            RecreateDuringAvailability = true
        };
        using var coordinator = CreateCoordinator(supervisor);
        using var status = new GlobalStatusViewModel(supervisor, coordinator);
        coordinator.RecordSourceSelectionChanged(true);

        var result = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ProcessingHostUnavailable, result.Rejection?.Code);
        Assert.AreEqual("Available", status.HostStatusText);
        Assert.AreEqual(
            "Discovery — Interrupted / incomplete. The Processing Host connection was lost.",
            status.OperationStatusText);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task BackgroundHostEventQueuesStatusUpdateWithoutBlockingNavigation()
    {
        var supervisor = new StubProcessingHostSupervisor(ProcessingHostLifecycleState.Stopped);
        using var coordinator = CreateCoordinator(supervisor);
        var uiContext = new QueuedSynchronizationContext();
        var originalContext = SynchronizationContext.Current;
        GlobalStatusViewModel status;

        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            status = new GlobalStatusViewModel(supervisor, coordinator);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        using (status)
        {
            var shell = new MainWindowViewModel(new ApplicationSession());
            var publish = Task.Run(() => supervisor.Publish(ProcessingHostLifecycleState.Ready));

            await publish.WaitAsync(TimeSpan.FromSeconds(1));
            shell.SelectedWorkspace = shell.Workspaces.Single(
                workspace => workspace.Area == WorkspaceArea.Settings);

            Assert.AreEqual(WorkspaceArea.Settings, shell.SelectedWorkspace.Area);
            Assert.AreEqual("Stopped", status.HostStatusText);

            uiContext.Drain();

            Assert.AreEqual("Available", status.HostStatusText);
        }
    }

    private static ApplicationWorkflowCoordinator CreateCoordinator(
        IProcessingHostSupervisor supervisor)
    {
        return new ApplicationWorkflowCoordinator(
            supervisor,
            new RecordingProcessingHistoryRecorder());
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public StubProcessingHostSupervisor(ProcessingHostLifecycleState initialState)
        {
            Current = CreateSnapshot(initialState);
        }

        public ProcessingHostLifecycleSnapshot Current { get; private set; }

        public int EnsureAvailableCallCount { get; private set; }

        public bool RecreateDuringAvailability { get; init; }

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged;

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailableCallCount++;

            if (RecreateDuringAvailability)
            {
                Publish(ProcessingHostLifecycleState.Recreating);
                Publish(ProcessingHostLifecycleState.Ready);
            }

            return Task.FromResult(Current);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(ProcessingHostLifecycleState.Stopped);
            return Task.CompletedTask;
        }

        public void Publish(ProcessingHostLifecycleState state)
        {
            Current = CreateSnapshot(state);
            StateChanged?.Invoke(this, Current);
        }

        private static ProcessingHostLifecycleSnapshot CreateSnapshot(
            ProcessingHostLifecycleState state)
        {
            return new ProcessingHostLifecycleSnapshot(
                state,
                HostDesired: state != ProcessingHostLifecycleState.Stopped,
                ProcessId: state == ProcessingHostLifecycleState.Ready ? 1234 : null,
                FailureCode: state == ProcessingHostLifecycleState.Faulted
                    ? "TestFailure"
                    : null);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _work.Enqueue((d, state));
        }

        public void Drain()
        {
            while (_work.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }
}
