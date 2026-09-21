using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CIA.Desktop.Hosting;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class InterruptedOperationRecoveryTests
{
    [TestMethod]
    public async Task AcceptedOperationPersistsStartBeforeControlReturnsForDispatch()
    {
        var history = new MutableHistoryStore();
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(supervisor, history);
        workflow.RecordSourceSelectionChanged(true);

        var accepted = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsTrue(accepted.Accepted);
        Assert.HasCount(1, history.Starts);
        Assert.AreEqual(accepted.Operation, history.Starts[0].Correlation);
        Assert.AreEqual(nameof(WorkflowOperationKind.Discovery), history.Starts[0].OperationName);
        Assert.IsTrue(history.Starts[0].RecordedAtUtc >= accepted.Operation!.InitiatedAtUtc);
    }

    [TestMethod]
    [DataRow(OperationOutcome.CompletedSuccessfully)]
    [DataRow(OperationOutcome.CompletedWithIssues)]
    [DataRow(OperationOutcome.Failed)]
    [DataRow(OperationOutcome.Cancelled)]
    public async Task ControlledTerminalOutcomeIsNeverReclassifiedAfterRestart(
        OperationOutcome outcome)
    {
        var history = new MutableHistoryStore();
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(supervisor, history);
        workflow.RecordSourceSelectionChanged(true);
        var accepted = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        workflow.CompleteOperation(accepted.Operation!.OperationId, outcome);
        var expectedAttempt = history.Attempts.Single();

        using var restartedWorkflow = new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            restartedWorkflow);
        recovery.ReconcileStartupHistory();

        Assert.HasCount(1, history.Attempts);
        Assert.AreSame(expectedAttempt, history.Attempts[0]);
        Assert.AreEqual(outcome, history.Attempts[0].TerminalOutcome);
    }

    [TestMethod]
    public async Task OrphanStartIsReconciledExactlyOnceAcrossRepeatedRestarts()
    {
        var history = new MutableHistoryStore();
        var supervisor = new StubProcessingHostSupervisor();
        using (var workflow = new ApplicationWorkflowCoordinator(supervisor, history))
        {
            workflow.RecordSourceSelectionChanged(true);
            await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        }

        for (var restart = 0; restart < 3; restart++)
        {
            using var workflow = new ApplicationWorkflowCoordinator(
                new StubProcessingHostSupervisor(),
                history);
            using var recovery = new InterruptedOperationRecoveryCoordinator(
                history,
                history,
                workflow);

            recovery.ReconcileStartupHistory();
        }

        Assert.HasCount(1, history.Attempts);
        Assert.AreEqual(
            OperationOutcome.InterruptedIncomplete,
            history.Attempts[0].TerminalOutcome);
        Assert.HasCount(1, history.Diagnostics);
        Assert.AreEqual("operation-interrupted", history.Diagnostics[0].FailureCode);
    }

    [TestMethod]
    public async Task LiveHostLossRecordsOneInterruptionAndNeverReplaysOrRewritesIt()
    {
        var history = new MutableHistoryStore();
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(supervisor, history);
        workflow.RecordSourceSelectionChanged(true);
        await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);

        supervisor.Publish(ProcessingHostLifecycleState.Recreating);
        supervisor.Publish(ProcessingHostLifecycleState.Faulted);
        supervisor.Publish(ProcessingHostLifecycleState.Ready);

        Assert.HasCount(1, history.Attempts);
        Assert.AreEqual(
            OperationOutcome.InterruptedIncomplete,
            history.Attempts[0].TerminalOutcome);
        Assert.AreEqual(
            WorkflowOperationState.InterruptedIncomplete,
            workflow.Current.LatestOperation?.State);
        Assert.IsNull(workflow.Current.ActiveOperation);
        Assert.AreEqual(1, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public async Task GracefulShutdownInterruptsAnActiveOperationBeforeHostDisposal()
    {
        var history = new MutableHistoryStore();
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(supervisor, history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);
        workflow.RecordSourceSelectionChanged(true);
        var accepted = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);

        await recovery.StopAsync(CancellationToken.None);

        Assert.IsNull(workflow.Current.ActiveOperation);
        Assert.AreEqual(
            WorkflowOperationState.InterruptedIncomplete,
            workflow.Current.LatestOperation?.State);
        Assert.HasCount(1, history.Attempts);
        Assert.AreEqual(accepted.Operation, history.Attempts[0].Correlation);
        Assert.AreEqual(
            OperationOutcome.InterruptedIncomplete,
            history.Attempts[0].TerminalOutcome);
    }

    [TestMethod]
    public void InterruptedDiscoveryAvailabilityTracksTheNormalSourcePrerequisite()
    {
        var start = CreateStart(WorkflowOperationKind.Discovery);
        var history = new MutableHistoryStore(starts: [start]);
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(
            supervisor,
            history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);

        recovery.ReconcileStartupHistory();

        Assert.IsNotNull(recovery.Current);
        Assert.AreEqual(start.Correlation.OperationId, recovery.Current.OriginalOperationId);
        Assert.AreEqual(WorkflowOperationKind.Discovery, recovery.Current.OperationKind);
        Assert.IsFalse(recovery.Current.ReinitiationPrerequisitesSatisfied);
        Assert.AreEqual(
            workflow.EvaluateOperationPrerequisites(WorkflowOperationKind.Discovery)
                .Rejection?.Reason,
            recovery.Current.UnavailableReason);
        Assert.IsNull(workflow.Current.ActiveOperation);
        Assert.AreEqual(0, supervisor.EnsureAvailableCallCount);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Extraction);

        workflow.RecordSourceSelectionChanged(true);

        Assert.IsTrue(recovery.Current.ReinitiationPrerequisitesSatisfied);
        Assert.IsNull(recovery.Current.UnavailableReason);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    [TestMethod]
    [DataRow(WorkflowOperationKind.DatabaseBuild)]
    [DataRow(WorkflowOperationKind.Extraction)]
    [DataRow(WorkflowOperationKind.Export)]
    [DataRow(WorkflowOperationKind.WorkingStateSave)]
    [DataRow(WorkflowOperationKind.WorkingStateRestore)]
    public async Task RecoveryAvailabilityUsesTheExistingOperationPrerequisites(
        WorkflowOperationKind operationKind)
    {
        var start = CreateStart(operationKind);
        var history = new MutableHistoryStore(starts: [start]);
        using var workflow = new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history);
        await EstablishPrerequisitesAsync(workflow, operationKind);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);

        recovery.ReconcileStartupHistory();

        var normalEligibility = workflow.EvaluateOperationPrerequisites(operationKind);
        Assert.AreEqual(
            normalEligibility.Accepted,
            recovery.Current?.ReinitiationPrerequisitesSatisfied);
        Assert.AreEqual(
            normalEligibility.Rejection?.Reason,
            recovery.Current?.UnavailableReason);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    [TestMethod]
    [DataRow(WorkflowOperationKind.DatabaseBuild)]
    [DataRow(WorkflowOperationKind.Extraction)]
    [DataRow(WorkflowOperationKind.Export)]
    public void DownstreamRecoveryIsUnavailableWithoutItsNormalCurrentArtifact(
        WorkflowOperationKind operationKind)
    {
        var history = new MutableHistoryStore(starts: [CreateStart(operationKind)]);
        var supervisor = new StubProcessingHostSupervisor();
        using var workflow = new ApplicationWorkflowCoordinator(supervisor, history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);

        recovery.ReconcileStartupHistory();

        var normalEligibility = workflow.EvaluateOperationPrerequisites(operationKind);
        Assert.IsFalse(normalEligibility.Accepted);
        Assert.IsFalse(recovery.Current?.ReinitiationPrerequisitesSatisfied);
        Assert.AreEqual(
            normalEligibility.Rejection?.Reason,
            recovery.Current?.UnavailableReason);
        Assert.IsNull(workflow.Current.ActiveOperation);
        Assert.AreEqual(0, supervisor.EnsureAvailableCallCount);
    }

    [TestMethod]
    public void UnknownOperationRemainsVisibleWithoutInventingAWorkflowKind()
    {
        var correlation = CreateCorrelation();
        var history = new MutableHistoryStore(
            starts:
            [
                new ProcessingOperationStartRecord(
                    correlation,
                    "LegacyOperation",
                    correlation.InitiatedAtUtc)
            ]);
        using var workflow = new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);

        recovery.ReconcileStartupHistory();

        Assert.AreEqual("LegacyOperation", recovery.Current?.OperationName);
        Assert.IsNull(recovery.Current?.OperationKind);
        Assert.IsFalse(recovery.Current?.ReinitiationPrerequisitesSatisfied);
        Assert.IsNotNull(recovery.Current?.UnavailableReason);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    [TestMethod]
    public void IncompleteHistoryEvidenceDoesNotManufactureAnInterruption()
    {
        var history = new MutableHistoryStore(starts: [CreateStart(WorkflowOperationKind.Discovery)])
        {
            RecoveryEvidenceComplete = false
        };
        using var workflow = new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);

        recovery.ReconcileStartupHistory();

        Assert.IsEmpty(history.Attempts);
        Assert.IsEmpty(history.Diagnostics);
        Assert.IsNull(recovery.Current);
    }

    [TestMethod]
    public async Task LaterUserReinitiationUsesNormalFlowAndANewCorrelation()
    {
        var start = CreateStart(WorkflowOperationKind.Discovery);
        var history = new MutableHistoryStore(starts: [start]);
        using var workflow = new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            workflow);
        recovery.ReconcileStartupHistory();
        workflow.RecordSourceSelectionChanged(true);

        var reinitiated = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);

        Assert.IsTrue(reinitiated.Accepted);
        Assert.AreNotEqual(start.Correlation.OperationId, reinitiated.Operation?.OperationId);
        Assert.HasCount(2, history.Starts);
    }

    private static ProcessingOperationStartRecord CreateStart(
        WorkflowOperationKind operationKind)
    {
        var correlation = CreateCorrelation();
        return new ProcessingOperationStartRecord(
            correlation,
            operationKind.ToString(),
            correlation.InitiatedAtUtc);
    }

    private static OperationCorrelation CreateCorrelation()
    {
        return OperationCorrelation.CreateNew(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private static async Task EstablishPrerequisitesAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind)
    {
        if (operationKind is WorkflowOperationKind.WorkingStateSave
            or WorkflowOperationKind.WorkingStateRestore)
        {
            return;
        }

        workflow.RecordSourceSelectionChanged(true);
        await CompleteAsync(workflow, WorkflowOperationKind.Discovery);
        if (operationKind == WorkflowOperationKind.DatabaseBuild)
        {
            return;
        }

        await CompleteAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        if (operationKind == WorkflowOperationKind.Extraction)
        {
            return;
        }

        await CompleteAsync(workflow, WorkflowOperationKind.Extraction);
    }

    private static async Task CompleteAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind)
    {
        var started = await workflow.BeginOperationAsync(operationKind);
        Assert.IsTrue(started.Accepted);
        Assert.IsTrue(workflow.CompleteOperation(
            started.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
    }

    private sealed class MutableHistoryStore :
        IProcessingHistoryReader,
        IProcessingHistoryRecorder
    {
        public MutableHistoryStore(
            IReadOnlyList<ProcessingOperationStartRecord>? starts = null)
        {
            if (starts is not null)
            {
                Starts.AddRange(starts);
            }
        }

        public List<ProcessingOperationStartRecord> Starts { get; } = [];

        public List<ProcessingAttemptRecord> Attempts { get; } = [];

        public List<ProcessingDiagnosticRecord> Diagnostics { get; } = [];

        public bool RecoveryEvidenceComplete { get; init; } = true;

        public ProcessingHistorySnapshot Read()
        {
            return new ProcessingHistorySnapshot(
                Attempts.ToArray(),
                Diagnostics.ToArray(),
                Starts.ToArray(),
                ReadProblem: null)
            {
                RecoveryEvidenceComplete = RecoveryEvidenceComplete
            };
        }

        public void RecordStart(ProcessingOperationStartRecord record)
        {
            Starts.Add(record);
        }

        public void RecordAttempt(ProcessingAttemptRecord record)
        {
            Attempts.Add(record);
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
            Diagnostics.Add(record);
        }
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; private set; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public int EnsureAvailableCallCount { get; private set; }

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged;

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailableCallCount++;
            return Task.FromResult(Current);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(ProcessingHostLifecycleState.Stopped);
            return Task.CompletedTask;
        }

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public void Publish(ProcessingHostLifecycleState state)
        {
            Current = new ProcessingHostLifecycleSnapshot(
                state,
                HostDesired: state != ProcessingHostLifecycleState.Stopped,
                ProcessId: state == ProcessingHostLifecycleState.Ready ? 1234 : null,
                FailureCode: state == ProcessingHostLifecycleState.Faulted
                    ? "test-failure"
                    : null);
            StateChanged?.Invoke(this, Current);
        }
    }
}
