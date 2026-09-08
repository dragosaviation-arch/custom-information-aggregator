using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CIA.ProcessingHost.Operations;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class CooperativeCancellationTests
{
    [TestMethod]
    public async Task AcceptedCancellationStopsNewStartsAndWaitsForInFlightSafeBoundary()
    {
        var history = new RecordingHistoryRecorder();
        var cancellation = new CooperativeOperationCancellation(history);
        var correlation = OperationCorrelation.CreateNew();
        var operation = cancellation.BeginOperation(
            correlation,
            "Discovery",
            "Source interpretation",
            [new ProcessingItemPlan("source-a"), new ProcessingItemPlan("source-b")]);
        Assert.IsTrue(operation.TryStartItem("source-a", out var inFlight));
        Assert.IsNotNull(inFlight);

        var request = cancellation.RequestCancellation(correlation.OperationId);

        Assert.IsTrue(request.Accepted);
        Assert.AreEqual(OperationCancellationRequestStatus.Accepted, request.Status);
        Assert.IsTrue(operation.IsCancellationAccepted);
        Assert.IsTrue(inFlight.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(request.Completion!.IsCompleted);
        Assert.IsFalse(operation.TryStartItem("source-b", out var rejectedStart));
        Assert.IsNull(rejectedStart);

        inFlight.CommitCompletedResult();
        var completion = await request.Completion;

        Assert.AreEqual(OperationOutcome.Cancelled, completion.Outcome);
        Assert.AreEqual(
            OperationItemState.ProcessedSuccessfully,
            completion.Items.Single(item => item.ItemId == "source-a").State);
        Assert.AreEqual(
            OperationItemState.Unprocessed,
            completion.Items.Single(item => item.ItemId == "source-b").State);
        Assert.AreEqual(
            "operation-cancelled-before-start",
            completion.Items.Single(item => item.ItemId == "source-b").FailureCode);
        Assert.IsTrue(completion.CanRetainResultFor("source-a"));
        Assert.IsNull(cancellation.ActiveOperationId);
    }

    [TestMethod]
    public async Task CompletedResultSurvivesWhileInFlightWorkStopsBeforeCommit()
    {
        var history = new RecordingHistoryRecorder();
        var cancellation = new CooperativeOperationCancellation(history);
        var correlation = OperationCorrelation.CreateNew();
        var operation = cancellation.BeginOperation(
            correlation,
            "DatabaseBuild",
            "Database commit",
            [new ProcessingItemPlan("committed"), new ProcessingItemPlan("provisional")]);
        Assert.IsTrue(operation.TryStartItem("committed", out var committed));
        Assert.IsTrue(operation.TryStartItem("provisional", out var provisional));
        committed!.CommitCompletedResult();

        var request = cancellation.RequestCancellation(correlation.OperationId);
        Assert.IsFalse(request.Completion!.IsCompleted);
        provisional!.StopBeforeCommit();

        var completion = await request.Completion;

        Assert.IsTrue(completion.CanRetainResultFor("committed"));
        Assert.IsFalse(completion.CanRetainResultFor("provisional"));
        Assert.AreEqual(
            "operation-cancelled-before-commit",
            completion.Items.Single(item => item.ItemId == "provisional").FailureCode);
    }

    [TestMethod]
    public async Task CancelledCompletionIsRetainedAsTruthfulCorrelatedHistory()
    {
        var history = new RecordingHistoryRecorder();
        var cancellation = new CooperativeOperationCancellation(history);
        var correlation = OperationCorrelation.CreateNew();
        var operation = cancellation.BeginOperation(
            correlation,
            "Extraction",
            "Artifact extraction",
            [new ProcessingItemPlan("archive-a"), new ProcessingItemPlan("archive-b")]);
        Assert.IsTrue(operation.TryStartItem("archive-a", out var inFlight));

        var request = cancellation.RequestCancellation(correlation.OperationId);
        inFlight!.RecordFailure("archive-read-failed");
        var completion = await request.Completion!;

        Assert.HasCount(1, history.Attempts);
        var attempt = history.Attempts[0];
        Assert.AreEqual(correlation, attempt.Correlation);
        Assert.AreEqual(OperationOutcome.Cancelled, attempt.TerminalOutcome);
        Assert.AreSame(completion, attempt.Completion);
        Assert.AreEqual(
            OperationItemState.Failed,
            attempt.Items.Single(item => item.ItemId == "archive-a").State);
        Assert.AreEqual(
            OperationItemState.Unprocessed,
            attempt.Items.Single(item => item.ItemId == "archive-b").State);
        Assert.IsEmpty(history.Diagnostics);
    }

    [TestMethod]
    public async Task CancellationIsIdempotentAndRejectsMismatchedOperationIdentity()
    {
        var cancellation = new CooperativeOperationCancellation(
            new RecordingHistoryRecorder());
        var correlation = OperationCorrelation.CreateNew();
        var operation = cancellation.BeginOperation(
            correlation,
            "Export",
            processingStage: null,
            [new ProcessingItemPlan("export-a")]);
        Assert.IsTrue(operation.TryStartItem("export-a", out var inFlight));

        var mismatch = cancellation.RequestCancellation(OperationId.CreateNew());
        var accepted = cancellation.RequestCancellation(correlation.OperationId);
        var repeated = cancellation.RequestCancellation(correlation.OperationId);

        Assert.IsFalse(mismatch.Accepted);
        Assert.AreEqual(OperationCancellationRequestStatus.OperationMismatch, mismatch.Status);
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual(OperationCancellationRequestStatus.AlreadyAccepted, repeated.Status);
        Assert.AreSame(accepted.Completion, repeated.Completion);
        inFlight!.StopBeforeCommit();
        Assert.AreEqual(OperationOutcome.Cancelled, (await accepted.Completion!).Outcome);
    }

    [TestMethod]
    public void AtomicCommitBoundaryCannotReportCancellationAsAccepted()
    {
        var cancellation = new CooperativeOperationCancellation(
            new RecordingHistoryRecorder());
        var correlation = OperationCorrelation.CreateNew();
        var operation = cancellation.BeginOperation(
            correlation,
            "DatabaseBuild",
            "Database publication",
            [new ProcessingItemPlan("publication")]);
        Assert.IsTrue(operation.TryStartItem("publication", out var publication));

        Assert.IsTrue(operation.TryEnterNonCancellableCommitBoundary());
        var request = cancellation.RequestCancellation(correlation.OperationId);

        Assert.IsFalse(request.Accepted);
        Assert.AreEqual(
            OperationCancellationRequestStatus.CommitBoundaryReached,
            request.Status);
        publication!.CommitCompletedResult();
        var completion = operation.Complete();
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, completion.Outcome);
    }

    private sealed class RecordingHistoryRecorder : IProcessingHistoryRecorder
    {
        public List<ProcessingAttemptRecord> Attempts { get; } = [];

        public List<ProcessingDiagnosticRecord> Diagnostics { get; } = [];

        public void RecordAttempt(ProcessingAttemptRecord record)
        {
            Attempts.Add(record);
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
            Diagnostics.Add(record);
        }
    }
}
