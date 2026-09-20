using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Export;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ExportRoutingConfigurationTests
{
    [TestMethod]
    public void SetsKeepIndependentFieldsHeadersCompanionsAndTypedIdentities()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var first = routing.SourceSets[0];
        var second = routing.SourceSets[1];
        var firstField = first.Fields.Single();
        var secondField = second.Fields.Single();

        firstField.ExcelHeader = "First Name";
        firstField.IsSourceIdExported = true;
        first.MetadataFields[0].IsExported = true;
        secondField.IsExported = false;

        var snapshot = routing.Capture();
        var firstConfiguration = snapshot.SourceSets.Single(set =>
            set.SourceSetId == first.SourceSetId);
        var secondConfiguration = snapshot.SourceSets.Single(set =>
            set.SourceSetId == second.SourceSetId);
        Assert.AreEqual("First Name", firstConfiguration.Fields.Single().ExcelHeader);
        Assert.IsTrue(firstConfiguration.Fields.Single().IsSourceIdCompanionIncluded);
        Assert.IsFalse(secondConfiguration.Fields.Single().IsValueIncluded);
        Assert.IsTrue(firstConfiguration.MetadataFields[0].IsIncluded);
        Assert.IsFalse(secondConfiguration.MetadataFields[0].IsIncluded);
        Assert.AreEqual(
            generation.Datasets[0].Columns.Single().Identity,
            firstConfiguration.Fields.Single().DatabaseColumnIdentity);
        Assert.AreNotEqual(
            firstConfiguration.Fields.Single().DatabaseColumnIdentity,
            secondConfiguration.Fields.Single().DatabaseColumnIdentity);
    }

    [TestMethod]
    public void SetsMayShareWorkbookButAlwaysOwnDifferentWorksheets()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var snapshot = routing.Capture();

        Assert.HasCount(1, snapshot.Workbooks);
        Assert.HasCount(2, snapshot.Workbooks.Single().Worksheets);
        Assert.HasCount(2, snapshot.Workbooks.Single().Worksheets
            .Select(worksheet => worksheet.WorksheetDefinitionId).Distinct());
        Assert.HasCount(2, snapshot.Workbooks.Single().Worksheets
            .Select(worksheet => worksheet.SourceSetId).Distinct());
        Assert.IsTrue(ExportConfigurationValidator.Validate(snapshot, generation).IsValid);
    }

    [TestMethod]
    public void ValidatorRejectsMultipleRoutesForOneSet()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var snapshot = routing.Capture();
        var workbook = snapshot.Workbooks.Single();
        var first = workbook.Worksheets[0];
        var duplicateRoute = new WorksheetDefinition(
            WorksheetDefinitionId.CreateNew(),
            workbook.WorkbookDefinitionId,
            first.SourceSetId,
            "Duplicate route",
            3);
        var invalid = new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                workbook.WorkbookDefinitionId,
                workbook.FileName,
                workbook.Order,
                [.. workbook.Worksheets, duplicateRoute])],
            snapshot.SourceSets);

        var validation = ExportConfigurationValidator.Validate(invalid, generation);

        Assert.IsTrue(validation.Failures.Any(failure => failure.Code == "source-set-route-count"));
    }

    [TestMethod]
    public void ValidatorRejectsSharedWorksheetRoute()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var snapshot = routing.Capture();
        var firstRoute = snapshot.Workbooks.Single().Worksheets[0];
        var second = snapshot.SourceSets[1];
        var invalidSecond = new SourceSetExportConfiguration(
            second.SourceSetId,
            true,
            firstRoute.WorksheetDefinitionId,
            second.Fields,
            second.MetadataFields);
        var invalid = new ExportConfigurationSnapshot(
            snapshot.Workbooks,
            [snapshot.SourceSets[0], invalidSecond]);

        var validation = ExportConfigurationValidator.Validate(invalid, generation);

        Assert.IsTrue(validation.Failures.Any(failure => failure.Code == "shared-worksheet-route"));
    }

    [TestMethod]
    public void StableWorkbookAndWorksheetIdsSurviveRenameAndReorder()
    {
        var routing = CreateRouting(CreateGeneration());
        var workbook = routing.Workbooks.Single();
        var first = routing.SourceSets[0];
        var second = routing.SourceSets[1];
        var workbookId = workbook.WorkbookDefinitionId;
        var firstWorksheetId = first.WorksheetDefinitionId;
        var secondWorksheetId = second.WorksheetDefinitionId;

        workbook.FileName = "Renamed.xlsx";
        first.WorksheetName = "Renamed sheet";
        Assert.IsTrue(routing.MoveWorksheet(second.SourceSetId, -1));

        Assert.AreEqual(workbookId, workbook.WorkbookDefinitionId);
        Assert.AreEqual(firstWorksheetId, first.WorksheetDefinitionId);
        Assert.AreEqual(secondWorksheetId, second.WorksheetDefinitionId);
        Assert.AreEqual(2, first.WorksheetOrder);
        Assert.AreEqual(1, second.WorksheetOrder);
    }

    [TestMethod]
    public void DefaultWorksheetOrderFollowsSourceSetOrder()
    {
        var routing = CreateRouting(CreateGeneration());

        CollectionAssert.AreEqual(
            new[] { "Set A", "Set B" },
            routing.SourceSets.OrderBy(set => set.WorksheetOrder)
                .Select(set => set.DisplayName).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            routing.SourceSets.OrderBy(set => set.WorksheetOrder)
                .Select(set => set.WorksheetOrder).ToArray());
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public void DisabledSetRetainsRoutingFieldsHeadersAndMetadataButProducesNoOutput()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var set = routing.SourceSets[0];
        var routeId = set.WorksheetDefinitionId;
        set.Fields[0].ExcelHeader = "Retained";
        set.Fields[0].IsSourceIdExported = true;
        set.MetadataFields[0].IsExported = true;
        set.IsEnabled = false;

        var snapshot = routing.Capture();
        var captured = snapshot.SourceSets.Single(candidate =>
            candidate.SourceSetId == set.SourceSetId);

        Assert.IsFalse(captured.IsEnabled);
        Assert.AreEqual(routeId, captured.WorksheetDefinitionId);
        Assert.AreEqual("Retained", captured.Fields[0].ExcelHeader);
        Assert.IsTrue(captured.Fields[0].IsSourceIdCompanionIncluded);
        Assert.IsTrue(captured.MetadataFields[0].IsIncluded);
        Assert.IsTrue(ExportConfigurationValidator.Validate(snapshot, generation).IsValid);
    }

    [TestMethod]
    public void WorkbookWithoutEnabledSetsIsOmittedAndAssignedWorkbookCannotBeDeleted()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var assigned = routing.Workbooks.Single();
        var unused = routing.CreateWorkbook("Unused.xlsx");
        foreach (var set in routing.SourceSets)
        {
            set.IsEnabled = false;
        }

        var validation = ExportConfigurationValidator.Validate(routing.Capture(), generation);

        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.RunnableWorkbooks);
        Assert.IsFalse(routing.TryDeleteWorkbook(assigned.WorkbookDefinitionId));
        Assert.IsTrue(routing.TryDeleteWorkbook(unused.WorkbookDefinitionId));
    }

    [TestMethod]
    public void InvalidReservedAndDuplicateWorkbookFilenamesAreActionable()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        routing.Workbooks.Single().FileName = "CON.xlsx";
        var invalid = ExportConfigurationValidator.Validate(routing.Capture(), generation);
        Assert.IsTrue(invalid.Failures.Any(failure => failure.Code == "invalid-workbook-filename"));

        routing.Workbooks.Single().FileName = "Shared.xlsx";
        routing.CreateWorkbook("shared.XLSX");
        var duplicate = ExportConfigurationValidator.Validate(routing.Capture(), generation);
        Assert.IsTrue(duplicate.Failures.Any(failure => failure.Code == "duplicate-workbook-filename"));
    }

    [TestMethod]
    public void InvalidAndDuplicateWorksheetNamesAreActionableWithinWorkbook()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        routing.SourceSets[0].WorksheetName = "Invalid/name";
        var invalid = ExportConfigurationValidator.Validate(routing.Capture(), generation);
        Assert.IsTrue(invalid.Failures.Any(failure => failure.Code == "invalid-worksheet-name"));

        routing.SourceSets[0].WorksheetName = "Results";
        routing.SourceSets[1].WorksheetName = "results";
        var duplicate = ExportConfigurationValidator.Validate(routing.Capture(), generation);
        Assert.IsTrue(duplicate.Failures.Any(failure => failure.Code == "duplicate-worksheet-name"));
    }

    [TestMethod]
    public void ReassignmentCreatesOwnWorksheetAndRetainsIdentityAndConfiguration()
    {
        var generation = CreateGeneration();
        var routing = CreateRouting(generation);
        var second = routing.SourceSets[1];
        var worksheetId = second.WorksheetDefinitionId;
        second.Fields[0].ExcelHeader = "Independent";
        var workbook = routing.CreateWorkbookFor(second.SourceSetId);

        Assert.AreEqual(workbook.WorkbookDefinitionId, second.WorkbookDefinitionId);
        Assert.AreEqual(worksheetId, second.WorksheetDefinitionId);
        Assert.AreEqual("Independent", second.Fields[0].ExcelHeader);
        Assert.HasCount(1, routing.Capture().Workbooks.Single(candidate =>
            candidate.WorkbookDefinitionId == workbook.WorkbookDefinitionId).Worksheets);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public void RoutingAndFieldChangesDoNotMutateDatabaseGeneration()
    {
        var generation = CreateGeneration();
        var before = JsonSerializer.Serialize(generation);
        var routing = CreateRouting(generation);
        routing.SourceSets[0].Fields[0].ExcelHeader = "Changed";
        routing.SourceSets[0].Fields[0].IsExported = false;
        routing.CreateWorkbookFor(routing.SourceSets[0].SourceSetId);

        Assert.AreEqual(before, JsonSerializer.Serialize(generation));
    }

    [TestMethod]
    public void EmptyEnabledAssignmentsProduceNoRunnableWorkbook()
    {
        var generation = CreateGeneration();
        var snapshot = new ExportConfigurationSnapshot([], generation.Datasets.Select(dataset =>
            new SourceSetExportConfiguration(
                dataset.SourceSetId,
                false,
                worksheetDefinitionId: null,
                dataset.Columns.Select(column => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    false)).ToArray(),
                [])).ToArray());

        var validation = ExportConfigurationValidator.Validate(snapshot, generation);

        Assert.IsTrue(validation.IsValid);
        Assert.IsEmpty(validation.RunnableWorkbooks);
    }

    private static ExportRoutingConfigurationPresentation CreateRouting(
        DatabaseGenerationSummary generation)
    {
        var routing = new ExportRoutingConfigurationPresentation();
        routing.Synchronize(generation);
        return routing;
    }

    private static DatabaseGenerationSummary CreateGeneration()
    {
        var firstSetId = SourceSetId.CreateNew();
        var secondSetId = SourceSetId.CreateNew();
        return new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            [
                CreateDataset(firstSetId, "Set A", 1, "name"),
                CreateDataset(secondSetId, "Set B", 2, "name")
            ]);
    }

    private static DatabaseDatasetSummary CreateDataset(
        SourceSetId sourceSetId,
        string displayName,
        int ordinal,
        string informationType)
    {
        var identity = new DiscoveryInformationIdentity(
            sourceSetId,
            $"/root/{informationType}",
            informationType,
            SourceValueCandidateKind.Element,
            $"/root/{informationType}");
        var mapping = new DatabaseFieldMapping(
            DatabaseLogicalFieldIdentity.Create(identity),
            informationType,
            false,
            [identity]);
        var column = new DatabaseColumnDefinition(
            new DatabaseColumnIdentity(
                sourceSetId,
                mapping.FieldKey,
                DatabaseRepeatCoordinatePath.Empty),
            informationType,
            1);
        return new DatabaseDatasetSummary(
            sourceSetId,
            displayName,
            ordinal,
            RepeatedDataLayout.StructuralRows,
            1,
            1,
            [column],
            [mapping]);
    }
}
