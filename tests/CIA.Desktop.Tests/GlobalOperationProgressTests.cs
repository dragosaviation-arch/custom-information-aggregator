using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class GlobalOperationProgressTests
{
    [TestMethod]
    public void DeterminateProgressIsMonotonicAndOnlySuccessReachesTotal()
    {
        var progress = new GlobalOperationProgress();
        var updateId = progress.Begin("Discovery", "Starting", 0, 10);

        progress.Report(updateId, "Processing", 6, 10);
        progress.Report(updateId, "Late lower report", 4, 10);

        Assert.AreEqual(6d, progress.Current.CompletedWork);
        progress.Complete(updateId, GlobalOperationProgressState.Failed, "failed");
        Assert.AreEqual(6d, progress.Current.CompletedWork);
        Assert.AreEqual(GlobalOperationProgressState.Failed, progress.Current.State);

        var successfulUpdate = progress.Begin("Discovery", "Starting", 0, 10);
        progress.Report(successfulUpdate, "Processing", 7, 10);
        progress.Complete(
            successfulUpdate,
            GlobalOperationProgressState.CompletedSuccessfully,
            "completed successfully");

        Assert.AreEqual(10d, progress.Current.CompletedWork);
        Assert.AreEqual(GlobalOperationProgressState.CompletedSuccessfully, progress.Current.State);
    }

    [TestMethod]
    public void UnknownWorkShowsStageWithoutInventingPercentageAndStaleUpdatesAreIgnored()
    {
        var progress = new GlobalOperationProgress();
        var first = progress.Begin("Database build", "Building datasets");

        Assert.IsTrue(progress.Current.IsActive);
        Assert.IsFalse(progress.Current.HasDeterminateProgress);
        Assert.IsNull(progress.Current.CompletedWork);
        Assert.IsNull(progress.Current.TotalWork);

        var second = progress.Begin("Exporting to Excel", "Publishing workbook");
        progress.Report(first, "Stale stage", 100, 100);

        Assert.AreEqual(second, progress.Current.UpdateId);
        Assert.AreEqual("Publishing workbook", progress.Current.StageText);
        Assert.IsNull(progress.Current.TotalWork);
    }

    [TestMethod]
    public async Task ActiveStageSurvivesWorkflowBeginAndTerminalActionRemainsVisible()
    {
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var progress = new GlobalOperationProgress();
        using var status = new GlobalStatusViewModel(
            new ReadyProcessingHostSupervisor(),
            workflow,
            progress);
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(discovery.Accepted);
        Assert.IsTrue(workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        var updateId = progress.Begin("Database build", "Building hierarchy-aware datasets");

        var operation = await workflow.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);

        Assert.IsTrue(operation.Accepted);
        Assert.AreEqual(
            "Database build - Building hierarchy-aware datasets",
            status.OperationStatusText);
        Assert.IsTrue(workflow.CompleteOperation(
            operation.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        progress.Complete(
            updateId,
            GlobalOperationProgressState.CompletedSuccessfully,
            "completed successfully");
        Assert.AreEqual("Database build - completed successfully", status.OperationStatusText);
        Assert.IsFalse(status.IsProgressVisible);
    }

    private sealed class ReadyProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1,
            FailureCode: null);

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
