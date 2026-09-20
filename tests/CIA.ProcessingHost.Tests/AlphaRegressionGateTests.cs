using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.Desktop.Presentation;
using CIA.Desktop.Discovery;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Export;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class AlphaRegressionGateTests
{
    private const string GateCategory = "AlphaRegressionGate";

    [TestMethod]
    [TestCategory(GateCategory)]
    public async Task PermanentFixtureDiscoveryKeepsLogicalAndDetailedSetIdentity()
    {
        using var workspace = new AlphaWorkspace();
        var fixture = workspace.LoadFixture();
        var result = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [.. fixture.AllSources, fixture.MalformedSource]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, result.Completion.Outcome);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(fixture.MalformedSource.SourceId, result.Issues[0].SourceId);

        var logical = DiscoveredInformationItemViewModel.CreateLogicalItems(
                result.Information,
                sourceSetId => fixture.NameOf(sourceSetId),
                _ => DiscoveryInformationDisposition.Selected,
                _ => null)
            .ToArray();
        var setAName = logical.Single(item =>
            item.SourceSetId == fixture.SetA
            && item.CandidateKind == SourceValueCandidateKind.Element
            && item.InformationType == "name");
        var setBName = logical.Single(item =>
            item.SourceSetId == fixture.SetB
            && item.CandidateKind == SourceValueCandidateKind.Element
            && item.InformationType == "name");

        Assert.IsGreaterThanOrEqualTo(2, setAName.DetailedIdentities.Count);
        Assert.AreEqual(
            setAName.DetailedIdentities.Count,
            setAName.DetailedIdentities.Select(identity => identity.StructuralPath).Distinct().Count());
        Assert.AreNotEqual(setAName.SourceSetId, setBName.SourceSetId);
        Assert.AreEqual(2, setBName.TotalOccurrenceCount);
        Assert.IsTrue(setAName.ContributingSources.Select(source => source.SourceId)
            .All(sourceId => fixture.SetASources.Any(source => source.SourceId == sourceId)));

        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(result.Information.Select(item => item.Identity));
        Assert.AreEqual(
            setAName.DetailedIdentities.Count,
            configuration.SetSelection(setAName.DetailedIdentities, isSelected: true));
        Assert.IsTrue(configuration.Current.Items
            .Where(item => setAName.DetailedIdentities.Contains(item.Identity))
            .All(item => item.Disposition == DiscoveryInformationDisposition.Selected));
        Assert.IsTrue(configuration.Current.Items
            .Where(item => item.Identity.SourceSetId == fixture.SetB)
            .All(item => item.Disposition == DiscoveryInformationDisposition.Neutral));

        var repeatedNote = result.Information.Single(item =>
            item.SourceSetId == fixture.SetA
            && item.Identity.CandidateKind == SourceValueCandidateKind.Element
            && item.InformationType == "note"
            && item.SampleValue == "same-content");
        Assert.AreEqual(3, repeatedNote.TotalOccurrenceCount);
        Assert.AreEqual(
            repeatedNote.TotalOccurrenceCount,
            repeatedNote.ContributingSources.Sum(source => source.OccurrenceCount));
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    [DataRow(RepeatedDataLayout.AlignRepeatedGroupsByPosition)]
    [DataRow(RepeatedDataLayout.StructuralRows)]
    [DataRow(RepeatedDataLayout.AllCombinations)]
    [DataRow(RepeatedDataLayout.NumberRepeatedValuesIntoColumns)]
    public async Task FourLayoutsPreserveHierarchyBoundariesConflictsAndPhysicalValues(
        RepeatedDataLayout layout)
    {
        using var workspace = new AlphaWorkspace();
        var fixture = workspace.LoadFixture();
        var specification = await workspace.CreateSpecificationAsync(fixture, layout);
        var expected = await workspace.ReadExpectedEvidenceAsync(fixture, specification);

        var result = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(), specification);

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.IsNotNull(result.PublishedGeneration);
        Assert.HasCount(3, result.PublishedGeneration.Datasets);
        var pages = await workspace.ReadAllDatabasePagesAsync(result.PublishedGeneration);
        var actualValues = pages.SelectMany(page => page.Rows)
            .SelectMany(row => row.Cells)
            .SelectMany(cell => cell.Values)
            .ToArray();

        AssertEvidencePreserved(expected, actualValues.Select(ValueEvidence.FromDatabaseValue));
        Assert.AreEqual(
            2,
            actualValues.Where(value => value.Value == "same-content")
                .Select(value => value.Lineage.NodeInstanceId).Distinct().Count());
        Assert.IsTrue(pages.SelectMany(page => page.Rows).All(row =>
            row.Cells.SelectMany(cell => cell.Values).All(value =>
                value.SourceId == row.Source.SourceId
                && value.DetailedIdentity.SourceSetId == row.Source.SourceSetId)));

        var conflict = pages.SelectMany(page => page.Rows)
            .SelectMany(row => row.Cells)
            .First(cell => cell.Values.Any(value => value.Value == "ATTR-A"));
        Assert.IsTrue(conflict.HasConflict);
        CollectionAssert.AreEquivalent(
            new[] { "ATTR-A", "ELEMENT-A" },
            conflict.Values.Select(value => value.Value).ToArray());

        if (layout == RepeatedDataLayout.NumberRepeatedValuesIntoColumns)
        {
            var elementA = actualValues.Single(value => value.Value == "ELEMENT-A");
            var quantityTwo = actualValues.Single(value => value.Value == "2");
            var elementB = actualValues.Single(value => value.Value == "ELEMENT-B");
            var quantityFive = actualValues.Single(value => value.Value == "5");
            Assert.AreEqual(elementA.RepeatCoordinates, quantityTwo.RepeatCoordinates);
            Assert.AreEqual(elementB.RepeatCoordinates, quantityFive.RepeatCoordinates);
            Assert.AreNotEqual(elementA.RepeatCoordinates, elementB.RepeatCoordinates);
            var serialCoordinates = actualValues
                .Where(value => value.Value is "S-A1" or "S-A2")
                .Select(value => value.RepeatCoordinates)
                .ToArray();
            Assert.HasCount(2, serialCoordinates);
            Assert.AreNotEqual(serialCoordinates[0], serialCoordinates[1]);
            Assert.IsTrue(serialCoordinates.All(coordinates => coordinates.Coordinates.Count >= 2));
        }
        else
        {
            var associated = pages.SelectMany(page => page.Rows)
                .Where(row => RowValues(row).Contains("ELEMENT-A"))
                .ToArray();
            Assert.IsNotEmpty(associated);
            Assert.IsTrue(associated.All(row => RowValues(row).Contains("2")));
            Assert.IsFalse(associated.Any(row => RowValues(row).Contains("5")));
        }
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    public async Task IncludedRowsFlowAtomicallyIntoRoutedWorkbooksWithoutChangingRowTruth()
    {
        using var workspace = new AlphaWorkspace();
        var fixture = workspace.LoadFixture();
        var specification = await workspace.CreateSpecificationAsync(
            fixture, RepeatedDataLayout.StructuralRows);
        var built = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(), specification);
        Assert.IsTrue(built.Accepted, built.Failure?.Description);
        var generation = built.PublishedGeneration!;
        var databasePages = await workspace.ReadAllDatabasePagesAsync(generation);
        var excluded = databasePages.Single(page => page.Dataset.SourceSetId == fixture.SetA).Rows[0];
        Assert.AreEqual(
            1,
            await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
                new DatabaseRowInclusionChange(
                    generation.OperationId, fixture.SetA, [excluded.Ordinal], false)));

        var extracted = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(), generation);

        Assert.IsTrue(extracted.Accepted, extracted.Failure?.Description);
        Assert.IsNotNull(extracted.PublishedResult);
        Assert.AreEqual(generation.RowCount - 1, extracted.PublishedResult.RowCount);
        var extractionRows = await workspace.ReadAllExtractionRowsAsync(extracted.PublishedResult);
        Assert.IsFalse(extractionRows.Any(row =>
            row.Source.SourceSetId == fixture.SetA
            && row.DatabaseRowOrdinal == excluded.Ordinal));
        Assert.IsTrue(extractionRows.All(row => row.Cells.SelectMany(cell => cell.Values).All(value =>
            value.SourceId == row.Source.SourceId
            && value.DetailedIdentity.SourceSetId == row.Source.SourceSetId
            && value.Lineage.TraversalOrder > 0)));
        Assert.IsTrue(extractionRows.SelectMany(row => row.Cells).Any(cell => cell.HasConflict));

        var configuration = CreateRouting(extracted.PublishedResult);
        var exportDirectory = Path.Combine(workspace.Root, "export");
        Directory.CreateDirectory(exportDirectory);
        var exported = await workspace.Export.ExportAsync(
            OperationCorrelation.CreateNew(),
            extracted.PublishedResult,
            configuration,
            CreatePublicationPlan(configuration, extracted.PublishedResult, exportDirectory));

        Assert.IsTrue(exported.Accepted, exported.Failure?.Description);
        Assert.IsNotNull(exported.Batch);
        Assert.HasCount(2, exported.Batch.Workbooks);
        Assert.IsFalse(File.Exists(Path.Combine(exportDirectory, "Empty.xlsx")));
        var combinedPath = Path.Combine(exportDirectory, "Combined.xlsx");
        var separatePath = Path.Combine(exportDirectory, "Separate.xlsx");
        Assert.IsTrue(File.Exists(combinedPath));
        Assert.IsTrue(File.Exists(separatePath));
        using var combined = SpreadsheetDocument.Open(combinedPath, isEditable: false);
        CollectionAssert.AreEqual(
            new[] { "Set B", "Set A" },
            SheetNames(combined).ToArray());
        using var separate = SpreadsheetDocument.Open(separatePath, isEditable: false);
        CollectionAssert.AreEqual(new[] { "Set C" }, SheetNames(separate).ToArray());

        var setBCells = ReadCells(combined, "Set B");
        Assert.IsTrue(setBCells.Values.Contains("=SUM(A1:A2)"));
        var formulaCells = Cells(combined, "Set B").Where(cell =>
            cell.InlineString?.Text?.Text == "=SUM(A1:A2)").ToArray();
        Assert.IsNotEmpty(formulaCells);
        Assert.IsTrue(formulaCells.All(cell => cell.CellFormula is null
            && cell.DataType?.Value == CellValues.InlineString));
        Assert.IsTrue(setBCells.Values.Contains("  preserved whitespace  "));

        foreach (var dataset in extracted.PublishedResult.Datasets)
        {
            var workbook = dataset.SourceSetId == fixture.SetC ? separate : combined;
            var sheetName = dataset.SourceSetId switch
            {
                var id when id == fixture.SetA => "Set A",
                var id when id == fixture.SetB => "Set B",
                _ => "Set C"
            };
            Assert.AreEqual(dataset.RowCount, DataRowCount(workbook, sheetName));
        }
        Assert.IsEmpty(Directory.GetFiles(exportDirectory, "*.incomplete"));
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    public void EvidencePreservationAssertionRejectsMissingPhysicalOccurrence()
    {
        var evidence = new ValueEvidence(
            SourceId.CreateNew(),
            SourceSetId.CreateNew(),
            SourceValueCandidateKind.Element,
            "/records/record/value",
            "same-content",
            NodeInstanceId: 17,
            TraversalOrder: 23);
        var expected = new[] { evidence, evidence };
        var actualWithOneOccurrenceMissing = new[] { evidence };

        Assert.ThrowsExactly<AssertFailedException>(() =>
            AssertEvidencePreserved(expected, actualWithOneOccurrenceMissing));
    }

    private static IReadOnlyList<string> RowValues(DatabaseReviewRow row) =>
        row.Cells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray();

    private static void AssertEvidencePreserved(
        IEnumerable<ValueEvidence> expected,
        IEnumerable<ValueEvidence> actual)
    {
        var expectedCounts = expected.GroupBy(value => value)
            .ToDictionary(group => group.Key, group => group.Count());
        var actualCounts = actual.GroupBy(value => value)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var item in expectedCounts)
        {
            Assert.IsTrue(actualCounts.TryGetValue(item.Key, out var actualCount), item.Key.ToString());
            if (actualCount < item.Value)
            {
                Assert.Fail(
                    $"Physical evidence was lost. Expected at least {item.Value}, actual {actualCount}: {item.Key}");
            }
        }
    }

    private static ExportConfigurationSnapshot CreateRouting(ExtractionResultSummary extraction)
    {
        var datasets = extraction.Datasets.OrderBy(dataset => dataset.Ordinal).ToArray();
        var combinedId = WorkbookDefinitionId.CreateNew();
        var separateId = WorkbookDefinitionId.CreateNew();
        var emptyId = WorkbookDefinitionId.CreateNew();
        var sheets = datasets.Select(_ => WorksheetDefinitionId.CreateNew()).ToArray();
        return new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                 combinedId,
                 "Combined.xlsx",
                 1,
                 [new WorksheetDefinition(sheets[1], combinedId, datasets[1].SourceSetId, "Set B", 1),
                  new WorksheetDefinition(sheets[0], combinedId, datasets[0].SourceSetId, "Set A", 2)]),
             new WorkbookDefinition(
                 separateId,
                 "Separate.xlsx",
                 2,
                 [new WorksheetDefinition(sheets[2], separateId, datasets[2].SourceSetId, "Set C", 1)]),
             new WorkbookDefinition(emptyId, "Empty.xlsx", 3, [])],
            datasets.Select((dataset, index) => new SourceSetExportConfiguration(
                dataset.SourceSetId,
                true,
                sheets[index],
                dataset.Columns.Select((column, columnIndex) => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    columnIndex == 0)).ToArray(),
                [new ExportMetadataFieldConfiguration(
                    DatabaseMetadataField.SourceFile,
                    true,
                    "Source File")])).ToArray());
    }

    private static WorkbookPublicationPlan CreatePublicationPlan(
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extraction,
        string directory)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extraction);
        Assert.IsTrue(validation.IsValid, string.Join("; ", validation.Failures.Select(f => f.Description)));
        return new WorkbookPublicationPlan(
            directory,
            validation.RunnableWorkbooks.Select(workbook => new WorkbookPublicationTarget(
                workbook.Workbook.WorkbookDefinitionId,
                Path.Combine(directory, workbook.Workbook.FileName),
                WorkbookPublicationDisposition.CreateNew)).ToArray());
    }

    private static IReadOnlyList<string> SheetNames(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart
            ?? throw new AssertFailedException("The workbook requires a workbook part.");
        var workbook = workbookPart.Workbook
            ?? throw new AssertFailedException("The workbook part requires a workbook.");
        var sheets = workbook.Sheets
            ?? throw new AssertFailedException("The workbook requires sheets.");
        return sheets.Elements<Sheet>()
            .Select(sheet => sheet.Name?.Value
                ?? throw new AssertFailedException("A worksheet requires a name."))
            .ToArray();
    }

    private static IReadOnlyList<Cell> Cells(SpreadsheetDocument document, string sheetName) =>
        GetWorksheet(document, sheetName).Descendants<Cell>().ToArray();

    private static IReadOnlyDictionary<string, string> ReadCells(
        SpreadsheetDocument document,
        string sheetName) =>
        Cells(document, sheetName)
            .Where(cell => cell.InlineString?.Text is not null)
            .ToDictionary(
                cell => cell.CellReference?.Value
                    ?? throw new AssertFailedException("A cell requires a reference."),
                cell => cell.InlineString?.Text?.Text
                    ?? throw new AssertFailedException("A tested cell requires inline text."));

    private static int DataRowCount(SpreadsheetDocument document, string sheetName) =>
        GetWorksheet(document, sheetName).Descendants<Row>()
            .Count(row => row.RowIndex?.Value > 1);

    private static Worksheet GetWorksheet(SpreadsheetDocument document, string sheetName)
    {
        var workbookPart = document.WorkbookPart
            ?? throw new AssertFailedException("The workbook requires a workbook part.");
        var workbook = workbookPart.Workbook
            ?? throw new AssertFailedException("The workbook part requires a workbook.");
        var sheets = workbook.Sheets
            ?? throw new AssertFailedException("The workbook requires sheets.");
        var sheet = sheets.Elements<Sheet>().Single(candidate =>
            candidate.Name?.Value == sheetName);
        var relationshipId = sheet.Id?.Value
            ?? throw new AssertFailedException("A worksheet requires a relationship ID.");
        return ((WorksheetPart)workbookPart.GetPartById(relationshipId)).Worksheet
            ?? throw new AssertFailedException("The worksheet requires content.");
    }

    private sealed record ValueEvidence(
        SourceId SourceId,
        SourceSetId SourceSetId,
        SourceValueCandidateKind CandidateKind,
        string StructuralIdentity,
        string Value,
        long NodeInstanceId,
        long TraversalOrder)
    {
        internal static ValueEvidence FromInterpreted(
            SourceSetId sourceSetId,
            SourceId sourceId,
            InterpretedSourceValue value) =>
            new(
                sourceId,
                sourceSetId,
                value.CandidateKind,
                value.StructuralIdentity,
                value.Content,
                value.Lineage!.NodeInstanceId,
                value.Lineage.TraversalOrder);

        internal static ValueEvidence FromDatabaseValue(DatabaseReviewValue value) =>
            new(
                value.SourceId,
                value.DetailedIdentity.SourceSetId,
                value.DetailedIdentity.CandidateKind,
                value.DetailedIdentity.StructuralIdentity,
                value.Value,
                value.Lineage.NodeInstanceId,
                value.Lineage.TraversalOrder);
    }

    private sealed class AlphaWorkspace : IDisposable
    {
        private readonly SourceInterpreter _interpreter;

        public AlphaWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "CIA.SPR142.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData")));
            var history = new NullHistory();
            var cancellation = new CooperativeOperationCancellation(history);
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            Discovery = new DiscoveryService(
                _interpreter,
                new SourceOccurrenceReader([], NullLogger<SourceOccurrenceReader>.Instance));
            Database = new DatabaseGenerationService(
                Repository,
                _interpreter,
                new HierarchyFlatteningEngine(),
                cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
            Extraction = new DatabaseExtractionService(
                Repository,
                cancellation,
                NullLogger<DatabaseExtractionService>.Instance);
            Export = new ExcelWorkbookExportService(
                Repository,
                cancellation,
                NullLogger<ExcelWorkbookExportService>.Instance);
        }

        public string Root { get; }
        public StructuredInformationRepository Repository { get; }
        public DiscoveryService Discovery { get; }
        public DatabaseGenerationService Database { get; }
        public DatabaseExtractionService Extraction { get; }
        public ExcelWorkbookExportService Export { get; }

        public AlphaFixture LoadFixture()
        {
            var setA = SourceSetId.CreateNew();
            var setB = SourceSetId.CreateNew();
            var setC = SourceSetId.CreateNew();
            var setASources = new[]
            {
                CopyFixture(setA, "set-a-orders-1.xml"),
                CopyFixture(setA, "set-a-orders-2.xml")
            };
            var setBSource = CopyFixture(setB, "set-b-orders.xml");
            var setCSource = CopyFixture(setC, "set-c-orders.xml");
            var malformed = CopyFixture(setC, "malformed-independent.xml");
            return new AlphaFixture(
                setA,
                setB,
                setC,
                setASources,
                setBSource,
                setCSource,
                malformed);
        }

        public async Task<DatabaseBuildSpecification> CreateSpecificationAsync(
            AlphaFixture fixture,
            RepeatedDataLayout layout)
        {
            var datasets = new List<DatabaseDatasetBuildSpecification>();
            foreach (var set in fixture.Sets)
            {
                var identities = new List<DiscoveryInformationIdentity>();
                foreach (var source in set.Sources)
                {
                    var interpreted = await InterpretAsync(source);
                    identities.AddRange(interpreted.Values.Where(value => value.Lineage is not null)
                        .Select(value => HierarchySourceOccurrence.FromInterpretedValue(
                            set.SourceSetId, value).Identity));
                }

                var distinct = identities.Distinct().ToArray();
                var overrides = distinct
                    .Where(identity => identity.InformationType == "code"
                        && identity.CandidateKind is SourceValueCandidateKind.Element
                            or SourceValueCandidateKind.Attribute)
                    .ToDictionary(identity => identity, _ => "Unified Code");
                datasets.Add(new DatabaseDatasetBuildSpecification(
                    set.SourceSetId,
                    set.Name,
                    datasets.Count + 1,
                    layout,
                    set.Sources,
                    DatabaseTagMapper.CreateFieldMappings(
                        set.SourceSetId,
                        distinct.Select(identity => new DiscoveryConfigurationItem(
                            identity, DiscoveryInformationDisposition.Selected)),
                        overrides)));
            }

            return new DatabaseBuildSpecification(datasets);
        }

        public async Task<IReadOnlyList<ValueEvidence>> ReadExpectedEvidenceAsync(
            AlphaFixture fixture,
            DatabaseBuildSpecification specification)
        {
            var expected = new List<ValueEvidence>();
            foreach (var dataset in specification.Datasets)
            {
                var selected = dataset.Fields.SelectMany(field => field.DetailedIdentities).ToHashSet();
                foreach (var source in fixture.Sets.Single(set =>
                             set.SourceSetId == dataset.SourceSetId).Sources)
                {
                    var interpreted = await InterpretAsync(source);
                    expected.AddRange(interpreted.Values.Where(value => value.Lineage is not null)
                        .Where(value => selected.Contains(
                            HierarchySourceOccurrence.FromInterpretedValue(
                                dataset.SourceSetId, value).Identity))
                        .Select(value => ValueEvidence.FromInterpreted(
                            dataset.SourceSetId, source.SourceId, value)));
                }
            }

            return expected;
        }

        public async Task<IReadOnlyList<DatabaseReviewPage>> ReadAllDatabasePagesAsync(
            DatabaseGenerationSummary generation)
        {
            var pages = new List<DatabaseReviewPage>();
            foreach (var dataset in generation.Datasets)
            {
                var page = await Repository.ReadPublishedDatabasePageAsync(
                    new DatabaseReviewQuery(
                        generation.OperationId,
                        dataset.SourceSetId,
                        1,
                        DatabaseReviewLimits.MaximumRowsPerPage,
                        null,
                        DatabaseRowInclusionFilter.All));
                Assert.IsNotNull(page);
                Assert.HasCount(page.TotalRowCount, page.Rows);
                pages.Add(page);
            }

            return pages;
        }

        public async Task<IReadOnlyList<ExtractionResultRow>> ReadAllExtractionRowsAsync(
            ExtractionResultSummary extraction)
        {
            var rows = new List<ExtractionResultRow>();
            foreach (var dataset in extraction.Datasets)
            {
                await foreach (var row in Repository.StreamPublishedExtractionRowsAsync(
                                   extraction.OperationId, dataset.SourceSetId))
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        private LoadedSourceContract CopyFixture(SourceSetId set, string fileName)
        {
            var source = Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "AlphaRegression", fileName);
            var target = Path.Combine(Root, set.ToString(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                set,
                target,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        private async Task<InterpretedSourceDocument> InterpretAsync(LoadedSourceContract source)
        {
            var result = await _interpreter.InterpretAsync(source);
            Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
            return result.Source!;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record AlphaFixture(
        SourceSetId SetA,
        SourceSetId SetB,
        SourceSetId SetC,
        IReadOnlyList<LoadedSourceContract> SetASources,
        LoadedSourceContract SetBSource,
        LoadedSourceContract SetCSource,
        LoadedSourceContract MalformedSource)
    {
        internal IReadOnlyList<LoadedSourceContract> AllSources =>
            [.. SetASources, SetBSource, SetCSource];

        internal IReadOnlyList<FixtureSet> Sets =>
            [new(SetA, "Set A", SetASources),
             new(SetB, "Set B", [SetBSource]),
             new(SetC, "Set C", [SetCSource])];

        internal string NameOf(SourceSetId sourceSetId) =>
            Sets.Single(set => set.SourceSetId == sourceSetId).Name;
    }

    private sealed record FixtureSet(
        SourceSetId SourceSetId,
        string Name,
        IReadOnlyList<LoadedSourceContract> Sources);

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
