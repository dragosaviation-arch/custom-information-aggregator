using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DatabaseGenerationServiceTests
{
    [TestMethod]
    public async Task RealXmlBuildPublishesOnlySelectedExactMappedValuesDeterministically()
    {
        using var workspace = new DatabaseGenerationWorkspace();
        var firstSource = workspace.CreateSource(
            "first.xml",
            "<root><alpha> First </alpha><beta>ignored</beta></root>");
        var secondSource = workspace.CreateSource(
            "second.xml",
            "<root><beta>Second</beta><alpha>Third</alpha></root>");
        var mapping = new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping("Combined", ["alpha"])
        ]);
        var service = workspace.CreateService();

        var firstResult = await service.BuildAsync(
            OperationCorrelation.CreateNew(),
            [firstSource, secondSource],
            mapping);
        var firstValues = await workspace.Repository.QueryPublishedDatabaseValuesAsync();

        Assert.IsTrue(firstResult.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, firstResult.Completion.Outcome);
        Assert.AreEqual(2, firstResult.PublishedGeneration?.ValueCount);
        CollectionAssert.AreEqual(
            new[] { "Combined:alpha: First ", "Combined:alpha:Third" },
            firstValues.Select(Describe).ToArray());
        CollectionAssert.AreEqual(
            new[] { firstSource.SourceId, secondSource.SourceId },
            firstValues.Select(value => value.SourceId).ToArray());

        var secondResult = await service.BuildAsync(
            OperationCorrelation.CreateNew(),
            [firstSource, secondSource],
            mapping);
        var secondValues = await workspace.Repository.QueryPublishedDatabaseValuesAsync();

        Assert.IsTrue(secondResult.Accepted);
        Assert.AreNotEqual(
            firstResult.PublishedGeneration!.OperationId,
            secondResult.PublishedGeneration!.OperationId);
        Assert.AreEqual(
            secondResult.PublishedGeneration.OperationId,
            (await workspace.Repository.ReadPublishedDatabaseGenerationAsync())!.OperationId);
        CollectionAssert.AreEqual(
            firstValues.Select(Describe).ToArray(),
            secondValues.Select(Describe).ToArray());
    }

    [TestMethod]
    public async Task IndependentSourceFailurePublishesValidCandidateWithTruthfulIssues()
    {
        using var workspace = new DatabaseGenerationWorkspace();
        var valid = workspace.CreateSource("valid.xml", "<root><tag>retained</tag></root>");
        var malformed = workspace.CreateSource("malformed.xml", "<root><tag>broken");
        var service = workspace.CreateService();

        var result = await service.BuildAsync(
            OperationCorrelation.CreateNew(),
            [valid, malformed],
            CreateMapping("tag"));
        var values = await workspace.Repository.QueryPublishedDatabaseValuesAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, result.Completion.Outcome);
        Assert.AreEqual(
            OperationItemState.Failed,
            result.Completion.Items.Single(item =>
                item.ItemId == malformed.SourceId.ToString()).State);
        Assert.HasCount(1, values);
        Assert.AreEqual("retained", values[0].Value);
        Assert.AreEqual(valid.SourceId, values[0].SourceId);
    }

    [TestMethod]
    public async Task EmptyOrInvalidFirstCandidatePublishesNothing()
    {
        using var workspace = new DatabaseGenerationWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><other>value</other></root>");
        var service = workspace.CreateService();

        var result = await service.BuildAsync(
            OperationCorrelation.CreateNew(),
            [source],
            CreateMapping("selected"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, result.Completion.Outcome);
        Assert.IsNull(result.PublishedGeneration);
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
        Assert.IsEmpty(await workspace.Repository.QueryPublishedDatabaseValuesAsync());
    }

    [TestMethod]
    public async Task CancelledReplacementPreservesPreviousGenerationAndCorrelation()
    {
        using var workspace = new DatabaseGenerationWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>original</tag></root>");
        var cancellation = workspace.Cancellation;
        var initial = await workspace.CreateService().BuildAsync(
            OperationCorrelation.CreateNew(),
            [source],
            CreateMapping("tag"));
        var previousOperationId = initial.PublishedGeneration!.OperationId;
        var blockingReader = new BlockingSourceValueReader();
        var service = workspace.CreateService(blockingReader);
        var replacementCorrelation = OperationCorrelation.CreateNew();

        var replacementTask = service.BuildAsync(
            replacementCorrelation,
            [source],
            CreateMapping("tag"));
        await blockingReader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = cancellation.RequestCancellation(replacementCorrelation.OperationId);
        var replacement = await replacementTask;

        Assert.IsTrue(request.Accepted);
        Assert.IsFalse(replacement.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, replacement.Completion.Outcome);
        Assert.AreEqual(replacementCorrelation, replacement.Completion.Correlation);
        var retained = await workspace.Repository.ReadPublishedDatabaseGenerationAsync();
        Assert.AreEqual(previousOperationId, retained?.OperationId);
        Assert.AreEqual(
            "original",
            (await workspace.Repository.QueryPublishedDatabaseValuesAsync()).Single().Value);
    }

    [TestMethod]
    public async Task CancelledFirstBuildPublishesNothing()
    {
        using var workspace = new DatabaseGenerationWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>value</tag></root>");
        var blockingReader = new BlockingSourceValueReader();
        var service = workspace.CreateService(blockingReader);
        var correlation = OperationCorrelation.CreateNew();

        var buildTask = service.BuildAsync(correlation, [source], CreateMapping("tag"));
        await blockingReader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = workspace.Cancellation.RequestCancellation(correlation.OperationId);
        var result = await buildTask;

        Assert.IsTrue(request.Accepted);
        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, result.Completion.Outcome);
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
        Assert.IsEmpty(await workspace.Repository.QueryPublishedDatabaseValuesAsync());
    }

    private static DatabaseMappingSnapshot CreateMapping(string informationType)
    {
        return DatabaseTagMapper.CreateMapping(
            new DiscoveryConfigurationSnapshot(
            [
                new DiscoveryConfigurationItem(
                    informationType,
                    DiscoveryInformationDisposition.Selected)
            ]),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static string Describe(MappedDatabaseValue value)
    {
        return $"{value.DatabaseTagName}:{value.SourceInformationType}:{value.Value}";
    }

    private sealed class BlockingSourceValueReader : ISourceValueBatchReader
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SourceValueBatchReadResult> ReadSelectedAsync(
            LoadedSourceContract source,
            IReadOnlySet<string> selectedInformationTypes,
            Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking reader must be cancelled.");
        }
    }

    private sealed class RecordingHistory : IProcessingHistoryRecorder
    {
        public List<ProcessingAttemptRecord> Attempts { get; } = [];

        public void RecordAttempt(ProcessingAttemptRecord record)
        {
            Attempts.Add(record);
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }

    private sealed class DatabaseGenerationWorkspace : IDisposable
    {
        private readonly string _root;
        private readonly RecordingHistory _history = new();

        public DatabaseGenerationWorkspace()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "CIA.SPR79.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var paths = ApplicationPaths.FromLocalApplicationData(
                Path.Combine(_root, "LocalAppData"));
            Repository = new StructuredInformationRepository(paths);
            Cancellation = new CooperativeOperationCancellation(_history);
        }

        public StructuredInformationRepository Repository { get; }

        public CooperativeOperationCancellation Cancellation { get; }

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

        public DatabaseGenerationService CreateService(
            ISourceValueBatchReader? sourceValueReader = null)
        {
            sourceValueReader ??= new SourceValueBatchReader(
                [],
                new GenericXmlElementValueSourceAdapter(),
                NullLogger<SourceValueBatchReader>.Instance);
            return new DatabaseGenerationService(
                Repository,
                sourceValueReader,
                Cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
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
