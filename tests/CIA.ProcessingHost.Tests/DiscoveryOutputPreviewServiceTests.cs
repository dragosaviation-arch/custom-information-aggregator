using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DiscoveryOutputPreviewServiceTests
{
    [TestMethod]
    public async Task PreviewUsesDatabaseProjectionForRootRepeatedAndNestedFamiliesWithoutPublication()
    {
        using var workspace = new Workspace();
        var source = workspace.Source("catalog.xml", """
            <catalog>
              <label>Current catalog</label>
              <book><id>B1</id><review><score>5</score></review><review><score>4</score></review></book>
              <book><id>B2</id><review><score>3</score></review></book>
            </catalog>
            """);
        var discovery = await workspace.DiscoverAsync(source);
        var dataset = CreateDataset(discovery, source, RepeatedDataLayout.StructuralRows);
        var selected = discovery.Information.Single(item => item.InformationType == "id");

        var interpreted = (await workspace.Interpreter.InterpretAsync(source)).Source!;
        var authoritative = await workspace.Projector.ProjectAsync(
            dataset,
            source,
            interpreted);
        _ = await workspace.Projector.ProjectAsync(
            dataset,
            source,
            interpreted,
            50);
        var result = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            dataset,
            selected,
            maximumRows: 50));

        Assert.IsTrue(result.Accepted,
            $"{result.Failure?.Code}: {result.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsNotNull(result.Preview);
        Assert.IsGreaterThanOrEqualTo(3, result.Preview.Groups.Count);
        var previewValues = result.Preview.Groups.SelectMany(group => group.Rows)
            .SelectMany(row => row.Cells)
            .SelectMany(cell => cell.Values)
            .ToArray();
        Assert.IsTrue(previewValues.Contains("B1"));
        Assert.IsTrue(previewValues.Contains("5"));
        Assert.IsTrue(previewValues.Contains("4"));
        Assert.IsFalse(previewValues.Contains("B2"));
        Assert.IsGreaterThan(
            result.Preview.Groups.Sum(group => group.Rows.Count),
            authoritative.Rows.Count);
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    public async Task SelectedOccurrenceScopesActualValuesAndMissingCellsRemainTruthful()
    {
        using var workspace = new Workspace();
        var source = workspace.Source("occurrences.xml", """
            <root>
              <record><id>A</id><title>Book A</title></record>
              <record><id>B</id><title>Book B</title></record>
              <record><id>C</id></record>
            </root>
            """);
        var discovery = await workspace.DiscoverAsync(source);
        var dataset = CreateDataset(discovery, source, RepeatedDataLayout.StructuralRows);
        var selected = discovery.Information.Single(item => item.InformationType == "id");
        var outputSetId = OutputSetId.CreateNew();

        var first = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery, dataset, selected, 20, globalOrdinal: 1, outputSetId: outputSetId));
        var second = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery, dataset, selected, 20, globalOrdinal: 2, outputSetId: outputSetId));
        var missing = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery, dataset, selected, 20, globalOrdinal: 3, outputSetId: outputSetId));

        Assert.IsTrue(first.Accepted);
        Assert.IsTrue(second.Accepted);
        Assert.IsTrue(missing.Accepted);
        Assert.AreEqual(outputSetId, first.Preview!.OutputSetId);
        CollectionAssert.Contains(Values(first), "Book A");
        CollectionAssert.DoesNotContain(Values(first), "Book B");
        CollectionAssert.Contains(Values(second), "Book B");
        CollectionAssert.DoesNotContain(Values(second), "Book A");
        var missingGroup = missing.Preview!.Groups.Single(group =>
            group.Columns.Any(column => column.Header == "title"));
        Assert.IsTrue(missingGroup.Columns.Any(column => column.Header == "title"));
        Assert.IsFalse(missingGroup.Rows.SelectMany(row => row.Cells)
            .SelectMany(cell => cell.Values).Any(value => value == "title"));
        Assert.IsFalse(Values(missing).Any(value => value.StartsWith("Book ", StringComparison.Ordinal)));
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    public async Task SiblingRepeatedFamiliesStaySeparateAndPreviewRowsAreBounded()
    {
        using var workspace = new Workspace();
        var records = string.Join(string.Empty, Enumerable.Range(1, 40).Select(index => $"""
            <record>
              <id>{index}</id>
              <left><entry><value>L{index}-1</value></entry><entry><value>L{index}-2</value></entry></left>
              <right><entry><value>R{index}-1</value></entry><entry><value>R{index}-2</value></entry></right>
            </record>
            """));
        var source = workspace.Source("siblings.xml", $"<root>{records}</root>");
        var discovery = await workspace.DiscoverAsync(source);
        var dataset = CreateDataset(discovery, source, RepeatedDataLayout.StructuralRows);
        var selected = discovery.Information.Single(item => item.InformationType == "id");

        var result = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            dataset,
            selected,
            maximumRows: 1));

        Assert.IsTrue(result.Accepted,
            $"{result.Failure?.Code}: {result.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsNotNull(result.Preview);
        Assert.IsTrue(result.Preview.Groups.All(group => group.Rows.Count <= 1));
        Assert.IsTrue(result.Preview.Groups.Any(group => group.IsTruncated));
        foreach (var group in result.Preview.Groups)
        {
            var values = group.Rows.SelectMany(row => row.Cells)
                .SelectMany(cell => cell.Values).ToArray();
            Assert.IsFalse(
                values.Any(value => value.StartsWith('L'))
                && values.Any(value => value.StartsWith('R')),
                "Unrelated sibling output families were cross-paired.");
        }

        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    public async Task OccurrenceAndLayoutRequestsRemainSourceSpecificAndEphemeral()
    {
        using var workspace = new Workspace();
        var first = workspace.Source(
            "first.xml",
            "<root><record><id>A</id><value>A1</value><value>A2</value></record></root>");
        var second = workspace.Source(
            "second.xml",
            "<root><record><id>B</id><value>B1</value><value>B2</value></record></root>");
        var discovery = await workspace.DiscoverAsync(first, second);
        var selected = discovery.Information.Single(item => item.InformationType == "id");
        var structuralDataset = CreateDataset(
            discovery,
            [first, second],
            RepeatedDataLayout.StructuralRows);
        var numberedDataset = CreateDataset(
            discovery,
            [first, second],
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns);

        var structural = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            structuralDataset,
            selected,
            maximumRows: 20,
            globalOrdinal: 2));
        var numbered = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            numberedDataset,
            selected,
            maximumRows: 20,
            globalOrdinal: 2));

        Assert.IsTrue(structural.Accepted,
            $"{structural.Failure?.Code}: {structural.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsTrue(numbered.Accepted, $"{numbered.Failure?.Code}: {numbered.Failure?.Description}");
        Assert.AreEqual(second.SourceId, structural.Preview?.SourceId);
        Assert.AreEqual(second.SourceId, numbered.Preview?.SourceId);
        Assert.AreEqual(RepeatedDataLayout.StructuralRows, structural.Preview?.RepeatedDataLayout);
        Assert.AreEqual(
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            numbered.Preview?.RepeatedDataLayout);
        Assert.IsTrue(structural.Preview!.Groups.SelectMany(group => group.Rows)
            .SelectMany(row => row.Cells).SelectMany(cell => cell.Values)
            .All(value => !value.StartsWith('A')));
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    [TestCategory("Spr224")]
    public async Task SourceFileProjectionPreservesStructuralSlotsAndPaginatesWholeSource()
    {
        using var workspace = new Workspace();
        var first = workspace.Source(
            "source-file-rows-a.xml",
            File.ReadAllText(ResolveFixturePath("source-file-rows-a.xml")));
        var second = workspace.Source(
            "source-file-rows-b.xml",
            File.ReadAllText(ResolveFixturePath("source-file-rows-b.xml")));
        var blank = workspace.Source(
            "source-file-rows-blank.xml",
            File.ReadAllText(ResolveFixturePath("source-file-rows-blank.xml")));
        var sources = new[] { first, second, blank };
        var discovery = await workspace.DiscoverAsync(sources);
        var selectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "code", "title", "name", "role", "value", "keyword", "label", "late"
        };
        var dataset = CreateSourceFileDataset(
            discovery,
            sources,
            information => selectedNames.Contains(information.InformationType)
                || information.Identity.StructuralPath.Contains("label", StringComparison.Ordinal));
        var interpreted = (await workspace.Interpreter.InterpretAsync(first)).Source!;

        var projection = await workspace.Projector.ProjectAsync(dataset, first, interpreted);

        Assert.HasCount(1, projection.Rows);
        var row = projection.Rows.Single();
        var rowling = row.Cells.Single(cell => cell.Values.Any(value => value.Value == "Rowling"));
        var illustrator = row.Cells.Single(cell =>
            cell.Values.Any(value => value.Value == "Illustrator"));
        var mary = row.Cells.Single(cell =>
            cell.Values.Any(value => value.Value == "Mary GrandPré"));
        Assert.AreEqual(mary.ColumnIdentity.RepeatCoordinates, illustrator.ColumnIdentity.RepeatCoordinates);
        Assert.AreNotEqual(rowling.ColumnIdentity.RepeatCoordinates, illustrator.ColumnIdentity.RepeatCoordinates);
        var role = dataset.Fields.Single(field =>
            field.DetailedIdentities.Single().InformationType == "role");
        Assert.IsTrue(projection.Columns.Any(column =>
            string.Equals(column.FieldKey, role.FieldKey, StringComparison.Ordinal)
            && column.RepeatCoordinates.Equals(rowling.ColumnIdentity.RepeatCoordinates)),
            $"Role key {role.FieldKey}; origin {role.OriginatingRowFamilyId}; Rowling key {rowling.ColumnIdentity.FieldKey}; coordinates {rowling.ColumnIdentity.RepeatCoordinates}; columns: {string.Join(", ", projection.Columns.Select(column => $"{column.FieldKey}={column.RepeatCoordinates}"))}");
        Assert.IsFalse(row.Cells.Any(cell =>
            string.Equals(cell.ColumnIdentity.FieldKey, role.FieldKey, StringComparison.Ordinal)
            && cell.ColumnIdentity.RepeatCoordinates == rowling.ColumnIdentity.RepeatCoordinates
            && cell.Values.Any(value => !string.IsNullOrEmpty(value.Value))));
        Assert.IsTrue(row.Cells.Any(cell => cell.Values.Any(value => value.Value == "Label A")));
        Assert.IsTrue(row.Cells.Any(cell => cell.Values.Any(value => value.Value == "Label B")));
        Assert.AreNotEqual(
            row.Cells.Single(cell => cell.Values.Any(value => value.Value == "Label A"))
                .ColumnIdentity.FieldKey,
            row.Cells.Single(cell => cell.Values.Any(value => value.Value == "Label B"))
                .ColumnIdentity.FieldKey);
        Assert.IsTrue(row.Cells.Any(cell => cell.Values.Any(value => value.Value == "B1")));

        var roleOnly = CreateSourceFileDataset(
            discovery,
            sources,
            information => information.InformationType == "role");
        var roleProjection = await workspace.Projector.ProjectAsync(roleOnly, first, interpreted);
        var roleValue = roleProjection.Rows.Single().Cells.Single(cell =>
            cell.Values.Any(value => value.Value == "Illustrator"));
        Assert.AreEqual(
            illustrator.ColumnIdentity.RepeatCoordinates,
            roleValue.ColumnIdentity.RepeatCoordinates,
            "Selecting fewer fields must not renumber structural instances.");
        Assert.IsTrue(roleProjection.Columns.Any(column =>
            column.RepeatCoordinates.Equals(rowling.ColumnIdentity.RepeatCoordinates)));

        var blankInterpretation = (await workspace.Interpreter.InterpretAsync(blank)).Source!;
        var blankProjection = await workspace.Projector.ProjectAsync(
            dataset,
            blank,
            blankInterpretation);
        Assert.HasCount(1, blankProjection.Rows);
        Assert.AreEqual(blank.SourceId, blankProjection.Rows.Single().Source.SourceId);
        Assert.HasCount(0, blankProjection.Rows.Single().Cells);

        var name = discovery.Information.First(information =>
            information.InformationType == "name");
        var firstPage = await workspace.Preview.GetPreviewAsync(CreateSourceFilePreviewRequest(
            discovery,
            dataset,
            name,
            columnStartIndex: 0));
        var secondPage = await workspace.Preview.GetPreviewAsync(CreateSourceFilePreviewRequest(
            discovery,
            dataset,
            name,
            columnStartIndex: DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage));
        Assert.IsTrue(firstPage.Accepted, firstPage.Failure?.Description);
        Assert.IsTrue(secondPage.Accepted, secondPage.Failure?.Description);
        Assert.AreEqual(first.SourceId, firstPage.Preview!.SourceId);
        Assert.AreEqual(
            DiscoveryOutputProjectionMode.OneRowPerSourceFile,
            firstPage.Preview.ProjectionMode);
        var firstGroup = firstPage.Preview.Groups.Single();
        var secondGroup = secondPage.Preview!.Groups.Single();
        Assert.HasCount(DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage, firstGroup.Columns);
        Assert.IsGreaterThan(DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage, firstGroup.TotalColumnCount);
        Assert.AreEqual(firstGroup.TotalColumnCount, secondGroup.TotalColumnCount);
        Assert.AreEqual(
            DiscoveryOutputPreviewLimits.SourceFileColumnsPerPage,
            secondGroup.ColumnStartIndex);
        Assert.IsFalse(firstGroup.IsTruncated);
        Assert.IsFalse(secondGroup.IsTruncated);
        Assert.HasCount(1, firstGroup.Rows);
        Assert.HasCount(1, secondGroup.Rows);
        Assert.IsFalse(firstGroup.Columns.Select(column => column.Identity)
            .Intersect(secondGroup.Columns.Select(column => column.Identity)).Any());

        await using (var ipc = new MemoryStream())
        {
            await LengthPrefixedJsonMessageFramer.WriteAsync(
                ipc,
                new BuildDatabaseCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    OperationCorrelation.CreateNew(),
                    new DatabaseBuildSpecification([dataset])));
            ipc.Position = 0;
            var roundTrip = Assert.IsInstanceOfType<BuildDatabaseCommand>(
                await LengthPrefixedJsonMessageFramer.ReadAsync(ipc));
            var roundTripDataset = roundTrip.Specification.Datasets.Single();
            Assert.AreEqual(
                DiscoveryOutputProjectionMode.OneRowPerSourceFile,
                roundTripDataset.ProjectionMode);
            CollectionAssert.AreEqual(
                dataset.SourceProjectionRepeatedPaths.ToArray(),
                roundTripDataset.SourceProjectionRepeatedPaths.ToArray());
            CollectionAssert.AreEqual(
                dataset.Fields.Select(field => field.OriginatingRowFamilyId).ToArray(),
                roundTripDataset.Fields.Select(field => field.OriginatingRowFamilyId).ToArray());
        }

        var database = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            new DatabaseBuildSpecification([dataset]));
        Assert.IsTrue(database.Accepted, database.Failure?.Description);
        var publishedDataset = database.PublishedGeneration!.Datasets.Single();
        Assert.AreEqual(dataset.OutputSetId, publishedDataset.OutputSetId);
        Assert.AreEqual(sources.Length, publishedDataset.RowCount);
        Assert.HasCount(projection.Columns.Count, publishedDataset.Columns);
        var family = publishedDataset.RowFamilies.Single();
        var review = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(
                database.PublishedGeneration.OperationId,
                dataset.OutputSetId!.Value,
                dataset.ParentSourceSetId,
                family.Id,
                1,
                DatabaseReviewLimits.MaximumRowsPerPage,
                null,
                DatabaseRowInclusionFilter.All));
        Assert.IsNotNull(review);
        Assert.HasCount(sources.Length, review.Rows);
        Assert.HasCount(0, review.Rows.Single(candidate =>
            candidate.Source.SourceId == blank.SourceId).Cells);

        var extraction = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(),
            database.PublishedGeneration);
        Assert.IsTrue(
            extraction.Accepted,
            $"{extraction.Failure?.Description} {workspace.ExtractionLogger.Exception}");
        Assert.AreEqual(sources.Length, extraction.PublishedResult!.Datasets.Single().RowCount);
        var extractedRows = new List<ExtractionResultRow>();
        await foreach (var extractedRow in workspace.Repository.StreamPublishedExtractionRowsAsync(
            extraction.PublishedResult.OperationId,
            dataset.OutputSetId.Value,
            family.Id))
        {
            extractedRows.Add(extractedRow);
        }

        Assert.HasCount(sources.Length, extractedRows);
        Assert.HasCount(0, extractedRows.Single(candidate =>
            candidate.Source.SourceId == blank.SourceId).Cells);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            new DatabaseBuildSpecification([dataset]),
            cancellation.Token);
        Assert.IsFalse(cancelled.Accepted);
        Assert.AreEqual(
            database.PublishedGeneration.OperationId,
            (await workspace.Repository.ReadPublishedDatabaseGenerationAsync())!.OperationId,
            "A cancelled source-file projection must retain the previously published Database.");

        File.Delete(first.Path);
        var failed = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            new DatabaseBuildSpecification([dataset]));
        Assert.IsFalse(failed.Accepted);
        Assert.AreEqual(
            database.PublishedGeneration.OperationId,
            (await workspace.Repository.ReadPublishedDatabaseGenerationAsync())!.OperationId,
            "A failed source-file projection must retain the previously published Database.");
    }

    private static DiscoveryOutputPreviewRequest CreateRequest(
        DiscoveryHostResult discovery,
        DatabaseDatasetBuildSpecification dataset,
        DiscoveredInformation selected,
        int maximumRows,
        int globalOrdinal = 1,
        OutputSetId? outputSetId = null)
    {
        return new DiscoveryOutputPreviewRequest(
            outputSetId ?? OutputSetId.CreateNew(),
            new DiscoveryOccurrenceLookup(
                discovery.Completion.Correlation.OperationId,
                [selected.Identity],
                globalOrdinal,
                selected.TotalOccurrenceCount),
            new DiscoveryOutputProjectionSpecification(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.RepeatedDataLayout,
                dataset.RowFamilies,
                dataset.Fields),
            maximumRows);
    }

    private static DiscoveryOutputPreviewRequest CreateSourceFilePreviewRequest(
        DiscoveryHostResult discovery,
        DatabaseDatasetBuildSpecification dataset,
        DiscoveredInformation selected,
        int columnStartIndex)
    {
        return new DiscoveryOutputPreviewRequest(
            dataset.OutputSetId!.Value,
            new DiscoveryOccurrenceLookup(
                discovery.Completion.Correlation.OperationId,
                [selected.Identity],
                1,
                selected.TotalOccurrenceCount),
            new DiscoveryOutputProjectionSpecification(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.RepeatedDataLayout,
                dataset.RowFamilies,
                dataset.Fields,
                dataset.ProjectionMode,
                dataset.SourceProjectionRepeatedPaths),
            maximumRowsPerGroup: 20,
            columnStartIndex);
    }

    private static string[] Values(DiscoveryOutputPreviewHostResult result) => result.Preview!.Groups
        .SelectMany(group => group.Rows)
        .SelectMany(row => row.Cells)
        .SelectMany(cell => cell.Values)
        .ToArray();

    private static DatabaseDatasetBuildSpecification CreateDataset(
        DiscoveryHostResult discovery,
        LoadedSourceContract source,
        RepeatedDataLayout layout) => CreateDataset(discovery, [source], layout);

    private static DatabaseDatasetBuildSpecification CreateDataset(
        DiscoveryHostResult discovery,
        IReadOnlyList<LoadedSourceContract> sources,
        RepeatedDataLayout layout)
    {
        var sourceSetId = sources[0].SourceSetId;
        var assignments = discovery.Information.ToDictionary(
            item => item.Identity,
            item => item.RowFamilyId!.Value);
        var configuration = discovery.Information.Select(item =>
            new DiscoveryConfigurationItem(
                item.Identity,
                DiscoveryInformationDisposition.Selected)).ToArray();
        var fields = DatabaseTagMapper.CreateFieldMappings(
            sourceSetId,
            configuration,
            new Dictionary<DiscoveryInformationIdentity, string>(),
            assignments);
        var selectedFamilies = fields.Select(field => field.RowFamilyId).ToHashSet();
        var families = discovery.FamilyCatalog!.Families
            .Where(family => selectedFamilies.Contains(family.Id))
            .ToArray();
        return new DatabaseDatasetBuildSpecification(
            sourceSetId,
            "Set 1",
            1,
            layout,
            sources,
            families,
            fields);
    }

    private static DatabaseDatasetBuildSpecification CreateSourceFileDataset(
        DiscoveryHostResult discovery,
        IReadOnlyList<LoadedSourceContract> sources,
        Func<DiscoveredInformation, bool> include)
    {
        var sourceSetId = sources[0].SourceSetId;
        var selected = discovery.Information
            .Where(information => information.Identity.CandidateKind
                != SourceValueCandidateKind.ElementScope)
            .Where(include)
            .ToArray();
        var configuration = new DiscoveryConfigurationSnapshot(
            selected.Select(information => new DiscoveryConfigurationItem(
                information.Identity,
                DiscoveryInformationDisposition.Selected)).ToArray(),
            [new SourceSetDiscoveryConfiguration(
                sourceSetId,
                DiscoveryOutputLayoutDefaults.Recommended)]);
        var assignments = discovery.Information.ToDictionary(
            information => information.Identity,
            information => information.RowFamilyId!.Value);
        var familyCatalog = discovery.FamilyCatalog!;
        var parent = DatabaseBuildSpecificationFactory.Create(
            configuration,
            new Dictionary<DiscoveryInformationIdentity, string>(),
            sources,
            new Dictionary<SourceSetId, string> { [sourceSetId] = "Set 1" },
            familyCatalog,
            assignments);
        var parentDataset = parent.Datasets.Single();
        var outputSet = new DiscoveryOutputSetDefinition(
            OutputSetId.CreateNew(),
            sourceSetId,
            new DiscoveryOutputModelKey("source-file-projection:v1"),
            "Set 1 - Source files",
            1,
            1,
            isEnabled: true,
            parentDataset.RowFamilies.Select(family => family.Id).ToArray(),
            DiscoveryOutputProjectionMode.OneRowPerSourceFile);
        var outputPlan = new DiscoveryOutputPlanSnapshot(
            [new DiscoveryParentOutputPlan(
                sourceSetId,
                [outputSet],
                DiscoveryOutputProjectionMode.OneRowPerSourceFile)]);
        return DatabaseBuildSpecificationFactory.CreateApprovedOutputSets(
            configuration,
            new Dictionary<DiscoveryInformationIdentity, string>(),
            sources,
            new Dictionary<SourceSetId, string> { [sourceSetId] = "Set 1" },
            familyCatalog,
            assignments,
            outputPlan).Datasets.Single();
    }

    private static string ResolveFixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "StructuralDiscovery",
        fileName);

    private static IEnumerable<string> Describe(IEnumerable<DatabaseReviewRow> rows) =>
        rows.OrderBy(row => row.SemanticIdentity!.RowFamilyId)
            .ThenBy(row => row.Ordinal)
            .Select(row => string.Join("|", row.Cells
                .OrderBy(cell => cell.ColumnIdentity.FieldKey, StringComparer.Ordinal)
                .ThenBy(cell => cell.ColumnIdentity.RepeatCoordinates)
                .SelectMany(cell => cell.Values.Select(value =>
                    $"{cell.ColumnIdentity.FieldKey}:{cell.ColumnIdentity.RepeatCoordinates}:{value.Value}"))));

    private static IEnumerable<string> Describe(IEnumerable<DiscoveryOutputPreviewRow> rows) =>
        rows.OrderBy(row => row.RecordIdentity.RowFamilyId)
            .ThenBy(row => row.Ordinal)
            .Select(row => string.Join("|", row.Cells
                .OrderBy(cell => cell.ColumnIdentity.FieldKey, StringComparer.Ordinal)
                .ThenBy(cell => cell.ColumnIdentity.RepeatCoordinates)
                .SelectMany(cell => cell.Values.Select(value =>
                    $"{cell.ColumnIdentity.FieldKey}:{cell.ColumnIdentity.RepeatCoordinates}:{value}"))));

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR207.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SourceSetId _sourceSetId = SourceSetId.CreateNew();

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData")));
            Interpreter = new SourceInterpreter(
                [],
                new GenericXmlElementValueSourceAdapter(),
                NullLogger<SourceInterpreter>.Instance);
            Projector = new DatabaseSourceProjectionService(new HierarchyFlatteningEngine());
            PreviewLogger = new CapturingLogger<DiscoveryOutputPreviewService>();
            Preview = new DiscoveryOutputPreviewService(
                Repository,
                Interpreter,
                Projector,
                PreviewLogger);
            var cancellation = new CooperativeOperationCancellation(new NullHistory());
            Database = new DatabaseGenerationService(
                Repository,
                Interpreter,
                Projector,
                cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
            ExtractionLogger = new CapturingLogger<DatabaseExtractionService>();
            Extraction = new DatabaseExtractionService(
                Repository,
                cancellation,
                ExtractionLogger);
        }

        public StructuredInformationRepository Repository { get; }

        public SourceInterpreter Interpreter { get; }

        public DatabaseSourceProjectionService Projector { get; }

        public DiscoveryOutputPreviewService Preview { get; }

        public DatabaseGenerationService Database { get; }

        public DatabaseExtractionService Extraction { get; }

        public CapturingLogger<DatabaseExtractionService> ExtractionLogger { get; }

        public CapturingLogger<DiscoveryOutputPreviewService> PreviewLogger { get; }

        public LoadedSourceContract Source(string fileName, string content)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllText(path, content);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                _sourceSetId,
                path,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public async Task<DiscoveryHostResult> DiscoverAsync(
            params LoadedSourceContract[] sources)
        {
            var discovery = new DiscoveryService(
                Interpreter,
                new SourceOccurrenceReader(
                    [],
                    new GenericXmlElementValueSourceAdapter(),
                    NullLogger<SourceOccurrenceReader>.Instance),
                Repository);
            var result = await discovery.RunAsync(OperationCorrelation.CreateNew(), sources);
            Assert.IsTrue(result.Accepted);
            return result;
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
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
