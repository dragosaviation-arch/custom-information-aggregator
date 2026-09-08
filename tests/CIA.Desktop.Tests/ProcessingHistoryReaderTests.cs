using System.Text.Json;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ProcessingHistoryReaderTests
{
    [TestMethod]
    public void ReaderConsumesTheCurrentStructuredClefRecorderOutput()
    {
        using var logs = new TemporaryHistoryDirectory();
        var correlation = CreateCorrelation(8);
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed")
            ]);
        var attempt = ProcessingAttemptRecord.FromCompletion(
            "Discovery",
            "Source interpretation",
            correlation.InitiatedAtUtc.AddMinutes(1),
            completion);
        var diagnostic = new ProcessingDiagnosticRecord(
            DiagnosticRecordId.CreateNew(),
            correlation,
            "Discovery",
            "Source interpretation",
            "source-b",
            "member.xml",
            OperationItemState.Failed,
            correlation.InitiatedAtUtc.AddMinutes(2),
            OperationOutcome.CompletedWithIssues,
            "One source item could not be processed.",
            "parse-failed",
            "XmlException at line 42.");

        using var host = DesktopApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logs.Path}"]);
        var recorder = host.Services.GetRequiredService<IProcessingHistoryRecorder>();
        recorder.RecordAttempt(attempt);
        recorder.RecordDiagnostic(diagnostic);

        var snapshot = new ClefProcessingHistoryReader(logs.Path).Read();

        Assert.HasCount(1, snapshot.Attempts);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(correlation.OperationId, snapshot.Attempts[0].Correlation.OperationId);
        Assert.AreEqual(diagnostic.DiagnosticId, snapshot.Diagnostics[0].DiagnosticId);
    }

    [TestMethod]
    public void ReaderLoadsBothStreamsNewestFirstAndPreservesItemAndIssueContext()
    {
        using var logs = new TemporaryHistoryDirectory();
        var correlation = CreateCorrelation(10);
        var otherCorrelation = CreateCorrelation(11);
        var hostAttempt = CreateAttemptEvent(
            correlation,
            correlation.InitiatedAtUtc.AddMinutes(3),
            "Discovery",
            OperationOutcome.CompletedWithIssues,
            [OperationItemStatus.ProcessedSuccessfully("source-a")],
            [OperationItemStatus.Failed("source-b", "parse-failed")],
            [OperationItemStatus.Unprocessed("source-c", "dependency-unavailable")]);
        var uiAttempt = CreateAttemptEvent(
            otherCorrelation,
            otherCorrelation.InitiatedAtUtc.AddMinutes(1),
            "Load",
            OperationOutcome.CompletedSuccessfully,
            [OperationItemStatus.ProcessedSuccessfully("source-d")],
            [],
            []);
        var diagnosticId = DiagnosticRecordId.CreateNew();
        var diagnostic = CreateDiagnosticEvent(
            diagnosticId,
            correlation,
            correlation.InitiatedAtUtc.AddMinutes(2));

        WriteLines(logs.Path, "cia-processing-host-20260908.clef", hostAttempt, hostAttempt);
        WriteLines(logs.Path, "cia-ui-20260908.clef", uiAttempt, diagnostic, diagnostic);

        var snapshot = new ClefProcessingHistoryReader(logs.Path).Read();

        Assert.HasCount(2, snapshot.Attempts);
        Assert.AreEqual("Load", snapshot.Attempts[0].OperationName);
        Assert.AreEqual("Discovery", snapshot.Attempts[1].OperationName);
        Assert.HasCount(3, snapshot.Attempts[1].Items);
        Assert.AreEqual(
            OperationItemState.ProcessedSuccessfully,
            snapshot.Attempts[1].Items.Single(item => item.ItemId == "source-a").State);
        Assert.AreEqual(
            OperationItemState.Failed,
            snapshot.Attempts[1].Items.Single(item => item.ItemId == "source-b").State);
        Assert.AreEqual(
            OperationItemState.Unprocessed,
            snapshot.Attempts[1].Items.Single(item => item.ItemId == "source-c").State);
        Assert.HasCount(1, snapshot.Diagnostics);
        Assert.AreEqual(diagnosticId, snapshot.Diagnostics[0].DiagnosticId);
        Assert.AreEqual("One source item could not be processed.", snapshot.Diagnostics[0].UserFacingDescription);
        Assert.AreEqual("XmlException at line 42.", snapshot.Diagnostics[0].TechnicalDetail);
        Assert.IsNull(snapshot.ReadProblem);
    }

    [TestMethod]
    public void MissingMalformedUnrelatedAndPartialHistoryIsContained()
    {
        using var logs = new TemporaryHistoryDirectory(createDirectory: false);
        var missing = new ClefProcessingHistoryReader(logs.Path).Read();
        Assert.IsEmpty(missing.Attempts);
        Assert.IsEmpty(missing.Diagnostics);
        Assert.IsNull(missing.ReadProblem);

        Directory.CreateDirectory(logs.Path);
        var correlation = CreateCorrelation(9);
        WriteLines(
            logs.Path,
            "cia-ui-20260908.clef",
            "not-json",
            JsonSerializer.Serialize(new { RecordType = "Unrelated", Message = "older event" }),
            CreateAttemptEvent(
                correlation,
                correlation.InitiatedAtUtc.AddMinutes(1),
                "Discovery",
                OperationOutcome.CompletedSuccessfully,
                [OperationItemStatus.ProcessedSuccessfully("source-a")],
                [],
                []),
            "{\"RecordType\":\"ProcessingDiagnostic\"");

        var contained = new ClefProcessingHistoryReader(logs.Path).Read();
        Assert.HasCount(1, contained.Attempts);
        Assert.IsEmpty(contained.Diagnostics);
        Assert.IsNull(contained.ReadProblem);
    }

    [TestMethod]
    public void ReaderKeepsOnlyTheConfiguredNewestRecords()
    {
        using var logs = new TemporaryHistoryDirectory();
        var lines = Enumerable.Range(1, 5)
            .Select(index =>
            {
                var correlation = CreateCorrelation(index);
                return CreateAttemptEvent(
                    correlation,
                    correlation.InitiatedAtUtc.AddMinutes(1),
                    $"Operation {index}",
                    OperationOutcome.CompletedSuccessfully,
                    [OperationItemStatus.ProcessedSuccessfully($"source-{index}")],
                    [],
                    []);
            })
            .ToArray();
        WriteLines(logs.Path, "cia-processing-host-20260908.clef", lines);

        var snapshot = new ClefProcessingHistoryReader(
            logs.Path,
            maximumRecordsPerCategory: 2).Read();

        Assert.HasCount(2, snapshot.Attempts);
        CollectionAssert.AreEqual(
            new[] { "Operation 5", "Operation 4" },
            snapshot.Attempts.Select(attempt => attempt.OperationName).ToArray());
    }

    [TestMethod]
    public void SettingsRefreshExposesAppendedHistoryAndKeepsTechnicalDetailSecondary()
    {
        using var logs = new TemporaryHistoryDirectory();
        var reader = new ClefProcessingHistoryReader(logs.Path);
        var viewModel = new SettingsWorkspaceViewModel(
            reader,
            new SettingsWorkspaceRuntimePaths(
                ApplicationPaths.FromLocalApplicationData(logs.Path),
                logs.Path));
        Assert.IsFalse(viewModel.HasHistory);
        Assert.IsFalse(viewModel.HasIssues);
        Assert.AreEqual(
            "No processing history has been recorded yet.",
            viewModel.HistoryEmptyMessage);
        Assert.AreEqual("No recorded processing issues.", viewModel.IssuesEmptyMessage);

        var correlation = CreateCorrelation(12);
        WriteLines(
            logs.Path,
            "cia-processing-host-20260908.clef",
            CreateAttemptEvent(
                correlation,
                correlation.InitiatedAtUtc.AddMinutes(1),
                "Discovery",
                OperationOutcome.CompletedWithIssues,
                [OperationItemStatus.ProcessedSuccessfully("source-a")],
                [OperationItemStatus.Failed("source-b", "parse-failed")],
                []));
        WriteLines(
            logs.Path,
            "cia-ui-20260908.clef",
            CreateDiagnosticEvent(
                DiagnosticRecordId.CreateNew(),
                correlation,
                correlation.InitiatedAtUtc.AddMinutes(2)));

        viewModel.RefreshCommand.Execute(null);

        Assert.IsTrue(viewModel.HasHistory);
        Assert.IsTrue(viewModel.HasIssues);
        Assert.IsNotNull(viewModel.SelectedAttempt);
        Assert.AreEqual(1, viewModel.SelectedAttempt.SuccessfulCount);
        Assert.AreEqual(1, viewModel.SelectedAttempt.FailedCount);
        Assert.IsNotNull(viewModel.SelectedIssue);
        Assert.AreEqual("One source item could not be processed.", viewModel.SelectedIssue.Description);
        Assert.AreEqual("XmlException at line 42.", viewModel.SelectedIssue.TechnicalDetail);
        Assert.AreNotEqual(viewModel.SelectedIssue.Description, viewModel.SelectedIssue.TechnicalDetail);
        Assert.IsTrue(viewModel.SelectedIssue.HasTechnicalDetails);
        Assert.HasCount(2, viewModel.Entries);
        Assert.IsTrue(viewModel.Entries.Any(
            entry => entry.EntryType == SettingsLogEntryPresentation.ActivityType));
        Assert.IsTrue(viewModel.Entries.Any(
            entry => entry.EntryType == SettingsLogEntryPresentation.IssueType));
    }

    [TestMethod]
    public void FileLevelReadProblemIsReportedWithoutCrashingSettings()
    {
        using var logs = new TemporaryHistoryDirectory();
        var filePath = Path.Combine(logs.Path, "cia-ui-20260908.clef");
        File.WriteAllText(filePath, "locked");

        using var locked = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var snapshot = new ClefProcessingHistoryReader(logs.Path).Read();

        Assert.IsEmpty(snapshot.Attempts);
        Assert.IsEmpty(snapshot.Diagnostics);
        Assert.AreEqual("Some processing history could not be read.", snapshot.ReadProblem);
    }

    private static OperationCorrelation CreateCorrelation(int hour)
    {
        return OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 8, hour, 0, 0, TimeSpan.Zero));
    }

    private static string CreateAttemptEvent(
        OperationCorrelation correlation,
        DateTimeOffset recordedAtUtc,
        string operationName,
        OperationOutcome outcome,
        IReadOnlyList<OperationItemStatus> completedItems,
        IReadOnlyList<OperationItemStatus> failedItems,
        IReadOnlyList<OperationItemStatus> unprocessedItems)
    {
        return JsonSerializer.Serialize(new
        {
            RecordType = "ProcessingAttempt",
            OperationId = correlation.OperationId.ToString(),
            OperationInitiatedAtUtc = correlation.InitiatedAtUtc,
            HistoryRecordedAtUtc = recordedAtUtc,
            ProcessingStage = "Source interpretation",
            OperationName = operationName,
            TerminalOutcome = outcome.ToString(),
            CompletedItems = completedItems,
            FailedItems = failedItems,
            UnprocessedItems = unprocessedItems
        });
    }

    private static string CreateDiagnosticEvent(
        DiagnosticRecordId diagnosticId,
        OperationCorrelation correlation,
        DateTimeOffset recordedAtUtc)
    {
        return JsonSerializer.Serialize(new
        {
            RecordType = "ProcessingDiagnostic",
            DiagnosticId = diagnosticId.ToString(),
            OperationId = correlation.OperationId.ToString(),
            OperationInitiatedAtUtc = correlation.InitiatedAtUtc,
            DiagnosticRecordedAtUtc = recordedAtUtc,
            ProcessingStage = "Source interpretation",
            SourceId = "source-b",
            ItemId = "member.xml",
            ItemState = OperationItemState.Failed.ToString(),
            TerminalOutcome = OperationOutcome.CompletedWithIssues.ToString(),
            OperationName = "Discovery",
            UserFacingDescription = "One source item could not be processed.",
            FailureCode = "parse-failed",
            TechnicalDetail = "XmlException at line 42."
        });
    }

    private static void WriteLines(string directory, string fileName, params string[] lines)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, fileName), lines);
    }

    private sealed class TemporaryHistoryDirectory : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR98.Tests");

        public TemporaryHistoryDirectory(bool createDirectory = true)
        {
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            if (createDirectory)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_testRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);
            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
