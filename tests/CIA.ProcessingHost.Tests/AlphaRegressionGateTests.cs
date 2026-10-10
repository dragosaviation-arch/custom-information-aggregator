using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.Desktop.Export;
using CIA.Desktop.Presentation;
using CIA.Desktop.Discovery;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Export;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceIntake;
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
        var correlation = OperationCorrelation.CreateNew();
        var result = await workspace.Discovery.RunAsync(
            correlation,
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
        var setAContributors = await workspace.Discovery.GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                correlation.OperationId,
                setAName.DetailedIdentities,
                StartIndex: 0,
                PageSize: DiscoveryContributorPaging.DefaultPageSize,
                ExpectedSourceCount: setAName.SourceCount));
        Assert.IsTrue(setAContributors.Accepted);
        Assert.IsTrue(setAContributors.Page!.Sources.Select(source => source.SourceId)
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
        Assert.AreEqual(2, repeatedNote.LogicalSourceCount);
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    [DataRow(RepeatedDataLayout.AlignRepeatedGroupsByPosition)]
    [DataRow(RepeatedDataLayout.StructuralRows)]
    [DataRow(RepeatedDataLayout.AllCombinations)]
    [DataRow(RepeatedDataLayout.NumberRepeatedValuesIntoColumns)]
    public async Task GoodbooksLikeSourceFlowsThroughIndependentFamilyWorksheetsInEveryLayout(
        RepeatedDataLayout layout)
    {
        using var workspace = new AlphaWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = await workspace.LoadStructuralFixtureAsync(
            sourceSetId,
            "spr201-end-to-end.xml");
        var discovery = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [source]);

        Assert.IsTrue(discovery.Accepted, discovery.Failure?.Description);
        Assert.IsNotNull(discovery.FamilyCatalog);
        CollectionAssert.AreEqual(
            new[]
            {
                "/book",
                "/book/authors/author",
                "/book/buy_links/link",
                "/book/series_works/work",
                "/book/similar_books/book"
            },
            discovery.FamilyCatalog.Families.Select(family => family.Id.AnchorPath).ToArray());
        Assert.IsTrue(discovery.Information.All(item => item.RowFamilyId is not null));

        var specification = CreateDiscoveredSpecification(
            discovery,
            [source],
            new Dictionary<SourceSetId, string> { [sourceSetId] = "Goodbooks" },
            layout);
        var first = await RunGoodbooksFlowAsync(workspace, specification, "first");
        var second = await RunGoodbooksFlowAsync(workspace, specification, "second");

        var expectedRows = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["/book"] = 1,
            ["/book/authors/author"] = 2,
            ["/book/buy_links/link"] = 2,
            ["/book/series_works/work"] = 1,
            ["/book/similar_books/book"] = 2
        };
        CollectionAssert.AreEqual(
            expectedRows.Keys.ToArray(),
            first.Generation.Datasets.Single().RowFamilies
                .Select(family => family.AnchorPath).ToArray());
        foreach (var family in first.Generation.Datasets.Single().RowFamilies)
        {
            Assert.AreEqual(expectedRows[family.AnchorPath], family.RowCount, family.AnchorPath);
            Assert.AreEqual(
                expectedRows[family.AnchorPath],
                first.Extraction.Datasets.Single().RowFamilies.Single(candidate =>
                    candidate.Id == family.Id).RowCount,
                family.AnchorPath);
        }
        Assert.AreEqual(8, first.Generation.RowCount);
        Assert.AreEqual(8, first.Extraction.RowCount);

        AssertFamilyOwnsPaths(
            first.Generation,
            "/book",
            "/book/num_pages",
            "/book/title",
            "/book/work/original_title");
        AssertFamilyOwnsPaths(
            first.Generation,
            "/book/authors/author",
            "/book/authors/author/name");
        AssertFamilyOwnsPaths(
            first.Generation,
            "/book/buy_links/link",
            "/book/buy_links/link/name");
        AssertFamilyOwnsPaths(
            first.Generation,
            "/book/series_works/work",
            "/book/series_works/work/title");
        AssertFamilyOwnsPaths(
            first.Generation,
            "/book/similar_books/book",
            "/book/similar_books/book/author/name",
            "/book/similar_books/book/num_pages",
            "/book/similar_books/book/title",
            "/book/similar_books/book/title_without_series");

        Assert.IsTrue(first.DatabaseRows["/book"].Single().Contains("Root Book"));
        Assert.IsTrue(first.DatabaseRows["/book"].Single().Contains("Original Root"));
        CollectionAssert.AreEquivalent(
            new[] { "Author A", "Author B" },
            first.DatabaseRows["/book/authors/author"].SelectMany(row => row)
                .Where(value => value.StartsWith("Author ", StringComparison.Ordinal)).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "Shop A", "Shop B" },
            first.DatabaseRows["/book/buy_links/link"].SelectMany(row => row)
                .Where(value => value.StartsWith("Shop ", StringComparison.Ordinal)).ToArray());
        Assert.IsTrue(first.DatabaseRows["/book/similar_books/book"].Any(row =>
            row.Contains("Similar A") && row.Contains("200") && row.Contains("Similar Author A")));
        Assert.IsTrue(first.DatabaseRows["/book/similar_books/book"].Any(row =>
            row.Contains("Similar B") && row.Contains("300") && row.Contains("Similar Author B")));
        Assert.IsFalse(first.DatabaseRows["/book/similar_books/book"].Any(row =>
            row.Contains("Shop A") || row.Contains("Author A")));

        CollectionAssert.AreEqual(
            new[]
            {
                "Goodbooks - Root",
                "Goodbooks - author",
                "Goodbooks - link",
                "Goodbooks - work",
                "Goodbooks - book"
            },
            first.WorksheetNames.ToArray());
        Assert.HasCount(5, first.WorksheetNames);
        AssertWorksheetContains(first, "Goodbooks - author", "Author A");
        AssertWorksheetDoesNotContain(first, "Goodbooks - author", "Shop A");
        AssertWorksheetContains(first, "Goodbooks - book", "Similar B");
        AssertWorksheetDoesNotContain(first, "Goodbooks - book", "Shop A");

        CollectionAssert.AreEqual(
            first.FamilySignatures.ToArray(),
            second.FamilySignatures.ToArray());
        CollectionAssert.AreEqual(
            first.ExtractionSignatures.ToArray(),
            second.ExtractionSignatures.ToArray());
        CollectionAssert.AreEqual(
            first.WorksheetNames.ToArray(),
            second.WorksheetNames.ToArray());
        foreach (var sheetName in first.WorksheetNames)
        {
            CollectionAssert.AreEqual(
                first.WorksheetCells[sheetName].ToArray(),
                second.WorksheetCells[sheetName].ToArray(),
                sheetName);
        }
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    public async Task ProvenNestedAuthorFamilyFlowsSeparatelyThroughExtractionAndExcel()
    {
        using var workspace = new AlphaWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = await workspace.LoadStructuralFixtureAsync(
            sourceSetId,
            "family-aware-nested-authors.xml");
        var discovery = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [source]);
        var specification = CreateDiscoveredSpecification(
            discovery,
            [source],
            new Dictionary<SourceSetId, string> { [sourceSetId] = "Nested" },
            RepeatedDataLayout.StructuralRows);

        var flow = await RunGoodbooksFlowAsync(workspace, specification, "nested");

        var families = flow.Generation.Datasets.Single().RowFamilies.ToDictionary(
            family => family.AnchorPath,
            StringComparer.Ordinal);
        Assert.AreEqual(1, families["/book"].RowCount);
        Assert.AreEqual(2, families["/book/similar_books/book"].RowCount);
        Assert.AreEqual(3, families["/book/similar_books/book/authors/author"].RowCount);
        Assert.AreEqual(
            families["/book/similar_books/book"].Id,
            families["/book/similar_books/book/authors/author"].ParentRowFamilyId);
        Assert.IsFalse(flow.DatabaseRows["/book/similar_books/book"].Any(row =>
            row.Any(value => value.StartsWith("Author ", StringComparison.Ordinal))));
        CollectionAssert.AreEquivalent(
            new[] { "Author A1", "Author A2", "Author B1" },
            flow.DatabaseRows["/book/similar_books/book/authors/author"]
                .SelectMany(row => row)
                .Where(value => value.StartsWith("Author ", StringComparison.Ordinal)).ToArray());
        Assert.IsTrue(flow.WorksheetNames.Any(name =>
            name.EndsWith(" - author", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    public async Task MultipleSetsAndFamiliesRouteAcrossWorkbooksWithoutCrossSetValues()
    {
        using var workspace = new AlphaWorkspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var firstSource = await workspace.LoadStructuralFixtureAsync(
            firstSet,
            "spr201-end-to-end.xml");
        var secondSource = await workspace.LoadStructuralFixtureAsync(
            secondSet,
            "spr201-end-to-end.xml");
        var discovery = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [firstSource, secondSource]);
        var reversedDiscovery = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [secondSource, firstSource]);
        CollectionAssert.AreEqual(
            discovery.FamilyCatalog!.Families.Select(family =>
                $"{family.Id}|{family.ParentRowFamilyId}").ToArray(),
            reversedDiscovery.FamilyCatalog!.Families.Select(family =>
                $"{family.Id}|{family.ParentRowFamilyId}").ToArray());
        CollectionAssert.AreEqual(
            discovery.Information.Select(item => $"{item.Identity}|{item.RowFamilyId}")
                .Order(StringComparer.Ordinal).ToArray(),
            reversedDiscovery.Information.Select(item =>
                $"{item.Identity}|{item.RowFamilyId}").Order(StringComparer.Ordinal).ToArray());
        var specification = CreateDiscoveredSpecification(
            discovery,
            [firstSource, secondSource],
            new Dictionary<SourceSetId, string>
            {
                [firstSet] = "Catalog",
                [secondSet] = "Catalog"
            },
            RepeatedDataLayout.StructuralRows);
        var built = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            specification);
        Assert.IsTrue(built.Accepted, built.Failure?.Description);
        var extracted = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(),
            built.PublishedGeneration!);
        Assert.IsTrue(extracted.Accepted, extracted.Failure?.Description);

        var routing = new ExportRoutingConfigurationPresentation();
        routing.Synchronize(built.PublishedGeneration!);
        Assert.HasCount(10, routing.SourceSets);
        Assert.HasCount(1, routing.Workbooks);
        Assert.HasCount(
            10,
            routing.SourceSets.Select(set => set.WorksheetName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(routing.SourceSets.Any(set =>
            set.WorksheetName.EndsWith(" (2)", StringComparison.Ordinal)));
        foreach (var family in routing.SourceSets)
        {
            family.Fields[0].IsSourceIdExported = true;
        }

        var separated = routing.SourceSets.Last(set => set.SourceSetId == secondSet);
        var siblingRoutesBefore = routing.SourceSets
            .Where(set => set.RowFamilyId != separated.RowFamilyId)
            .ToDictionary(set => set.RowFamilyId, set => set.WorkbookDefinitionId);
        var separateWorkbook = routing.CreateWorkbookFor(separated.RowFamilyId);
        routing.Workbooks.Single(workbook =>
            workbook.WorkbookDefinitionId != separateWorkbook.WorkbookDefinitionId).FileName =
            "Shared.xlsx";
        separateWorkbook.FileName = "Separate.xlsx";
        Assert.IsTrue(routing.SourceSets
            .Where(set => set.RowFamilyId != separated.RowFamilyId)
            .All(set => set.WorkbookDefinitionId == siblingRoutesBefore[set.RowFamilyId]));

        var configuration = routing.Capture();
        var exportDirectory = Path.Combine(workspace.Root, "multi-set-export");
        Directory.CreateDirectory(exportDirectory);
        var exported = await workspace.Export.ExportAsync(
            OperationCorrelation.CreateNew(),
            extracted.PublishedResult!,
            configuration,
            CreatePublicationPlan(configuration, extracted.PublishedResult!, exportDirectory));

        Assert.IsTrue(exported.Accepted, exported.Failure?.Description);
        Assert.HasCount(2, exported.Batch!.Workbooks);
        foreach (var route in configuration.SourceSets)
        {
            var worksheet = configuration.Workbooks.SelectMany(workbook => workbook.Worksheets)
                .Single(sheet => sheet.RowFamilyId == route.RowFamilyId);
            var workbook = configuration.Workbooks.Single(candidate =>
                candidate.WorkbookDefinitionId == worksheet.WorkbookDefinitionId);
            using var document = SpreadsheetDocument.Open(
                Path.Combine(exportDirectory, workbook.FileName),
                isEditable: false);
            var values = ReadCells(document, worksheet.Name).Values.ToArray();
            var expectedSource = route.SourceSetId == firstSet ? firstSource : secondSource;
            var foreignSource = route.SourceSetId == firstSet ? secondSource : firstSource;
            Assert.IsTrue(values.Contains(expectedSource.SourceId.ToString()), worksheet.Name);
            Assert.IsFalse(values.Contains(foreignSource.SourceId.ToString()), worksheet.Name);
        }
        Assert.IsEmpty(Directory.GetFiles(exportDirectory, "*.incomplete"));
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

        var extracted = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(),
            result.PublishedGeneration);
        Assert.IsTrue(extracted.Accepted, extracted.Failure?.Description);
        var publishedExtraction = extracted.PublishedResult!;
        var configuration = CreateRouting(publishedExtraction);
        var exportDirectory = Path.Combine(workspace.Root, $"layout-{layout}");
        Directory.CreateDirectory(exportDirectory);
        var exported = await workspace.Export.ExportAsync(
            OperationCorrelation.CreateNew(),
            publishedExtraction,
            configuration,
            CreatePublicationPlan(configuration, publishedExtraction, exportDirectory));
        Assert.IsTrue(exported.Accepted, exported.Failure?.Description);
        Assert.AreEqual(
            publishedExtraction.Datasets.Sum(dataset => dataset.RowFamilies.Count),
            exported.Batch!.Workbooks.Sum(workbook => workbook.Worksheets.Count));
        Assert.IsTrue(exported.Batch.Workbooks.SelectMany(workbook => workbook.Worksheets)
            .All(sheet => publishedExtraction.Datasets
                .SelectMany(dataset => dataset.RowFamilies)
                .Single(family => family.Id == sheet.RowFamilyId).RowCount == sheet.RowCount));
        Assert.IsEmpty(Directory.GetFiles(exportDirectory, "*.incomplete"));
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
        var combinedDefinition = configuration.Workbooks.Single(workbook =>
            workbook.FileName == "Combined.xlsx");
        CollectionAssert.AreEqual(
            combinedDefinition.Worksheets.OrderBy(sheet => sheet.Order)
                .Select(sheet => sheet.Name).ToArray(),
            SheetNames(combined).ToArray());
        using var separate = SpreadsheetDocument.Open(separatePath, isEditable: false);
        var separateDefinition = configuration.Workbooks.Single(workbook =>
            workbook.FileName == "Separate.xlsx");
        CollectionAssert.AreEqual(
            separateDefinition.Worksheets.OrderBy(sheet => sheet.Order)
                .Select(sheet => sheet.Name).ToArray(),
            SheetNames(separate).ToArray());

        var setBSheet = combinedDefinition.Worksheets.Single(sheet =>
            sheet.SourceSetId == fixture.SetB);
        var setBCells = ReadCells(combined, setBSheet.Name);
        Assert.IsTrue(setBCells.Values.Contains("=SUM(A1:A2)"));
        var formulaCells = Cells(combined, setBSheet.Name).Where(cell =>
            cell.InlineString?.Text?.Text == "=SUM(A1:A2)").ToArray();
        Assert.IsNotEmpty(formulaCells);
        Assert.IsTrue(formulaCells.All(cell => cell.CellFormula is null
            && cell.DataType?.Value == CellValues.InlineString));
        Assert.IsTrue(setBCells.Values.Contains("  preserved whitespace  "));

        foreach (var family in extracted.PublishedResult.Datasets.SelectMany(dataset =>
                     dataset.RowFamilies))
        {
            var definition = configuration.Workbooks.Single(workbook =>
                workbook.Worksheets.Any(sheet => sheet.RowFamilyId == family.Id));
            var workbook = definition.FileName == "Separate.xlsx" ? separate : combined;
            var sheetName = definition.Worksheets.Single(sheet =>
                sheet.RowFamilyId == family.Id).Name;
            Assert.AreEqual(family.RowCount, DataRowCount(workbook, sheetName));
        }
        Assert.IsEmpty(Directory.GetFiles(exportDirectory, "*.incomplete"));
    }

    [TestMethod]
    [TestCategory(GateCategory)]
    public async Task SameLeafDifferentAncestryRemainsFilterableThroughExtractionAndExcel()
    {
        using var workspace = new AlphaWorkspace();
        var (source, specification) = await workspace.CreateSameLeafSpecificationAsync(
            RepeatedDataLayout.StructuralRows);
        var discovered = await workspace.Discovery.RunAsync(
            OperationCorrelation.CreateNew(),
            [source]);
        Assert.IsTrue(discovered.Accepted, discovered.Failure?.Description);
        var logicalPartNumber = DiscoveredInformationItemViewModel.CreateLogicalItems(
                discovered.Information,
                _ => "Tools",
                _ => DiscoveryInformationDisposition.Selected,
                identity => identity.InformationType == "pnr" ? "Part Number" : null)
            .Single(item => item.InformationType == "pnr");
        Assert.HasCount(2, logicalPartNumber.DetailedIdentities);
        Assert.AreEqual("Part Number", logicalPartNumber.DatabaseTag);
        var built = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            specification);
        Assert.IsTrue(built.Accepted, built.Failure?.Description);
        var databasePage = (await workspace.ReadAllDatabasePagesAsync(
            built.PublishedGeneration!)).Single();
        Assert.HasCount(2, databasePage.Rows);

        var extracted = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(),
            built.PublishedGeneration!);
        Assert.IsTrue(extracted.Accepted, extracted.Failure?.Description);
        var extractionRows = await workspace.ReadAllExtractionRowsAsync(extracted.PublishedResult!);
        Assert.HasCount(2, extractionRows);
        Assert.IsTrue(extractionRows.All(row =>
            row.Source.SourceId == source.SourceId
            && RowValues(row).Contains("KIT")));
        Assert.IsFalse(extractionRows.Any(row =>
            RowValues(row).Contains("MAIN-001") && RowValues(row).Contains("ALT-001")));

        var dataset = extracted.PublishedResult!.Datasets.Single();
        var family = dataset.RowFamilies.Single();
        var workbookId = WorkbookDefinitionId.CreateNew();
        var worksheetId = WorksheetDefinitionId.CreateNew();
        var configuration = new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                workbookId,
                "Structural origins.xlsx",
                1,
                [new WorksheetDefinition(
                    worksheetId,
                    workbookId,
                    dataset.SourceSetId,
                    family.Id,
                    "Results",
                    1)])],
            [new SourceSetExportConfiguration(
                dataset.SourceSetId,
                family.Id,
                true,
                worksheetId,
                family.Columns.Select(column => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    false)).ToArray(),
                [new ExportMetadataFieldConfiguration(
                     DatabaseMetadataField.ValuePath,
                     true,
                     "Value / XML Path"),
                 new ExportMetadataFieldConfiguration(
                     DatabaseMetadataField.StructuralIdentity,
                     true,
                     "Structural Identity"),
                 new ExportMetadataFieldConfiguration(
                     DatabaseMetadataField.RecordHierarchy,
                     true,
                     "Record Hierarchy")])]);
        var exportDirectory = Path.Combine(workspace.Root, "same-leaf-export");
        Directory.CreateDirectory(exportDirectory);
        var exported = await workspace.Export.ExportAsync(
            OperationCorrelation.CreateNew(),
            extracted.PublishedResult,
            configuration,
            CreatePublicationPlan(configuration, extracted.PublishedResult, exportDirectory));

        Assert.IsTrue(exported.Accepted, exported.Failure?.Description);
        var workbookPath = Path.Combine(exportDirectory, "Structural origins.xlsx");
        using var workbook = SpreadsheetDocument.Open(workbookPath, isEditable: false);
        var cells = ReadCells(workbook, "Results");
        var columns = cells.Where(cell => cell.Key.EndsWith("1", StringComparison.Ordinal))
            .ToDictionary(cell => cell.Value, cell => cell.Key[..^1], StringComparer.Ordinal);
        var partNumberColumn = columns["Part Number"];
        var valuePathColumn = columns["Value / XML Path"];
        var structuralIdentityColumn = columns["Structural Identity"];
        var recordHierarchyColumn = columns["Record Hierarchy"];
        var rows = new[] { 2, 3 };
        var mainRow = rows.Single(row => cells[$"{partNumberColumn}{row}"] == "MAIN-001");
        var alternateRow = rows.Single(row => cells[$"{partNumberColumn}{row}"] == "ALT-001");
        Assert.DoesNotContain("|", cells[$"{partNumberColumn}{mainRow}"]);
        Assert.DoesNotContain("|", cells[$"{partNumberColumn}{alternateRow}"]);
        StringAssert.Contains(cells[$"{valuePathColumn}{mainRow}"], "/toolnbr/pnr");
        Assert.IsFalse(cells[$"{valuePathColumn}{mainRow}"].Contains(
            "/pnrdata/rplby/pnr", StringComparison.Ordinal));
        StringAssert.Contains(cells[$"{valuePathColumn}{alternateRow}"], "/pnrdata/rplby/pnr");
        StringAssert.Contains(cells[$"{structuralIdentityColumn}{mainRow}"], "/toolnbr/pnr");
        StringAssert.Contains(
            cells[$"{structuralIdentityColumn}{alternateRow}"],
            "/pnrdata/rplby/pnr");
        Assert.AreEqual(
            cells[$"{recordHierarchyColumn}{mainRow}"],
            cells[$"{recordHierarchyColumn}{alternateRow}"]);
        Assert.AreEqual(2, DataRowCount(workbook, "Results"));
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

    private static IReadOnlyList<string> RowValues(ExtractionResultRow row) =>
        row.Cells.SelectMany(cell => cell.Values).Select(value => value.Value).ToArray();

    private static DatabaseBuildSpecification CreateDiscoveredSpecification(
        DiscoveryHostResult discovery,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames,
        RepeatedDataLayout layout)
    {
        Assert.IsTrue(discovery.Accepted, discovery.Failure?.Description);
        Assert.IsNotNull(discovery.FamilyCatalog);
        var configuration = new DiscoveryConfigurationSnapshot(
            discovery.Information.Select(item => new DiscoveryConfigurationItem(
                item.Identity,
                DiscoveryInformationDisposition.Selected)).ToArray(),
            sourceSetNames.Keys.Select(sourceSetId => new SourceSetDiscoveryConfiguration(
                sourceSetId,
                layout)).ToArray());
        var assignments = discovery.Information.ToDictionary(
            item => item.Identity,
            item => item.RowFamilyId
                ?? throw new AssertFailedException(
                    $"Discovery did not assign {item.Identity.StructuralPath} to a Row Family."));
        return DatabaseBuildSpecificationFactory.Create(
            configuration,
            new Dictionary<DiscoveryInformationIdentity, string>(),
            sources,
            sourceSetNames,
            discovery.FamilyCatalog,
            assignments);
    }

    private static async Task<GoodbooksFlowResult> RunGoodbooksFlowAsync(
        AlphaWorkspace workspace,
        DatabaseBuildSpecification specification,
        string runName)
    {
        var built = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            specification);
        Assert.IsTrue(built.Accepted, built.Failure?.Description);
        var generation = built.PublishedGeneration!;
        var databaseRows = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(
            StringComparer.Ordinal);
        foreach (var family in generation.Datasets.Single().RowFamilies)
        {
            var page = await workspace.ReadDatabaseFamilyPageAsync(generation, family.Id);
            databaseRows[family.AnchorPath] = page.Rows.Select(row =>
                (IReadOnlyList<string>)RowValues(row)).ToArray();
        }

        var extracted = await workspace.Extraction.ExtractAsync(
            OperationCorrelation.CreateNew(),
            generation);
        Assert.IsTrue(extracted.Accepted, extracted.Failure?.Description);
        var extraction = extracted.PublishedResult!;
        var routing = new ExportRoutingConfigurationPresentation();
        routing.Synchronize(generation);
        var configuration = routing.Capture();
        var exportDirectory = Path.Combine(workspace.Root, $"goodbooks-{runName}");
        Directory.CreateDirectory(exportDirectory);
        var exported = await workspace.Export.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, exportDirectory));
        Assert.IsTrue(exported.Accepted, exported.Failure?.Description);
        var workbook = configuration.Workbooks.Single();
        var workbookPath = Path.Combine(exportDirectory, workbook.FileName);
        using var document = SpreadsheetDocument.Open(workbookPath, isEditable: false);
        var worksheetNames = SheetNames(document);
        foreach (var worksheet in workbook.Worksheets)
        {
            var family = extraction.Datasets.SelectMany(dataset => dataset.RowFamilies)
                .Single(candidate => candidate.Id == worksheet.RowFamilyId);
            Assert.AreEqual(family.RowCount, DataRowCount(document, worksheet.Name));
            CollectionAssert.AreEqual(
                configuration.CreateIncludedOutputColumns(family.Id)
                    .Select(column => column.Header).ToArray(),
                ReadRowTexts(document, worksheet.Name, 1).ToArray(),
                worksheet.Name);
        }
        var worksheetCells = worksheetNames.ToDictionary(
            sheetName => sheetName,
            sheetName => (IReadOnlyList<string>)ReadCells(document, sheetName)
                .OrderBy(cell => cell.Key, StringComparer.Ordinal)
                .Select(cell => $"{cell.Key}={cell.Value}")
                .ToArray(),
            StringComparer.Ordinal);
        Assert.IsEmpty(Directory.GetFiles(exportDirectory, "*.incomplete"));
        return new GoodbooksFlowResult(
            generation,
            extraction,
            databaseRows,
            generation.Datasets.Single().RowFamilies.Select(family =>
                $"{family.Ordinal}|{family.Id}|{family.ParentRowFamilyId}|{family.RowCount}")
                .ToArray(),
            extraction.Datasets.Single().RowFamilies.Select(family =>
                $"{family.Ordinal}|{family.Id}|{family.ParentRowFamilyId}|{family.RowCount}")
                .ToArray(),
            worksheetNames,
            worksheetCells);
    }

    private static void AssertFamilyOwnsPaths(
        DatabaseGenerationSummary generation,
        string familyPath,
        params string[] expectedPaths)
    {
        var family = generation.Datasets.Single().RowFamilies.Single(candidate =>
            candidate.AnchorPath == familyPath);
        CollectionAssert.AreEqual(
            expectedPaths.Order(StringComparer.Ordinal).ToArray(),
            family.Mappings.SelectMany(mapping => mapping.DetailedIdentities)
                .Select(identity => identity.StructuralPath)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            familyPath);
    }

    private static void AssertWorksheetContains(
        GoodbooksFlowResult result,
        string sheetName,
        string value) =>
        Assert.IsTrue(result.WorksheetCells[sheetName].Any(cell =>
            cell.EndsWith($"={value}", StringComparison.Ordinal)));

    private static void AssertWorksheetDoesNotContain(
        GoodbooksFlowResult result,
        string sheetName,
        string value) =>
        Assert.IsFalse(result.WorksheetCells[sheetName].Any(cell =>
            cell.EndsWith($"={value}", StringComparison.Ordinal)));

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
        var families = datasets.SelectMany(dataset => dataset.RowFamilies
            .OrderBy(family => family.Ordinal)
            .Select(family => (Dataset: dataset, Family: family))).ToArray();
        var combinedId = WorkbookDefinitionId.CreateNew();
        var separateId = WorkbookDefinitionId.CreateNew();
        var emptyId = WorkbookDefinitionId.CreateNew();
        var sheets = families.ToDictionary(
            item => item.Family.Id,
            _ => WorksheetDefinitionId.CreateNew());
        var combinedFamilies = families.Where(item => item.Dataset.Ordinal != 3).ToArray();
        var separateFamilies = families.Where(item => item.Dataset.Ordinal == 3).ToArray();
        return new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                 combinedId,
                 "Combined.xlsx",
                 1,
                 combinedFamilies.Select((item, index) => new WorksheetDefinition(
                     sheets[item.Family.Id],
                     combinedId,
                     item.Dataset.SourceSetId,
                     item.Family.Id,
                     $"{item.Dataset.DisplayName} - {item.Family.Ordinal}",
                     index + 1)).ToArray()),
             new WorkbookDefinition(
                 separateId,
                 "Separate.xlsx",
                 2,
                 separateFamilies.Select((item, index) => new WorksheetDefinition(
                     sheets[item.Family.Id],
                     separateId,
                     item.Dataset.SourceSetId,
                     item.Family.Id,
                     $"{item.Dataset.DisplayName} - {item.Family.Ordinal}",
                     index + 1)).ToArray()),
             new WorkbookDefinition(emptyId, "Empty.xlsx", 3, [])],
            families.Select(item => new SourceSetExportConfiguration(
                item.Dataset.SourceSetId,
                item.Family.Id,
                true,
                sheets[item.Family.Id],
                item.Family.Columns.Select((column, columnIndex) => new ExportFieldConfiguration(
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

    private static IReadOnlyList<string> ReadRowTexts(
        SpreadsheetDocument document,
        string sheetName,
        uint rowIndex) =>
        GetWorksheet(document, sheetName).Descendants<Row>()
            .Single(row => row.RowIndex?.Value == rowIndex)
            .Elements<Cell>()
            .Select(cell => cell.InlineString?.Text?.Text
                ?? throw new AssertFailedException("A tested cell requires inline text."))
            .ToArray();

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

    private sealed record GoodbooksFlowResult(
        DatabaseGenerationSummary Generation,
        ExtractionResultSummary Extraction,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> DatabaseRows,
        IReadOnlyList<string> FamilySignatures,
        IReadOnlyList<string> ExtractionSignatures,
        IReadOnlyList<string> WorksheetNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> WorksheetCells);

    private sealed class AlphaWorkspace : IDisposable
    {
        private readonly SourceInterpreter _interpreter;
        private readonly ApplicationPaths _paths;

        public AlphaWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "CIA.SPR142.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _paths = ApplicationPaths.FromLocalApplicationData(
                Path.Combine(Root, "LocalAppData"));
            Repository = new StructuredInformationRepository(_paths);
            var history = new NullHistory();
            var cancellation = new CooperativeOperationCancellation(history);
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            Discovery = new DiscoveryService(
                _interpreter,
                new SourceOccurrenceReader([], NullLogger<SourceOccurrenceReader>.Instance),
                Repository);
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

        public async Task<LoadedSourceContract> LoadStructuralFixtureAsync(
            SourceSetId sourceSetId,
            string fileName)
        {
            var sourcePath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "StructuralDiscovery",
                fileName);
            var targetPath = Path.Combine(Root, sourceSetId.ToString(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
            var intake = new SourceIntakeService(new ArchiveExtractionService(_paths));
            var result = await intake.LoadAsync(
                SourceSelectionKind.XmlFile,
                targetPath,
                SourceLoadSettings.Default,
                progress: null,
                cancellationToken: default,
                sourceSetId);
            Assert.IsTrue(result.Accepted, result.Failure?.Description);
            Assert.HasCount(1, result.Sources);
            var loaded = result.Sources[0] with { SourceSetId = sourceSetId };
            Assert.AreEqual(LoadedSourceStatus.Ready, loaded.Status);
            return loaded;
        }

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

        public async Task<(LoadedSourceContract Source, DatabaseBuildSpecification Specification)>
            CreateSameLeafSpecificationAsync(RepeatedDataLayout layout)
        {
            var sourceSet = SourceSetId.CreateNew();
            var source = CopyFixture(
                sourceSet,
                "same-leaf-different-ancestry.xml",
                "StructuralDiscovery");
            var interpreted = await InterpretAsync(source);
            var identities = interpreted.Values
                .Where(value => value.Lineage is not null)
                .Select(value => HierarchySourceOccurrence.FromInterpretedValue(
                    sourceSet,
                    value).Identity)
                .Where(identity => identity.InformationType is "pnr" or "mfr" or "keyword")
                .Distinct()
                .ToArray();
            var overrides = identities
                .Where(identity => identity.InformationType is "pnr" or "mfr")
                .ToDictionary(
                    identity => identity,
                    identity => identity.InformationType == "pnr"
                        ? "Part Number"
                        : "Vendor Code");
            var fields = DatabaseTagMapper.CreateFieldMappings(
                sourceSet,
                identities.Select(identity => new DiscoveryConfigurationItem(
                    identity,
                    DiscoveryInformationDisposition.Selected)),
                overrides);
            return (source, new DatabaseBuildSpecification(
                [new DatabaseDatasetBuildSpecification(
                    sourceSet,
                    "Tools",
                    1,
                    layout,
                    [source],
                    fields)]));
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

        public async Task<DatabaseReviewPage> ReadDatabaseFamilyPageAsync(
            DatabaseGenerationSummary generation,
            RowFamilyId rowFamilyId)
        {
            var page = await Repository.ReadPublishedDatabasePageAsync(
                new DatabaseReviewQuery(
                    generation.OperationId,
                    rowFamilyId.SourceSetId,
                    rowFamilyId,
                    1,
                    DatabaseReviewLimits.MaximumRowsPerPage,
                    null,
                    DatabaseRowInclusionFilter.All));
            Assert.IsNotNull(page);
            Assert.HasCount(page.TotalRowCount, page.Rows);
            return page;
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

        private LoadedSourceContract CopyFixture(
            SourceSetId set,
            string fileName,
            string fixtureDirectory = "AlphaRegression")
        {
            var source = Path.Combine(
                AppContext.BaseDirectory, "Fixtures", fixtureDirectory, fileName);
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
