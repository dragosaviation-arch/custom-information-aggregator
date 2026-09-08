using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DatabaseExtractionServiceTests
{
    [TestMethod]
    public async Task ExtractionPublishesDistinctDeterministicResultFromDatabaseOnly()
    {
        using var workspace = new ExtractionWorkspace();
        var first = workspace.CreateSource(
            "first.xml",
            "<root><alpha> First </alpha><beta>B1</beta><alpha>A2</alpha></root>");
        var second = workspace.CreateSource(
            "second.xml",
            "<root><beta>B2</beta></root>");
        var database = await workspace.BuildDatabaseAsync(
            [first, second],
            new DatabaseMappingSnapshot(
            [
                new DatabaseColumnMapping("Combined", ["alpha", "beta"])
            ]));
        File.Delete(first.Path);
        File.Delete(second.Path);

        var firstExtraction = await workspace.ExtractAsync(database);
        var firstValues = await ReadAllAsync(
            workspace.Repository,
            firstExtraction.PublishedResult!.OperationId);
        var secondExtraction = await workspace.ExtractAsync(database);
        var secondValues = await ReadAllAsync(
            workspace.Repository,
            secondExtraction.PublishedResult!.OperationId);

        Assert.IsTrue(firstExtraction.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, firstExtraction.Completion.Outcome);
        Assert.AreEqual(database.OperationId, firstExtraction.PublishedResult.DatabaseGeneration.OperationId);
        Assert.AreEqual(database.ValueCount, firstExtraction.PublishedResult.ValueCount);
        Assert.AreNotEqual(
            firstExtraction.PublishedResult.OperationId,
            secondExtraction.PublishedResult.OperationId);
        CollectionAssert.AreEqual(
            new[] { "Combined:alpha: First ", "Combined:beta:B1", "Combined:alpha:A2", "Combined:beta:B2" },
            firstValues.Select(Describe).ToArray());
        CollectionAssert.AreEqual(
            new[] { first.SourceId, first.SourceId, first.SourceId, second.SourceId },
            firstValues.Select(value => value.SourceId).ToArray());
        CollectionAssert.AreEqual(
            firstValues.Select(Describe).ToArray(),
            secondValues.Select(Describe).ToArray());
    }

    [TestMethod]
    public async Task DatabaseReplacementCannotChangeAnInFlightExtractionBasis()
    {
        using var workspace = new ExtractionWorkspace();
        var originalSource = workspace.CreateSource(
            "original.xml",
            "<root><tag>original</tag></root>");
        var mapping = new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping("Field", ["tag"])
        ]);
        var originalDatabase = await workspace.BuildDatabaseAsync([originalSource], mapping);
        using var atPublicationBoundary = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        var extractionCorrelation = OperationCorrelation.CreateNew();

        var extractionTask = Task.Run(() =>
            workspace.Repository.ExtractPublishedDatabaseAsync(
                extractionCorrelation,
                originalDatabase,
                tryEnterPublicationBoundary: () =>
                {
                    atPublicationBoundary.Set();
                    releasePublication.Wait(TimeSpan.FromSeconds(5));
                    return true;
                }));
        Assert.IsTrue(atPublicationBoundary.Wait(TimeSpan.FromSeconds(5)));

        var replacementSource = workspace.CreateSource(
            "replacement.xml",
            "<root><tag>replacement</tag></root>");
        var replacementTask = workspace.BuildDatabaseAsync([replacementSource], mapping);
        await Task.Delay(100);
        Assert.IsFalse(replacementTask.IsCompleted);

        releasePublication.Set();
        var extraction = await extractionTask;
        var replacement = await replacementTask;
        var extractedValues = await ReadAllAsync(workspace.Repository, extraction.OperationId);

        Assert.AreEqual(originalDatabase.OperationId, extraction.DatabaseGeneration.OperationId);
        Assert.AreNotEqual(originalDatabase.OperationId, replacement.OperationId);
        Assert.AreEqual("original", extractedValues.Single().Value);
    }

    [TestMethod]
    public async Task FailedAndCancelledReplacementPreservePriorPublishedResult()
    {
        using var workspace = new ExtractionWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>retained</tag></root>");
        var mapping = new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping("Field", ["tag"])
        ]);
        var database = await workspace.BuildDatabaseAsync([source], mapping);
        var original = await workspace.ExtractAsync(database);

        var cancelled = await workspace.ExtractionService.ExtractAsync(
            OperationCorrelation.CreateNew(),
            database,
            new CancellationToken(canceled: true));
        var mismatchedBasis = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            mapping,
            database.ValueCount);
        var failed = await workspace.ExtractAsync(mismatchedBasis);
        var retained = await workspace.Repository.ReadPublishedExtractionResultAsync();
        var retainedValues = await ReadAllAsync(
            workspace.Repository,
            original.PublishedResult!.OperationId);

        Assert.IsFalse(cancelled.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, cancelled.Completion.Outcome);
        Assert.IsFalse(failed.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, failed.Completion.Outcome);
        Assert.AreEqual(original.PublishedResult.OperationId, retained?.OperationId);
        Assert.AreEqual("retained", retainedValues.Single().Value);
    }

    [TestMethod]
    public async Task PublishedResultCanBeConsumedAsABoundedStream()
    {
        using var workspace = new ExtractionWorkspace();
        var xml = "<root>" + string.Concat(
            Enumerable.Range(1, 600).Select(index => $"<tag>V{index}</tag>")) + "</root>";
        var source = workspace.CreateSource("many.xml", xml);
        var database = await workspace.BuildDatabaseAsync(
            [source],
            new DatabaseMappingSnapshot(
            [
                new DatabaseColumnMapping("Field", ["tag"])
            ]));
        var extraction = await workspace.ExtractAsync(database);
        var firstValues = new List<ExtractionResultValue>();

        await foreach (var value in workspace.Repository.StreamPublishedExtractionValuesAsync(
                           extraction.PublishedResult!.OperationId))
        {
            firstValues.Add(value);
            if (firstValues.Count == 3)
            {
                break;
            }
        }

        Assert.HasCount(3, firstValues);
        CollectionAssert.AreEqual(
            new[] { "V1", "V2", "V3" },
            firstValues.Select(value => value.Value).ToArray());
        Assert.AreEqual(600, extraction.PublishedResult.ValueCount);
    }

    private static string Describe(ExtractionResultValue value)
    {
        return $"{value.DatabaseFieldName}:{value.SourceInformationType}:{value.Value}";
    }

    private static async Task<IReadOnlyList<ExtractionResultValue>> ReadAllAsync(
        StructuredInformationRepository repository,
        OperationId extractionId)
    {
        var values = new List<ExtractionResultValue>();
        await foreach (var value in repository.StreamPublishedExtractionValuesAsync(extractionId))
        {
            values.Add(value);
        }

        return values;
    }

    private sealed class RecordingHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }

    private sealed class ExtractionWorkspace : IDisposable
    {
        private readonly string _root;

        public ExtractionWorkspace()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "CIA.SPR83.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData")));
            Cancellation = new CooperativeOperationCancellation(new RecordingHistory());
            ExtractionService = new DatabaseExtractionService(
                Repository,
                Cancellation,
                NullLogger<DatabaseExtractionService>.Instance);
        }

        public StructuredInformationRepository Repository { get; }

        public CooperativeOperationCancellation Cancellation { get; }

        public DatabaseExtractionService ExtractionService { get; }

        public LoadedSourceContract CreateSource(string fileName, string xml)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllText(path, xml);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                path,
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public async Task<DatabaseGenerationSummary> BuildDatabaseAsync(
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping)
        {
            var reader = new SourceValueBatchReader(
                [],
                new GenericXmlElementValueSourceAdapter(),
                NullLogger<SourceValueBatchReader>.Instance);
            var service = new DatabaseGenerationService(
                Repository,
                reader,
                Cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
            var result = await service.BuildAsync(
                OperationCorrelation.CreateNew(),
                sources,
                mapping);
            Assert.IsTrue(result.Accepted);
            return result.PublishedGeneration!;
        }

        public Task<DatabaseExtractionHostResult> ExtractAsync(
            DatabaseGenerationSummary databaseGeneration)
        {
            return ExtractionService.ExtractAsync(
                OperationCorrelation.CreateNew(),
                databaseGeneration);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
