using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
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
public sealed class HierarchyDatabaseGenerationTests
{
    [TestMethod]
    public void ProductionDatabaseServiceExposesOnlyTypedHierarchyBuildContract()
    {
        var buildMethods = typeof(DatabaseGenerationService).GetMethods()
            .Where(method => method.Name == nameof(DatabaseGenerationService.BuildAsync))
            .ToArray();

        Assert.HasCount(1, buildMethods);
        Assert.AreEqual(
            typeof(DatabaseBuildSpecification),
            buildMethods[0].GetParameters()[1].ParameterType);
    }

    [TestMethod]
    public async Task SourceSetsAndFilesProduceIndependentHierarchyRows()
    {
        using var workspace = new Workspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = workspace.Source(firstSet, "first.xml",
            "<records><record><code>A</code><price>10</price></record><record><code>B</code><price>20</price></record></records>");
        var second = workspace.Source(firstSet, "second.xml",
            "<records><record><code>C</code><price>30</price></record></records>");
        var third = workspace.Source(secondSet, "third.xml",
            "<records><record><code>D</code><price>40</price></record></records>");
        var specification = await workspace.SpecificationAsync(
            (firstSet, "Orders", RepeatedDataLayout.AlignRepeatedGroupsByPosition, new[] { first, second }),
            (secondSet, "Invoices", RepeatedDataLayout.AlignRepeatedGroupsByPosition, new[] { third }));

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);

        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(result.PublishedGeneration);
        Assert.HasCount(2, result.PublishedGeneration.Datasets);
        Assert.AreEqual(4, result.PublishedGeneration.RowCount);
        var firstPage = await workspace.PageAsync(result.PublishedGeneration, firstSet);
        var secondPage = await workspace.PageAsync(result.PublishedGeneration, secondSet);
        Assert.AreEqual(3, firstPage.TotalRowCount);
        Assert.AreEqual(1, secondPage.TotalRowCount);
        Assert.IsTrue(firstPage.Rows.All(row => row.Cells.SelectMany(cell => cell.Values)
            .All(value => value.SourceId == row.Source.SourceId)));
        CollectionAssert.AreEquivalent(
            new[] { first.SourceId, second.SourceId },
            firstPage.Rows.Select(row => row.Source.SourceId).Distinct().ToArray());
        Assert.IsTrue(firstPage.Rows.Any(row => Values(row).Contains("A") && Values(row).Contains("10")));
        Assert.IsFalse(firstPage.Rows.Any(row => Values(row).Contains("A") && Values(row).Contains("30")));
        Assert.AreEqual("Orders", firstPage.Dataset.DisplayName);
        Assert.AreEqual("Invoices", secondPage.Dataset.DisplayName);
        Assert.IsTrue(firstPage.Rows.All(row =>
            row.RecordHierarchy.Contains("/record[", StringComparison.Ordinal)
            && !row.RecordHierarchy.Contains(row.Source.SourceId.ToString(), StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(RepeatedDataLayout.AlignRepeatedGroupsByPosition)]
    [DataRow(RepeatedDataLayout.StructuralRows)]
    [DataRow(RepeatedDataLayout.AllCombinations)]
    [DataRow(RepeatedDataLayout.NumberRepeatedValuesIntoColumns)]
    public async Task EveryLayoutPersistsSpr137AssociationTruth(RepeatedDataLayout layout)
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(set, "associated.xml",
            "<records><record><code>A</code><price>10</price></record><record><code>B</code><price>20</price></record></records>");
        var specification = await workspace.SpecificationAsync((set, "Set", layout, new[] { source }));

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        Assert.IsTrue(result.Accepted);
        Assert.IsTrue(page.Rows.Any(row => Values(row).Contains("A") && Values(row).Contains("10")));
        Assert.IsTrue(page.Rows.Any(row => Values(row).Contains("B") && Values(row).Contains("20")));
        if (layout == RepeatedDataLayout.NumberRepeatedValuesIntoColumns)
        {
            Assert.IsTrue(page.Dataset.Columns.Any(column =>
                column.Identity.RepeatCoordinates.Coordinates.Count > 0));
            var cells = page.Rows.SelectMany(row => row.Cells).ToArray();
            var aCoordinates = cells.Single(cell => cell.Values.Any(value => value.Value == "A"))
                .ColumnIdentity.RepeatCoordinates;
            var tenCoordinates = cells.Single(cell => cell.Values.Any(value => value.Value == "10"))
                .ColumnIdentity.RepeatCoordinates;
            var bCoordinates = cells.Single(cell => cell.Values.Any(value => value.Value == "B"))
                .ColumnIdentity.RepeatCoordinates;
            var twentyCoordinates = cells.Single(cell => cell.Values.Any(value => value.Value == "20"))
                .ColumnIdentity.RepeatCoordinates;
            Assert.AreEqual(aCoordinates, tenCoordinates);
            Assert.AreEqual(bCoordinates, twentyCoordinates);
            Assert.AreNotEqual(aCoordinates, bCoordinates);
        }
        else
        {
            Assert.IsFalse(page.Rows.Any(row => Values(row).Contains("A") && Values(row).Contains("20")));
        }
    }

    [TestMethod]
    public async Task ExplicitNormalizationPreservesConflictingAndEqualUnderlyingEvidence()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(set, "mapping.xml",
            "<records><record code=\"A\"><code>B</code></record><record code=\"C\"><code>C</code></record></records>");
        var interpreted = await workspace.InterpretAsync(source);
        var identities = Identities(set, interpreted);
        var overrides = identities.ToDictionary(identity => identity, _ => "Unified");
        var specification = workspace.Specification(
            set, "Mapped", RepeatedDataLayout.StructuralRows, [source], identities, overrides);

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        var conflict = page.Rows.Single(row => Values(row).Contains("A"));
        var equal = page.Rows.Single(row => Values(row).Count(value => value == "C") == 2);
        Assert.HasCount(1, conflict.Cells);
        Assert.IsTrue(conflict.Cells[0].HasConflict);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, Values(conflict).ToArray());
        Assert.HasCount(2, equal.Cells[0].Values);
        Assert.IsFalse(equal.Cells[0].HasConflict);
        Assert.IsTrue(equal.Cells[0].Values.Select(value => value.DetailedIdentity.CandidateKind)
            .Contains(SourceValueCandidateKind.Attribute));
        Assert.IsTrue(equal.Cells[0].Values.Select(value => value.DetailedIdentity.CandidateKind)
            .Contains(SourceValueCandidateKind.Element));
    }

    [TestMethod]
    public async Task InclusionAndValueOrMetadataSearchAreAppliedBeforePaging()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var first = workspace.Source(set, "alpha-file.xml",
            "<records><record><code>needle</code></record></records>");
        var second = workspace.Source(set, "metadata-target.xml",
            "<records><record><code>other</code></record></records>");
        var specification = await workspace.SpecificationAsync(
            (set, "Search set", RepeatedDataLayout.StructuralRows, new[] { first, second }));
        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var generation = result.PublishedGeneration!;
        var all = await workspace.PageAsync(generation, set);

        Assert.IsTrue(all.Rows.All(row => row.IsIncluded));
        var changed = await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
            new DatabaseRowInclusionChange(generation.OperationId, set, [all.Rows[0].Ordinal], false));
        Assert.AreEqual(1, changed);
        var excluded = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 100, null,
                DatabaseRowInclusionFilter.Excluded));
        var valueSearch = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 1, "needle",
                DatabaseRowInclusionFilter.All));
        var metadataSearch = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 1, "metadata-target",
                DatabaseRowInclusionFilter.All));
        var sourceIdSearch = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 1, second.SourceId.ToString(),
                DatabaseRowInclusionFilter.All));

        Assert.AreEqual(1, excluded!.TotalRowCount);
        Assert.IsFalse(excluded.Rows[0].IsIncluded);
        Assert.AreEqual(1, valueSearch!.TotalRowCount);
        Assert.AreEqual("needle", Values(valueSearch.Rows[0]).Single());
        Assert.AreEqual(1, metadataSearch!.TotalRowCount);
        Assert.AreEqual("metadata-target.xml", metadataSearch.Rows[0].Source.SourceFileName);
        Assert.AreEqual(second.SourceId, sourceIdSearch!.Rows[0].Source.SourceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(metadataSearch.Rows[0].Source.FullSourcePath));
        Assert.IsNotNull(metadataSearch.Rows[0].Cells[0].Values[0].Lineage);

        Assert.AreEqual(
            all.Rows.Count - 1,
            await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
                new DatabaseRowInclusionChange(
                    generation.OperationId,
                    set,
                    all.Rows.Skip(1).Select(row => row.Ordinal).ToArray(),
                    false)));
        var allExcluded = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 100, null,
                DatabaseRowInclusionFilter.Excluded));
        Assert.AreEqual(all.TotalRowCount, allExcluded!.TotalRowCount);
        Assert.AreEqual(
            1,
            await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
                new DatabaseRowInclusionChange(
                    generation.OperationId,
                    set,
                    [all.Rows[0].Ordinal],
                    true)));
        var includedAgain = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(generation.OperationId, set, 1, 100, null,
                DatabaseRowInclusionFilter.Included));
        Assert.AreEqual(1, includedAgain!.TotalRowCount);
    }

    [TestMethod]
    public async Task SearchMatchesVisibleSourceAndCandidateKindNamesBeforePaging()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var fixture = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "StructuralDiscovery", "stable-slot-records.xml");
        var source = workspace.Source(set, "candidates.xml", File.ReadAllText(fixture));
        var specification = await workspace.SpecificationAsync(
            (set, "Candidate kinds", RepeatedDataLayout.StructuralRows, new[] { source }));
        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);

        foreach (var term in new[] { "XmlFile", "Element", "Attribute", "Structural" })
        {
            var page = await workspace.Repository.ReadPublishedDatabasePageAsync(
                new DatabaseReviewQuery(
                    result.PublishedGeneration!.OperationId,
                    set,
                    1,
                    1,
                    term,
                    DatabaseRowInclusionFilter.All));
            Assert.IsNotNull(page, term);
            Assert.IsGreaterThan(0, page.TotalRowCount, term);
            Assert.HasCount(1, page.Rows, term);
        }
    }

    [TestMethod]
    public async Task NestedNumberedCoordinatesAndAllCandidateKindsRoundTripLosslessly()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var nestedFixture = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "StructuralDiscovery", "flattening-nested-records.xml");
        var candidateFixture = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "StructuralDiscovery", "stable-slot-records.xml");
        var nested = workspace.Source(set, "nested.xml", File.ReadAllText(nestedFixture));
        var candidates = workspace.Source(set, "candidates.xml", File.ReadAllText(candidateFixture));
        var specification = await workspace.SpecificationAsync(
            (set, "Detailed", RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                new[] { nested, candidates }));

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        Assert.IsTrue(page.Dataset.Columns.Any(column =>
            column.Identity.RepeatCoordinates.Coordinates.Count >= 2));
        var values = page.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values).ToArray();
        CollectionAssert.IsSubsetOf(
            new[]
            {
                SourceValueCandidateKind.Element,
                SourceValueCandidateKind.Attribute,
                SourceValueCandidateKind.Structural
            },
            values.Select(value => value.DetailedIdentity.CandidateKind).Distinct().ToArray());
        Assert.IsTrue(values.All(value =>
            !string.IsNullOrWhiteSpace(value.DetailedIdentity.StructuralPath)
            && !string.IsNullOrWhiteSpace(value.DetailedIdentity.StructuralIdentity)
            && value.Lineage.TraversalOrder > 0));
    }

    [TestMethod]
    public async Task SameNameElementAndAttributeRemainDistinctWithoutExplicitNormalization()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "distinct-kinds.xml",
            "<records><record code=\"attribute-value\"><code>element-value</code></record></records>");
        var specification = await workspace.SpecificationAsync(
            (set, "Distinct kinds", RepeatedDataLayout.StructuralRows, new[] { source }));

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(2, page.Dataset.Mappings.Count(mapping =>
            mapping.DetailedIdentities.Any(identity => identity.InformationType == "code")));
        Assert.AreEqual(2, page.Dataset.Columns.Count(column =>
            string.Equals(column.EffectiveName, "code", StringComparison.Ordinal)));
        var codeCells = page.Rows.SelectMany(row => row.Cells)
            .Where(cell => cell.Values.Any(value => value.SourceInformationType == "code"))
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "attribute-value", "element-value" },
            codeCells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray());
        Assert.IsFalse(codeCells.Any(cell => cell.HasConflict));
        Assert.AreEqual(2, codeCells.Select(cell => cell.ColumnIdentity.FieldKey).Distinct().Count());
    }

    [TestMethod]
    public async Task DetailedPathVariantsContributeThroughOneLogicalTypedMapping()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "path-variants.xml",
            "<record><buyer><code>A</code></buyer><seller><code>B</code></seller></record>");
        var interpreted = await workspace.InterpretAsync(source);
        var codeIdentities = Identities(set, interpreted)
            .Where(identity => identity.CandidateKind == SourceValueCandidateKind.Element
                && identity.InformationType == "code")
            .ToArray();
        var specification = workspace.Specification(
            set,
            "Path variants",
            RepeatedDataLayout.StructuralRows,
            [source],
            codeIdentities,
            new Dictionary<DiscoveryInformationIdentity, string>());

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        Assert.HasCount(2, codeIdentities);
        Assert.HasCount(1, page.Dataset.Mappings);
        Assert.HasCount(2, page.Dataset.Mappings[0].DetailedIdentities);
        Assert.HasCount(1, page.Dataset.Columns);
        CollectionAssert.AreEquivalent(
            new[] { "A", "B" },
            page.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values)
                .Select(value => value.Value).ToArray());
        Assert.AreEqual(2, page.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values)
            .Select(value => value.DetailedIdentity.StructuralPath).Distinct().Count());
    }

    [TestMethod]
    public async Task SparseRepeatedRecordsRemainDistinctSemanticRows()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(
            set,
            "sparse.xml",
            "<records><record><code>A</code><price>10</price></record><record><code>B</code></record><record><price>30</price></record></records>");
        var specification = await workspace.SpecificationAsync(
            (set, "Sparse", RepeatedDataLayout.StructuralRows, new[] { source }));

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);

        Assert.AreEqual(3, page.TotalRowCount);
        Assert.IsTrue(page.Rows.Any(row => Values(row).Contains("A") && Values(row).Contains("10")));
        Assert.IsTrue(page.Rows.Any(row => Values(row).SequenceEqual(["B"])));
        Assert.IsTrue(page.Rows.Any(row => Values(row).SequenceEqual(["30"])));
        Assert.IsFalse(page.Rows.Any(row => Values(row).Contains("B") && Values(row).Contains("30")));
    }

    [TestMethod]
    public async Task NestedRepeatedRowsExposeNestedStructuralRecordHierarchy()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var fixture = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "StructuralDiscovery", "flattening-nested-records.xml");
        var source = workspace.Source(set, "nested.xml", File.ReadAllText(fixture));
        var interpreted = await workspace.InterpretAsync(source);
        var identities = Identities(set, interpreted)
            .Where(identity => identity.InformationType is "code" or "price")
            .ToArray();
        var specification = workspace.Specification(
            set,
            "Nested",
            RepeatedDataLayout.StructuralRows,
            [source],
            identities,
            new Dictionary<DiscoveryInformationIdentity, string>());

        var result = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var page = await workspace.PageAsync(result.PublishedGeneration!, set);
        var firstVariant = page.Rows.Single(row =>
            Values(row).Contains("A") && Values(row).Contains("10"));

        StringAssert.Contains(firstVariant.RecordHierarchy, "/catalog[1]/product[1]");
        StringAssert.Contains(firstVariant.RecordHierarchy, "/variants[");
        StringAssert.Contains(firstVariant.RecordHierarchy, "/variant[1]");
        Assert.IsFalse(firstVariant.RecordHierarchy.Contains(
            firstVariant.Source.SourceId.ToString(), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedCandidateDoesNotReplacePublishedHierarchyGeneration()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var valid = workspace.Source(set, "valid.xml", "<root><value>kept</value></root>");
        var firstSpecification = await workspace.SpecificationAsync(
            (set, "Stable", RepeatedDataLayout.StructuralRows, new[] { valid }));
        var first = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), firstSpecification);
        var previousId = first.PublishedGeneration!.OperationId;
        var invalid = workspace.Source(set, "invalid.xml", "<root><value>");
        var invalidSpecification = workspace.Specification(
            set, "Stable", RepeatedDataLayout.StructuralRows, [valid, invalid],
            firstSpecification.Datasets[0].Fields.SelectMany(field => field.DetailedIdentities).ToArray(),
            new Dictionary<DiscoveryInformationIdentity, string>());

        var replacement = await workspace.Service.BuildAsync(
            OperationCorrelation.CreateNew(), invalidSpecification);
        var retained = await workspace.Repository.ReadPublishedDatabaseGenerationAsync();

        Assert.IsFalse(replacement.Accepted);
        Assert.AreEqual(previousId, retained?.OperationId);
        Assert.AreEqual(OperationOutcome.Failed, replacement.Completion.Outcome);
        Assert.AreEqual(
            OperationItemState.Failed,
            replacement.Completion.Items.Single(item =>
                item.ItemId == invalid.SourceId.ToString()).State);
        Assert.AreEqual(
            "kept",
            (await workspace.PageAsync(retained!, set)).Rows.Single().Cells.Single().Values.Single().Value);
    }

    [TestMethod]
    public async Task CancelledCandidatePreservesPreviousPublishedGeneration()
    {
        using var workspace = new Workspace();
        var set = SourceSetId.CreateNew();
        var source = workspace.Source(set, "source.xml", "<root><value>kept</value></root>");
        var specification = await workspace.SpecificationAsync(
            (set, "Stable", RepeatedDataLayout.StructuralRows, new[] { source }));
        var first = await workspace.Service.BuildAsync(OperationCorrelation.CreateNew(), specification);
        var cancellation = new CooperativeOperationCancellation(new NullHistory());
        var blocking = new BlockingInterpreter();
        var replacementService = new DatabaseGenerationService(
            workspace.Repository, blocking, new HierarchyFlatteningEngine(), cancellation,
            NullLogger<DatabaseGenerationService>.Instance);
        var correlation = OperationCorrelation.CreateNew();

        var replacementTask = replacementService.BuildAsync(correlation, specification);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(cancellation.RequestCancellation(correlation.OperationId).Accepted);
        var replacement = await replacementTask;

        Assert.IsFalse(replacement.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, replacement.Completion.Outcome);
        Assert.AreEqual(
            first.PublishedGeneration!.OperationId,
            (await workspace.Repository.ReadPublishedDatabaseGenerationAsync())!.OperationId);
    }

    [TestMethod]
    public async Task SchemaFourMigrationInvalidatesLegacyDerivedPublication()
    {
        using var workspace = new Workspace();
        await workspace.Repository.InitializeAsync();
        var generation = OperationId.CreateNew().ToString();
        await using (var connection = new SqliteConnection(
                         $"Data Source={workspace.Repository.DatabasePath};Mode=ReadWrite"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO database_generations (generation_id, operation_id, created_utc, generation_state)
                VALUES ('{generation}', '{generation}', '{DateTimeOffset.UtcNow:O}', 2);
                INSERT INTO database_generation_sources (generation_id, source_ordinal, source_id)
                VALUES ('{generation}', 0, '{SourceId.CreateNew()}');
                INSERT INTO database_columns (generation_id, column_ordinal, database_tag_name)
                VALUES ('{generation}', 0, 'legacy');
                INSERT INTO database_column_sources (
                    generation_id, database_tag_name, source_information_ordinal, source_information_type)
                VALUES ('{generation}', 'legacy', 0, 'legacy');
                INSERT INTO database_publication (singleton_id, generation_id) VALUES (1, '{generation}');
                DROP TABLE hierarchy_extraction_publication;
                DROP TABLE hierarchy_extraction_cell_values;
                DROP TABLE hierarchy_extraction_cells;
                DROP TABLE hierarchy_extraction_rows;
                DROP TABLE hierarchy_extraction_columns;
                DROP TABLE hierarchy_extraction_datasets;
                DROP TABLE hierarchy_extraction_results;
                DROP TABLE hierarchy_database_publication;
                DROP TABLE hierarchy_database_cell_values;
                DROP TABLE hierarchy_database_cells;
                DROP TABLE hierarchy_database_rows;
                DROP TABLE hierarchy_database_columns;
                DROP TABLE hierarchy_database_mappings;
                DROP TABLE hierarchy_database_sources;
                DROP TABLE hierarchy_database_datasets;
                DROP TABLE hierarchy_database_generations;
                PRAGMA user_version = 3;
                """;
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var reopened = new StructuredInformationRepository(ApplicationPaths.FromLocalApplicationData(
            Path.Combine(workspace.Root, "LocalAppData")));

        await reopened.InitializeAsync();

        Assert.AreEqual(StructuredInformationRepository.CurrentSchemaVersion,
            await ReadSchemaVersionAsync(reopened.DatabasePath));
        Assert.IsNull(await reopened.ReadPublishedDatabaseGenerationAsync());
    }

    private static IReadOnlyList<string> Values(DatabaseReviewRow row) =>
        row.Cells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray();

    private static DiscoveryInformationIdentity[] Identities(
        SourceSetId set,
        InterpretedSourceDocument document) =>
        document.Values.Where(value => value.Lineage is not null)
            .Select(value => HierarchySourceOccurrence.FromInterpretedValue(set, value).Identity)
            .Distinct().ToArray();

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "CIA.SPR138.Tests", Guid.NewGuid().ToString("N"));
        private readonly SourceInterpreter _interpreter;

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(ApplicationPaths.FromLocalApplicationData(
                Path.Combine(_root, "LocalAppData")));
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            Service = new DatabaseGenerationService(
                Repository,
                _interpreter,
                new HierarchyFlatteningEngine(),
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<DatabaseGenerationService>.Instance);
        }

        public StructuredInformationRepository Repository { get; }

        public string Root => _root;

        public DatabaseGenerationService Service { get; }

        public LoadedSourceContract Source(SourceSetId set, string name, string content)
        {
            var path = Path.Combine(_root, set.ToString(), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new LoadedSourceContract(
                SourceId.CreateNew(), set, path, true, LoadedSourceStatus.Ready, LoadedSourceKind.XmlFile);
        }

        public async Task<InterpretedSourceDocument> InterpretAsync(LoadedSourceContract source)
        {
            var result = await _interpreter.InterpretAsync(source);
            Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
            return result.Source!;
        }

        public async Task<DatabaseBuildSpecification> SpecificationAsync(
            params (SourceSetId Set, string Name, RepeatedDataLayout Layout, LoadedSourceContract[] Sources)[] sets)
        {
            var datasets = new List<DatabaseDatasetBuildSpecification>();
            foreach (var set in sets)
            {
                var identities = new List<DiscoveryInformationIdentity>();
                foreach (var source in set.Sources)
                {
                    identities.AddRange(Identities(set.Set, await InterpretAsync(source)));
                }
                var config = identities.Distinct().Select(identity =>
                    new DiscoveryConfigurationItem(identity, DiscoveryInformationDisposition.Selected)).ToArray();
                datasets.Add(new DatabaseDatasetBuildSpecification(
                    set.Set, set.Name, datasets.Count + 1, set.Layout, set.Sources,
                    DatabaseTagMapper.CreateFieldMappings(
                        set.Set, config, new Dictionary<DiscoveryInformationIdentity, string>())));
            }
            return new DatabaseBuildSpecification(datasets);
        }

        public DatabaseBuildSpecification Specification(
            SourceSetId set,
            string name,
            RepeatedDataLayout layout,
            IReadOnlyList<LoadedSourceContract> sources,
            IReadOnlyList<DiscoveryInformationIdentity> identities,
            IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides) =>
            new([new DatabaseDatasetBuildSpecification(
                set, name, 1, layout, sources,
                DatabaseTagMapper.CreateFieldMappings(
                    set,
                    identities.Select(identity => new DiscoveryConfigurationItem(
                        identity, DiscoveryInformationDisposition.Selected)),
                    overrides))]);

        public async Task<DatabaseReviewPage> PageAsync(
            DatabaseGenerationSummary generation,
            SourceSetId set) =>
            (await Repository.ReadPublishedDatabasePageAsync(new DatabaseReviewQuery(
                generation.OperationId, set, 1, 100, null, DatabaseRowInclusionFilter.All)))!;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class BlockingInterpreter : ISourceInterpreter
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SourceInterpretationResult> InterpretAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class NullHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(CIA.Contracts.Diagnostics.ProcessingAttemptRecord record) { }

        public void RecordDiagnostic(CIA.Contracts.Diagnostics.ProcessingDiagnosticRecord record) { }
    }
}
