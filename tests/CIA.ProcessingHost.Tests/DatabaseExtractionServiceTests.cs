using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Core.Sources;
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
    public async Task IncludedHierarchyRowsAreSnapshottedAcrossSetsWithoutRereadingXml()
    {
        using var workspace = new ExtractionWorkspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = workspace.Source(firstSet, "first.xml",
            "<records><record><code>SAME</code><qty>1</qty></record><record><code>DROP</code><qty>2</qty></record></records>");
        var second = workspace.Source(firstSet, "second.xml",
            "<records><record><code>SAME</code><qty>3</qty></record></records>");
        var third = workspace.Source(secondSet, "third.xml",
            "<records><record><code>OTHER</code><qty>4</qty></record></records>");
        var database = await workspace.BuildDatabaseAsync(
            (firstSet, "Set one", RepeatedDataLayout.StructuralRows, new[] { first, second }),
            (secondSet, "Set two", RepeatedDataLayout.StructuralRows, new[] { third }));
        var before = await workspace.DatabasePageAsync(database, firstSet);
        var excluded = before.Rows.Single(row => Values(row).Contains("DROP"));
        Assert.AreEqual(1, await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
            new DatabaseRowInclusionChange(
                database.OperationId,
                firstSet,
                [excluded.Ordinal],
                false)));
        File.Delete(first.Path);
        File.Delete(second.Path);
        File.Delete(third.Path);

        var extraction = await workspace.ExtractAsync(database);
        var laterExcluded = before.Rows.Single(row => Values(row).Contains("1"));
        Assert.AreEqual(1, await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
            new DatabaseRowInclusionChange(
                database.OperationId,
                firstSet,
                [laterExcluded.Ordinal],
                false)));
        var firstRows = await ReadRowsAsync(
            workspace.Repository,
            extraction.PublishedResult!.OperationId,
            firstSet);
        var secondRows = await ReadRowsAsync(
            workspace.Repository,
            extraction.PublishedResult.OperationId,
            secondSet);

        Assert.IsTrue(extraction.Accepted);
        Assert.HasCount(2, extraction.PublishedResult.Datasets);
        Assert.AreEqual(database.OperationId, extraction.PublishedResult.DatabaseGeneration.OperationId);
        Assert.HasCount(2, firstRows);
        Assert.HasCount(1, secondRows);
        Assert.IsFalse(firstRows.SelectMany(Values).Contains("DROP"));
        Assert.AreEqual(2, firstRows.SelectMany(Values).Count(value => value == "SAME"));
        Assert.AreNotEqual(firstRows[0].Source.SourceId, firstRows[1].Source.SourceId);
        Assert.IsTrue(firstRows.All(row => row.Source.SourceSetId == firstSet));
        Assert.IsTrue(secondRows.All(row => row.Source.SourceSetId == secondSet));
        Assert.IsTrue(firstRows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values)
            .All(value => value.SourceId == firstRows.Single(row =>
                row.Source.SourceId == value.SourceId).Source.SourceId));
        Assert.IsTrue(firstRows.Any(row => Values(row).Contains("SAME") && Values(row).Contains("1")));
        Assert.IsTrue(firstRows.Any(row => Values(row).Contains("SAME") && Values(row).Contains("3")));
        CollectionAssert.AreEqual(
            firstRows.Select(row => row.DatabaseRowOrdinal).Order().ToArray(),
            firstRows.Select(row => row.DatabaseRowOrdinal).ToArray());
    }

    [TestMethod]
    public async Task ConflictDetailedIdentitiesLineageAndNestedCoordinatesSurvive()
    {
        using var workspace = new ExtractionWorkspace();
        var conflictSet = SourceSetId.CreateNew();
        var nestedSet = SourceSetId.CreateNew();
        var conflictSource = workspace.Source(
            conflictSet,
            "conflict.xml",
            "<records><record code=\"A\"><code>B</code></record><record code=\"C\"><code>C</code></record></records>");
        var nestedFixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "StructuralDiscovery",
            "flattening-nested-records.xml");
        var candidateFixture = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "StructuralDiscovery",
            "stable-slot-records.xml");
        var nestedSource = workspace.Source(
            nestedSet,
            "nested.xml",
            File.ReadAllText(nestedFixture));
        var candidateSource = workspace.Source(
            nestedSet,
            "candidates.xml",
            File.ReadAllText(candidateFixture));
        var conflictIdentities = await workspace.IdentitiesAsync(conflictSource);
        var conflictOverrides = conflictIdentities.ToDictionary(identity => identity, _ => "Unified");
        var database = await workspace.BuildDatabaseAsync(
            workspace.Dataset(
                conflictSet,
                "Conflicts",
                RepeatedDataLayout.StructuralRows,
                [conflictSource],
                conflictIdentities,
                conflictOverrides,
                1),
            await workspace.DatasetAsync(
                nestedSet,
                "Detailed",
                RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                [nestedSource, candidateSource],
                2));

        var extraction = await workspace.ExtractAsync(database);
        var conflictRows = await ReadRowsAsync(
            workspace.Repository,
            extraction.PublishedResult!.OperationId,
            conflictSet);
        var detailedRows = await ReadRowsAsync(
            workspace.Repository,
            extraction.PublishedResult.OperationId,
            nestedSet);

        var conflict = conflictRows.Single(row => Values(row).Contains("A"));
        Assert.HasCount(1, conflict.Cells);
        Assert.IsTrue(conflict.Cells[0].HasConflict);
        CollectionAssert.AreEquivalent(
            new[] { "A", "B" },
            conflict.Cells[0].Values.Select(value => value.Value).ToArray());
        var equal = conflictRows.Single(row => Values(row).Count(value => value == "C") == 2);
        Assert.HasCount(2, equal.Cells.Single().Values);
        Assert.IsFalse(equal.Cells.Single().HasConflict);

        var allValues = detailedRows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values).ToArray();
        CollectionAssert.IsSubsetOf(
            new[]
            {
                SourceValueCandidateKind.Element,
                SourceValueCandidateKind.Attribute,
                SourceValueCandidateKind.Structural
            },
            allValues.Select(value => value.DetailedIdentity.CandidateKind).Distinct().ToArray());
        Assert.IsTrue(allValues.All(value => value.Lineage.ElementPath.Count > 0));
        Assert.IsTrue(detailedRows.SelectMany(row => row.Cells).Any(cell =>
            cell.ColumnIdentity.RepeatCoordinates.Coordinates.Count > 1));
        Assert.IsTrue(detailedRows.SelectMany(row => row.Cells).All(cell =>
            cell.Values.All(value => value.RepeatCoordinates.Equals(
                cell.ColumnIdentity.RepeatCoordinates))));
    }

    [TestMethod]
    public async Task FailedCancelledAndIncompleteReplacementRetainPriorPublishedResult()
    {
        using var workspace = new ExtractionWorkspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "source.xml",
            "<records><record><tag>retained</tag></record></records>");
        var database = await workspace.BuildDatabaseAsync(
            (set, "Set", RepeatedDataLayout.StructuralRows, new[] { source }));
        var original = await workspace.ExtractAsync(database);

        var cancelled = await workspace.ExtractionService.ExtractAsync(
            OperationCorrelation.CreateNew(),
            database,
            new CancellationToken(canceled: true));
        var basisDataset = database.Datasets.Single();
        var mismatchedBasis = new DatabaseGenerationSummary(
            database.OperationId,
            [new DatabaseDatasetSummary(
                basisDataset.SourceSetId,
                "Changed name",
                basisDataset.Ordinal,
                basisDataset.RepeatedDataLayout,
                basisDataset.RowCount,
                basisDataset.ValueCount,
                basisDataset.Columns,
                basisDataset.Mappings)]);
        var failed = await workspace.ExtractAsync(mismatchedBasis);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await workspace.Repository.ExtractPublishedDatabaseAsync(
                OperationCorrelation.CreateNew(),
                database,
                tryEnterPublicationBoundary: () => false));
        var retained = await workspace.Repository.ReadPublishedExtractionResultAsync();
        var retainedRows = await ReadRowsAsync(
            workspace.Repository,
            original.PublishedResult!.OperationId,
            set);

        Assert.IsFalse(cancelled.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, cancelled.Completion.Outcome);
        Assert.IsFalse(failed.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, failed.Completion.Outcome);
        Assert.AreEqual(original.PublishedResult.OperationId, retained?.OperationId);
        Assert.AreEqual("retained", retainedRows.Single().Cells.Single().Values.Single().Value);
    }

    [TestMethod]
    public async Task PublishedRowsAreDeterministicAndBounded()
    {
        using var workspace = new ExtractionWorkspace();
        var set = SourceSetId.CreateNew();
        var xml = "<records>" + string.Concat(
            Enumerable.Range(1, 600).Select(index =>
                $"<record><tag>V{index}</tag></record>")) + "</records>";
        var source = workspace.Source(set, "many.xml", xml);
        var database = await workspace.BuildDatabaseAsync(
            (set, "Many", RepeatedDataLayout.StructuralRows, new[] { source }));
        var first = await workspace.ExtractAsync(database);
        var firstRows = await ReadRowsAsync(
            workspace.Repository,
            first.PublishedResult!.OperationId,
            set,
            maximumRows: 3);
        var second = await workspace.ExtractAsync(database);
        var secondRows = await ReadRowsAsync(
            workspace.Repository,
            second.PublishedResult!.OperationId,
            set,
            maximumRows: 3);

        Assert.HasCount(3, firstRows);
        CollectionAssert.AreEqual(
            new[] { "V1", "V2", "V3" },
            firstRows.SelectMany(Values).ToArray());
        CollectionAssert.AreEqual(
            firstRows.SelectMany(Values).ToArray(),
            secondRows.SelectMany(Values).ToArray());
        Assert.AreEqual(600, first.PublishedResult.RowCount);
        Assert.AreEqual(600, first.PublishedResult.ValueCount);
    }

    [TestMethod]
    public async Task LegacyFlatGenerationIsRejectedAndNoLegacyExportStreamIsExposed()
    {
        using var workspace = new ExtractionWorkspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "source.xml",
            "<records><record><tag>value</tag></record></records>");
        var database = await workspace.BuildDatabaseAsync(
            (set, "Set", RepeatedDataLayout.StructuralRows, new[] { source }));
        Assert.IsTrue((await workspace.ExtractAsync(database)).Accepted);
        var legacy = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            new DatabaseMappingSnapshot([new DatabaseColumnMapping("Field", ["tag"])]),
            1);

        var rejected = await workspace.ExtractAsync(legacy);

        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual("legacy-flat-extraction-not-supported", rejected.Failure?.Code);
        Assert.IsFalse(typeof(StructuredInformationRepository).GetMethods().Any(method =>
            method.Name.Contains("ExtractionValuesForExport", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SupersededExtractionRowStreamFailsInsteadOfYieldingAnEmptyDataset()
    {
        using var workspace = new ExtractionWorkspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "source.xml",
            "<records><record><tag>value</tag></record></records>");
        var database = await workspace.BuildDatabaseAsync(
            (set, "Set", RepeatedDataLayout.StructuralRows, new[] { source }));
        var first = await workspace.ExtractAsync(database);
        Assert.IsTrue(first.Accepted);
        var replacement = await workspace.ExtractAsync(database);
        Assert.IsTrue(replacement.Accepted);

        var exception = await Assert.ThrowsExactlyAsync<StructuredInformationRepositoryException>(
            async () =>
            {
                await foreach (var _ in workspace.Repository.StreamPublishedExtractionRowsAsync(
                                   first.PublishedResult!.OperationId,
                                   set))
                {
                }
            });

        StringAssert.Contains(exception.Message, "no longer the active published result");
    }

    private static IReadOnlyList<string> Values(DatabaseReviewRow row) =>
        row.Cells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray();

    private static IReadOnlyList<string> Values(ExtractionResultRow row) =>
        row.Cells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray();

    private static async Task<IReadOnlyList<ExtractionResultRow>> ReadRowsAsync(
        StructuredInformationRepository repository,
        OperationId extractionId,
        SourceSetId sourceSetId,
        int maximumRows = int.MaxValue)
    {
        var rows = new List<ExtractionResultRow>();
        await foreach (var row in repository.StreamPublishedExtractionRowsAsync(
                           extractionId,
                           sourceSetId))
        {
            rows.Add(row);
            if (rows.Count == maximumRows)
            {
                break;
            }
        }

        return rows;
    }

    private sealed class ExtractionWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR139.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SourceInterpreter _interpreter;

        public ExtractionWorkspace()
        {
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData")));
            Cancellation = new CooperativeOperationCancellation(new NullHistory());
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            DatabaseService = new DatabaseGenerationService(
                Repository,
                _interpreter,
                new HierarchyFlatteningEngine(),
                Cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
            ExtractionService = new DatabaseExtractionService(
                Repository,
                Cancellation,
                NullLogger<DatabaseExtractionService>.Instance);
        }

        public StructuredInformationRepository Repository { get; }

        public CooperativeOperationCancellation Cancellation { get; }

        public DatabaseGenerationService DatabaseService { get; }

        public DatabaseExtractionService ExtractionService { get; }

        public LoadedSourceContract Source(
            SourceSetId sourceSetId,
            string fileName,
            string xml)
        {
            var path = Path.Combine(_root, sourceSetId.ToString(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, xml);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId,
                path,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public async Task<IReadOnlyList<DiscoveryInformationIdentity>> IdentitiesAsync(
            LoadedSourceContract source)
        {
            var result = await _interpreter.InterpretAsync(source);
            Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
            return result.Source!.Values
                .Where(value => value.Lineage is not null)
                .Select(value => HierarchySourceOccurrence.FromInterpretedValue(
                    source.SourceSetId,
                    value).Identity)
                .Distinct()
                .ToArray();
        }

        public async Task<DatabaseDatasetBuildSpecification> DatasetAsync(
            SourceSetId sourceSetId,
            string name,
            RepeatedDataLayout layout,
            IReadOnlyList<LoadedSourceContract> sources,
            int ordinal)
        {
            var identities = new List<DiscoveryInformationIdentity>();
            foreach (var source in sources)
            {
                identities.AddRange(await IdentitiesAsync(source));
            }

            return Dataset(
                sourceSetId,
                name,
                layout,
                sources,
                identities.Distinct().ToArray(),
                new Dictionary<DiscoveryInformationIdentity, string>(),
                ordinal);
        }

        public DatabaseDatasetBuildSpecification Dataset(
            SourceSetId sourceSetId,
            string name,
            RepeatedDataLayout layout,
            IReadOnlyList<LoadedSourceContract> sources,
            IReadOnlyList<DiscoveryInformationIdentity> identities,
            IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
            int ordinal) =>
            new(
                sourceSetId,
                name,
                ordinal,
                layout,
                sources,
                DatabaseTagMapper.CreateFieldMappings(
                    sourceSetId,
                    identities.Select(identity => new DiscoveryConfigurationItem(
                        identity,
                        DiscoveryInformationDisposition.Selected)),
                    overrides));

        public async Task<DatabaseGenerationSummary> BuildDatabaseAsync(
            params (SourceSetId Set, string Name, RepeatedDataLayout Layout,
                LoadedSourceContract[] Sources)[] datasets)
        {
            var specifications = new List<DatabaseDatasetBuildSpecification>();
            foreach (var dataset in datasets)
            {
                specifications.Add(await DatasetAsync(
                    dataset.Set,
                    dataset.Name,
                    dataset.Layout,
                    dataset.Sources,
                    specifications.Count + 1));
            }

            return await BuildDatabaseAsync(specifications.ToArray());
        }

        public async Task<DatabaseGenerationSummary> BuildDatabaseAsync(
            params DatabaseDatasetBuildSpecification[] datasets)
        {
            var result = await DatabaseService.BuildAsync(
                OperationCorrelation.CreateNew(),
                new DatabaseBuildSpecification(datasets));
            Assert.IsTrue(result.Accepted);
            return result.PublishedGeneration!;
        }

        public async Task<DatabaseReviewPage> DatabasePageAsync(
            DatabaseGenerationSummary generation,
            SourceSetId sourceSetId) =>
            (await Repository.ReadPublishedDatabasePageAsync(
                new DatabaseReviewQuery(
                    generation.OperationId,
                    sourceSetId,
                    1,
                    DatabaseReviewLimits.MaximumRowsPerPage,
                    null,
                    DatabaseRowInclusionFilter.All)))!;

        public Task<DatabaseExtractionHostResult> ExtractAsync(
            DatabaseGenerationSummary generation) =>
            ExtractionService.ExtractAsync(OperationCorrelation.CreateNew(), generation);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class NullHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }
}
