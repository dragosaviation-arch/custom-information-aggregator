using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ApplicationWorkflowCoordinatorTests
{
    [TestMethod]
    public async Task ValidIntentIsCoordinatedThroughProcessingHostAvailability()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);

        Assert.IsTrue(coordinator.RecordSourceSelectionChanged(true).Accepted);

        var result = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(result.Operation);
        Assert.AreEqual(7, result.Operation.OperationId.Value.Version);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);
        Assert.AreEqual(result.Operation, coordinator.Current.ActiveOperation?.Correlation);
    }

    [TestMethod]
    public async Task MissingPrerequisiteIsRejectedBeforeHostAvailabilityIsRequested()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);

        var result = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.MissingSourceSelection, result.Rejection?.Code);
        Assert.IsNull(result.Operation);
        Assert.AreEqual(0, supervisor.EnsureAvailableCallCount);
        Assert.IsNull(coordinator.Current.ActiveOperation);
    }

    [TestMethod]
    public async Task ConflictingMutationIsRejectedWhileAnOperationIsActive()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);
        coordinator.RecordSourceSelectionChanged(true);
        var discovery = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        var conflictingOperation = await coordinator.BeginOperationAsync(
            WorkflowOperationKind.DatabaseBuild);
        var conflictingSourceChange = coordinator.RecordSourceSelectionChanged(false);

        Assert.IsTrue(discovery.Accepted);
        Assert.AreEqual(
            WorkflowRejectionCode.ConflictingOperation,
            conflictingOperation.Rejection?.Code);
        Assert.AreEqual(
            WorkflowRejectionCode.ConflictingOperation,
            conflictingSourceChange.Rejection?.Code);
        Assert.IsTrue(coordinator.Current.HasValidSourceSelection);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task SuccessfulWorkflowTracksCurrentAndCascadingStaleState()
    {
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor());
        coordinator.RecordSourceSelectionChanged(true);

        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Discovery);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.DatabaseBuild);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Extraction);

        Assert.AreEqual(WorkflowArtifactStatus.Current, coordinator.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Current, coordinator.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Current, coordinator.Current.Extraction);

        var sourceChange = coordinator.RecordSourceSelectionChanged(true);

        Assert.IsTrue(sourceChange.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Extraction);
    }

    [TestMethod]
    public async Task DiscoveryConfigurationChangeMakesDownstreamArtifactsStale()
    {
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor());
        coordinator.RecordSourceSelectionChanged(true);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Discovery);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.DatabaseBuild);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Extraction);

        var result = coordinator.RecordDiscoveryConfigurationChanged();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, coordinator.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Extraction);
    }

    [TestMethod]
    public async Task DownstreamOperationsRequireCurrentUpstreamState()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);
        coordinator.RecordSourceSelectionChanged(true);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Discovery);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.DatabaseBuild);
        coordinator.RecordSourceSelectionChanged(true);

        var database = await coordinator.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);
        var extraction = await coordinator.BeginOperationAsync(WorkflowOperationKind.Extraction);
        var export = await coordinator.BeginOperationAsync(WorkflowOperationKind.Export);

        Assert.AreEqual(WorkflowRejectionCode.DiscoveryNotCurrent, database.Rejection?.Code);
        Assert.AreEqual(WorkflowRejectionCode.DatabaseNotCurrent, extraction.Rejection?.Code);
        Assert.AreEqual(WorkflowRejectionCode.ExtractionNotCurrent, export.Rejection?.Code);
        Assert.AreEqual(2, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task FailedDiscoveryAttemptMarksRetainedStateStaleAndNextAttemptHasNewIdentity()
    {
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor());
        coordinator.RecordSourceSelectionChanged(true);
        await CompleteSuccessfullyAsync(coordinator, WorkflowOperationKind.Discovery);

        var failedAttempt = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
        var failedCompletion = coordinator.CompleteOperation(
            failedAttempt.Operation!.OperationId,
            OperationOutcome.Failed);
        var nextAttempt = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsTrue(failedCompletion.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, coordinator.Current.Discovery);
        Assert.AreNotEqual(
            failedAttempt.Operation.OperationId,
            nextAttempt.Operation?.OperationId);
    }

    [TestMethod]
    public async Task SharedPartialCompletionIsRetainedByWorkflowStatus()
    {
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor());
        coordinator.RecordSourceSelectionChanged(true);
        var begin = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
        var completion = OperationCompletion.FromCompletedItems(
            begin.Operation!,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed")
            ]);

        var result = coordinator.CompleteOperation(completion);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, coordinator.Current.Discovery);
        Assert.AreEqual(
            WorkflowOperationState.CompletedWithIssues,
            coordinator.Current.LatestOperation?.State);
        Assert.AreSame(completion, coordinator.Current.LatestOperation?.Completion);
    }

    [TestMethod]
    public async Task SharedCompletionIsRetainedAsStructuredAttemptHistory()
    {
        var history = new RecordingProcessingHistoryRecorder();
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor(), history);
        coordinator.RecordSourceSelectionChanged(true);
        var begin = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
        var completion = OperationCompletion.FromCompletedItems(
            begin.Operation!,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed")
            ]);

        coordinator.CompleteOperation(completion);

        Assert.HasCount(1, history.Attempts);
        Assert.AreEqual(begin.Operation, history.Attempts[0].Correlation);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, history.Attempts[0].TerminalOutcome);
        Assert.AreSame(completion, history.Attempts[0].Completion);
        Assert.IsEmpty(history.Diagnostics);
    }

    [TestMethod]
    public async Task ReinitiatedOperationAppendsASeparateHistoryAttempt()
    {
        var history = new RecordingProcessingHistoryRecorder();
        var coordinator = CreateCoordinator(new StubProcessingHostSupervisor(), history);
        coordinator.RecordSourceSelectionChanged(true);

        var first = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
        coordinator.CompleteOperation(first.Operation!.OperationId, OperationOutcome.Failed);
        var second = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);
        coordinator.CompleteOperation(
            second.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);

        Assert.HasCount(2, history.Attempts);
        Assert.AreNotEqual(
            history.Attempts[0].Correlation.OperationId,
            history.Attempts[1].Correlation.OperationId);
        Assert.AreEqual(OperationOutcome.Failed, history.Attempts[0].TerminalOutcome);
        Assert.AreEqual(
            OperationOutcome.CompletedSuccessfully,
            history.Attempts[1].TerminalOutcome);
        Assert.HasCount(1, history.Diagnostics);
        Assert.AreEqual(
            history.Attempts[0].Correlation.OperationId,
            history.Diagnostics[0].Correlation.OperationId);
    }

    [TestMethod]
    public async Task HostFailureIsReturnedWithoutExposingTheRawException()
    {
        const string sensitiveMessage = "internal host failure details";
        var supervisor = new StubProcessingHostSupervisor
        {
            AvailabilityException = new InvalidOperationException(sensitiveMessage)
        };
        var coordinator = CreateCoordinator(supervisor);
        coordinator.RecordSourceSelectionChanged(true);

        var result = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ProcessingHostUnavailable, result.Rejection?.Code);
        Assert.IsFalse(result.Rejection!.Reason.Contains(sensitiveMessage, StringComparison.Ordinal));
        Assert.IsNull(coordinator.Current.ActiveOperation);
    }

    [TestMethod]
    public async Task InvalidCommandAndMismatchedCompletionAreRejectedCleanly()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);

        var unsupported = await coordinator.BeginOperationAsync((WorkflowOperationKind)999);
        var mismatch = coordinator.CompleteOperation(
            OperationId.CreateNew(),
            OperationOutcome.CompletedSuccessfully);

        Assert.AreEqual(WorkflowRejectionCode.UnsupportedOperation, unsupported.Rejection?.Code);
        Assert.AreEqual(WorkflowRejectionCode.OperationMismatch, mismatch.Rejection?.Code);
        Assert.AreEqual(0, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task AcceptedCancellationKeepsOperationActiveUntilCancelledCompletionArrives()
    {
        var history = new RecordingProcessingHistoryRecorder();
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor, history);
        coordinator.RecordSourceSelectionChanged(true);
        var begin = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        var cancellation = await coordinator.RequestCancellationAsync();
        var conflictingOperation = await coordinator.BeginOperationAsync(
            WorkflowOperationKind.Discovery);

        Assert.IsTrue(cancellation.Accepted);
        Assert.AreEqual(1, supervisor.CancellationRequestCount);
        Assert.AreEqual(begin.Operation!.OperationId, supervisor.LastCancellationOperationId);
        Assert.AreEqual(WorkflowOperationState.Active, coordinator.Current.LatestOperation?.State);
        Assert.AreEqual(
            WorkflowRejectionCode.ConflictingOperation,
            conflictingOperation.Rejection?.Code);

        var completion = OperationCompletion.FromTerminalOutcome(
            begin.Operation,
            OperationOutcome.Cancelled,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Unprocessed("source-b", "operation-cancelled-before-start")
            ]);
        var completed = coordinator.CompleteOperation(completion);

        Assert.IsTrue(completed.Accepted);
        Assert.IsNull(coordinator.Current.ActiveOperation);
        Assert.AreEqual(WorkflowOperationState.Cancelled, coordinator.Current.LatestOperation?.State);
        Assert.AreSame(completion, coordinator.Current.LatestOperation?.Completion);
        Assert.IsTrue(completion.CanRetainResultFor("source-a"));
        Assert.HasCount(1, history.Attempts);
        Assert.AreEqual(OperationOutcome.Cancelled, history.Attempts[0].TerminalOutcome);
        Assert.AreSame(completion, history.Attempts[0].Completion);
    }

    [TestMethod]
    public async Task CancellationWithoutActiveOperationIsRejectedBeforeHostContact()
    {
        var supervisor = new StubProcessingHostSupervisor();
        var coordinator = CreateCoordinator(supervisor);

        var result = await coordinator.RequestCancellationAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.NoActiveOperation, result.Rejection?.Code);
        Assert.AreEqual(0, supervisor.CancellationRequestCount);
    }

    [TestMethod]
    public async Task RejectedCancellationLeavesTheOperationActive()
    {
        var supervisor = new StubProcessingHostSupervisor
        {
            CancellationAccepted = false
        };
        var coordinator = CreateCoordinator(supervisor);
        coordinator.RecordSourceSelectionChanged(true);
        var begin = await coordinator.BeginOperationAsync(WorkflowOperationKind.Discovery);

        var result = await coordinator.RequestCancellationAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.CancellationRejected, result.Rejection?.Code);
        Assert.AreEqual(begin.Operation, coordinator.Current.ActiveOperation?.Correlation);
        Assert.AreEqual(WorkflowOperationState.Active, coordinator.Current.LatestOperation?.State);
    }

    [TestMethod]
    public void DesktopWorkflowAssemblyDoesNotReferenceProcessingHostImplementation()
    {
        var referencedAssemblies = typeof(ApplicationWorkflowCoordinator)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(referencedAssemblies, "CIA.ProcessingHost");
    }

    private static ApplicationWorkflowCoordinator CreateCoordinator(
        IProcessingHostSupervisor supervisor,
        RecordingProcessingHistoryRecorder? historyRecorder = null)
    {
        return new ApplicationWorkflowCoordinator(
            supervisor,
            historyRecorder ?? new RecordingProcessingHistoryRecorder());
    }

    private static async Task CompleteSuccessfullyAsync(
        IApplicationWorkflowCoordinator coordinator,
        WorkflowOperationKind operationKind)
    {
        var begin = await coordinator.BeginOperationAsync(operationKind);
        Assert.IsTrue(begin.Accepted);
        Assert.IsNotNull(begin.Operation);

        var completion = coordinator.CompleteOperation(
            begin.Operation.OperationId,
            OperationOutcome.CompletedSuccessfully);
        Assert.IsTrue(completion.Accepted);
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; private set; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public int EnsureAvailableCallCount { get; private set; }

        public int CancellationRequestCount { get; private set; }

        public OperationId? LastCancellationOperationId { get; private set; }

        public bool CancellationAccepted { get; init; } = true;

        public Exception? AvailabilityException { get; init; }

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailableCallCount++;

            if (AvailabilityException is not null)
            {
                throw AvailabilityException;
            }

            return Task.FromResult(Current);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = new ProcessingHostLifecycleSnapshot(
                ProcessingHostLifecycleState.Stopped,
                HostDesired: false,
                ProcessId: null,
                FailureCode: null);
            return Task.CompletedTask;
        }

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationRequestCount++;
            LastCancellationOperationId = operationId;
            return Task.FromResult(CancellationAccepted);
        }
    }
}
