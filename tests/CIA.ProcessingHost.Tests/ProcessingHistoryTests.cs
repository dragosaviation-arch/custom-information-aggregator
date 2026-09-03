using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CIA.Desktop.Hosting;
using CIA.ProcessingHost.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ProcessingHistoryTests
{
    [TestMethod]
    public void PartialAttemptRetainsCompletedFailedAndUnprocessedItems()
    {
        var correlation = CreateCorrelation();
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed"),
                OperationItemStatus.Unprocessed("source-c", "dependency-unavailable")
            ]);

        var record = ProcessingAttemptRecord.FromCompletion(
            "Discovery",
            "Source interpretation",
            correlation.InitiatedAtUtc.AddMinutes(1),
            completion);

        Assert.AreEqual(correlation.OperationId, record.Correlation.OperationId);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, record.TerminalOutcome);
        Assert.HasCount(3, record.Items);
        Assert.AreEqual(
            OperationItemState.ProcessedSuccessfully,
            record.Items.Single(item => item.ItemId == "source-a").State);
        Assert.AreEqual(
            "parse-failed",
            record.Items.Single(item => item.ItemId == "source-b").FailureCode);
        Assert.AreEqual(
            OperationItemState.Unprocessed,
            record.Items.Single(item => item.ItemId == "source-c").State);
    }

    [TestMethod]
    public void CancelledAndInterruptedAttemptsRetainKnownItemContext()
    {
        foreach (var outcome in new[]
                 {
                     OperationOutcome.Cancelled,
                     OperationOutcome.InterruptedIncomplete
                 })
        {
            var correlation = CreateCorrelation();
            var completion = OperationCompletion.FromTerminalOutcome(
                correlation,
                outcome,
                [
                    OperationItemStatus.ProcessedSuccessfully("source-a"),
                    OperationItemStatus.Unprocessed("source-b", "operation-stopped")
                ]);
            var record = ProcessingAttemptRecord.FromCompletion(
                "DatabaseBuild",
                finalStage: null,
                correlation.InitiatedAtUtc.AddMinutes(1),
                completion);

            Assert.AreEqual(outcome, record.TerminalOutcome);
            Assert.IsTrue(record.Items.Any(
                item => item.State == OperationItemState.ProcessedSuccessfully));
            Assert.IsTrue(record.Items.Any(
                item => item.State == OperationItemState.Unprocessed));
        }
    }

    [TestMethod]
    public void DiagnosticRetainsApplicableContextAndCreatesConciseIssueSummary()
    {
        var correlation = CreateCorrelation();
        var record = new ProcessingDiagnosticRecord(
            DiagnosticRecordId.CreateNew(),
            correlation,
            "Discovery",
            "Source interpretation",
            "source-17",
            "archive/member.xml",
            OperationItemState.Failed,
            correlation.InitiatedAtUtc.AddSeconds(5),
            OperationOutcome.CompletedWithIssues,
            "One source item could not be processed.",
            "source-parse-failed",
            "XmlException at line 42.");

        var summary = record.ToIssueSummary();

        Assert.AreEqual(record.DiagnosticId, summary.DiagnosticId);
        Assert.AreEqual(correlation.OperationId, summary.OperationId);
        Assert.AreEqual(record.ProcessingStage, summary.ProcessingStage);
        Assert.AreEqual(record.SourceId, summary.SourceId);
        Assert.AreEqual(record.ItemId, summary.ItemId);
        Assert.AreEqual(record.ItemState, summary.ItemState);
        Assert.AreEqual(record.TerminalOutcome, summary.TerminalOutcome);
        Assert.AreEqual(record.UserFacingDescription, summary.Description);
        Assert.IsNull(
            typeof(ProcessingIssueSummary).GetProperty(
                nameof(ProcessingDiagnosticRecord.TechnicalDetail)));
        Assert.IsNull(
            typeof(ProcessingIssueSummary).GetProperty(
                nameof(ProcessingDiagnosticRecord.FailureCode)));
    }

    [TestMethod]
    public void HistoryContractsRoundTripThroughStrictJson()
    {
        var correlation = CreateCorrelation();
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
            correlation.InitiatedAtUtc.AddSeconds(30),
            completion.Outcome,
            "One source item could not be processed.",
            "parse-failed",
            "Parser rejected malformed input.");

        var attemptRoundTrip = JsonSerializer.Deserialize<ProcessingAttemptRecord>(
            JsonSerializer.Serialize(attempt, SerializerOptions),
            SerializerOptions);
        var diagnosticRoundTrip = JsonSerializer.Deserialize<ProcessingDiagnosticRecord>(
            JsonSerializer.Serialize(diagnostic, SerializerOptions),
            SerializerOptions);

        Assert.IsNotNull(attemptRoundTrip);
        Assert.IsNotNull(diagnosticRoundTrip);
        Assert.AreEqual(attempt.Correlation, attemptRoundTrip.Correlation);
        Assert.AreEqual(attempt.TerminalOutcome, attemptRoundTrip.TerminalOutcome);
        Assert.HasCount(2, attemptRoundTrip.Items);
        Assert.AreEqual(diagnostic.DiagnosticId, diagnosticRoundTrip.DiagnosticId);
        Assert.AreEqual(diagnostic.TechnicalDetail, diagnosticRoundTrip.TechnicalDetail);
    }

    [TestMethod]
    public void HistoryContractsRequireUtcTechnicalTimestamps()
    {
        var correlation = CreateCorrelation();
        var localTimestamp = correlation.InitiatedAtUtc.ToOffset(TimeSpan.FromHours(3));

        Assert.ThrowsExactly<ArgumentException>(
            () => ProcessingAttemptRecord.FromTerminalOutcome(
                correlation,
                "Discovery",
                finalStage: null,
                localTimestamp,
                OperationOutcome.Failed));
        Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessingDiagnosticRecord(
                DiagnosticRecordId.CreateNew(),
                correlation,
                "Discovery",
                processingStage: null,
                sourceId: null,
                itemId: null,
                itemState: null,
                localTimestamp,
                OperationOutcome.Failed,
                "Discovery failed.",
                failureCode: null,
                technicalDetail: null));
    }

    [TestMethod]
    public void ReinitiatedAttemptsAppendDistinctClefRecords()
    {
        using var logs = new TemporaryLogDirectory();
        var first = CreateCorrelation();
        var second = first.CreateReinitiatedAttempt(first.InitiatedAtUtc.AddMinutes(2));

        using (var host = ProcessingHostApplicationHost.Create(CreateArguments(logs.Path)))
        {
            var recorder = host.Services.GetRequiredService<IProcessingHistoryRecorder>();
            recorder.RecordAttempt(CreateSuccessfulAttempt(first));
            recorder.RecordAttempt(CreateSuccessfulAttempt(second));
        }

        var records = ReadEvents(
            AssertSingleLogFile(logs.Path, "cia-processing-host-*.clef"),
            "ProcessingAttempt");

        Assert.HasCount(2, records);
        CollectionAssert.AreEquivalent(
            new[] { first.OperationId.ToString(), second.OperationId.ToString() },
            records.Select(record => record.GetProperty("OperationId").GetString()).ToArray());
    }

    [TestMethod]
    public void DesktopAndProcessingHostWriteCorrelatedStructuredClefRecords()
    {
        using var logs = new TemporaryLogDirectory();
        var correlation = CreateCorrelation();
        var diagnostic = new ProcessingDiagnosticRecord(
            DiagnosticRecordId.CreateNew(),
            correlation,
            "Discovery",
            "Source interpretation",
            "source-a",
            "member.xml",
            OperationItemState.Failed,
            correlation.InitiatedAtUtc.AddSeconds(5),
            OperationOutcome.CompletedWithIssues,
            "One source item could not be processed.",
            "parse-failed",
            "Parser rejected malformed input.");

        using (var desktopHost = DesktopApplicationHost.Create(CreateArguments(logs.Path)))
        {
            desktopHost.Services
                .GetRequiredService<IProcessingHistoryRecorder>()
                .RecordDiagnostic(diagnostic);
        }

        using (var processingHost = ProcessingHostApplicationHost.Create(CreateArguments(logs.Path)))
        {
            processingHost.Services
                .GetRequiredService<IProcessingHistoryRecorder>()
                .RecordAttempt(CreatePartialAttempt(correlation));
        }

        var uiRecords = ReadEvents(
            AssertSingleLogFile(logs.Path, "cia-ui-*.clef"),
            "ProcessingDiagnostic");
        var hostRecords = ReadEvents(
            AssertSingleLogFile(logs.Path, "cia-processing-host-*.clef"),
            "ProcessingAttempt");
        Assert.HasCount(1, uiRecords);
        Assert.HasCount(1, hostRecords);
        var uiRecord = uiRecords[0];
        var hostRecord = hostRecords[0];

        AssertStructuredCorrelation(uiRecord, DesktopApplicationHost.ProcessRole, correlation);
        AssertStructuredCorrelation(
            hostRecord,
            ProcessingHostApplicationHost.ProcessRole,
            correlation);
        Assert.AreEqual("Source interpretation", uiRecord.GetProperty("ProcessingStage").GetString());
        Assert.AreEqual("source-a", uiRecord.GetProperty("SourceId").GetString());
        Assert.AreEqual("member.xml", uiRecord.GetProperty("ItemId").GetString());
        Assert.AreEqual(
            OperationItemState.Failed.ToString(),
            uiRecord.GetProperty("ItemState").GetString());
        Assert.AreEqual("parse-failed", uiRecord.GetProperty("FailureCode").GetString());
        Assert.AreEqual(
            "Parser rejected malformed input.",
            uiRecord.GetProperty("TechnicalDetail").GetString());
        Assert.AreEqual(
            OperationOutcome.CompletedWithIssues.ToString(),
            hostRecord.GetProperty("TerminalOutcome").GetString());
        Assert.AreEqual(1, hostRecord.GetProperty("CompletedItemCount").GetInt32());
        Assert.AreEqual(1, hostRecord.GetProperty("FailedItemCount").GetInt32());
        Assert.AreEqual(1, hostRecord.GetProperty("UnprocessedItemCount").GetInt32());
        Assert.AreEqual(
            "source-a",
            hostRecord.GetProperty("CompletedItems")[0].GetProperty("ItemId").GetString());
        Assert.AreEqual(
            "source-b",
            hostRecord.GetProperty("FailedItems")[0].GetProperty("ItemId").GetString());
        Assert.AreEqual(
            "parse-failed",
            hostRecord.GetProperty("FailedItems")[0].GetProperty("FailureCode").GetString());
        Assert.AreEqual(
            "source-c",
            hostRecord.GetProperty("UnprocessedItems")[0].GetProperty("ItemId").GetString());
    }

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static OperationCorrelation CreateCorrelation()
    {
        return OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
    }

    private static ProcessingAttemptRecord CreateSuccessfulAttempt(
        OperationCorrelation correlation)
    {
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [OperationItemStatus.ProcessedSuccessfully("source-a")]);
        return ProcessingAttemptRecord.FromCompletion(
            "Discovery",
            "Source interpretation",
            correlation.InitiatedAtUtc.AddMinutes(1),
            completion);
    }

    private static ProcessingAttemptRecord CreatePartialAttempt(
        OperationCorrelation correlation)
    {
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed"),
                OperationItemStatus.Unprocessed("source-c", "dependency-unavailable")
            ]);
        return ProcessingAttemptRecord.FromCompletion(
            "Discovery",
            "Source interpretation",
            correlation.InitiatedAtUtc.AddMinutes(1),
            completion);
    }

    private static string[] CreateArguments(string logDirectory)
    {
        return [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}"];
    }

    private static string AssertSingleLogFile(string directory, string searchPattern)
    {
        var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, files);
        return files[0];
    }

    private static List<JsonElement> ReadEvents(string logFile, string recordType)
    {
        var events = new List<JsonElement>();

        foreach (var line in File.ReadLines(logFile))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("RecordType", out var type)
                && type.GetString() == recordType)
            {
                events.Add(root.Clone());
            }
        }

        return events;
    }

    private static void AssertStructuredCorrelation(
        JsonElement record,
        string processRole,
        OperationCorrelation correlation)
    {
        Assert.AreEqual(processRole, record.GetProperty("ProcessRole").GetString());
        Assert.AreEqual(
            correlation.OperationId.ToString(),
            record.GetProperty("OperationId").GetString());
        Assert.AreEqual(
            correlation.InitiatedAtUtc,
            record.GetProperty("OperationInitiatedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(
            TimeSpan.Zero,
            DateTimeOffset.Parse(
                record.GetProperty("@t").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).Offset);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed class TemporaryLogDirectory : IDisposable
    {
        private readonly string _testRoot;

        public TemporaryLogDirectory()
        {
            _testRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CIA.SPR99.Tests");
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
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
