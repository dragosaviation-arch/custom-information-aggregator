using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Export;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ExportRoutingConfigurationTests
{
    [TestMethod]
    public void RowFamiliesDefaultToSeparateWorksheetsInOneWorkbookAndStayIndependent()
    {
        var generation = CreateFamilyGeneration("Set A", "/root/orders", "/root/vendors");
        var routing = CreateRouting(generation);

        Assert.HasCount(3, routing.SourceSets);
        Assert.HasCount(1, routing.Workbooks);
        var snapshot = routing.Capture();
        Assert.HasCount(3, snapshot.Workbooks.Single().Worksheets);
        CollectionAssert.AreEqual(
            new[] { "Set A - Root", "Set A - orders", "Set A - vendors" },
            snapshot.Workbooks.Single().Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.HasCount(3, snapshot.SourceSets.Select(set => set.RowFamilyId).Distinct());
        Assert.IsTrue(snapshot.SourceSets.All(set => set.Fields.Single().ExcelHeader == "Shared"));
        Assert.HasCount(3, snapshot.SourceSets.Select(set =>
            set.Fields.Single().DatabaseColumnIdentity).Distinct());

        routing.SourceSets[1].IsEnabled = false;
        var validation = ExportConfigurationValidator.Validate(routing.Capture(), generation);
        Assert.IsTrue(validation.IsValid);
        Assert.HasCount(2, validation.RunnableWorkbooks.Single().Worksheets);
        Assert.IsFalse(validation.RunnableWorkbooks.Single().Worksheets.Any(sheet =>
            sheet.Worksheet.RowFamilyId == routing.SourceSets[1].RowFamilyId));
    }

    [TestMethod]
    public void FamilyWorksheetNamesAreSafeTruncatedAndCollisionResolvedInCanonicalOrder()
    {
        var generation = CreateFamilyGeneration(
            "Very/Long:Source*Set?Name[For]Collision",
            "/root/alpha/item",
            "/root/beta/item");
        var routing = CreateRouting(generation);
        var names = routing.SourceSets.OrderBy(set => set.FamilyOrder)
            .Select(set => set.WorksheetName).ToArray();

        Assert.IsTrue(names.All(ExportConfigurationValidator.IsValidWorksheetName));
        Assert.IsTrue(names.All(name => name.Length <= 31));
        Assert.HasCount(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(names[1].EndsWith(" (2)", StringComparison.Ordinal));
        Assert.IsTrue(names[2].EndsWith(" (3)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FamilyConfigurationRoundTripRestoresRoutesEnablementAndStableIds()
    {
        var generation = CreateFamilyGeneration("Set A", "/root/orders");
        var routing = CreateRouting(generation);
        var repeated = routing.SourceSets.Single(set =>
            set.RowFamilyId.Kind == RowFamilyKind.Repeated);
        repeated.IsEnabled = false;
        repeated.Fields.Single().ExcelHeader = "Restored Header";
        var separateWorkbook = routing.CreateWorkbookFor(repeated.RowFamilyId);
        var captured = routing.Capture();
        var roundTrip = JsonSerializer.Deserialize<ExportConfigurationSnapshot>(
            JsonSerializer.Serialize(captured));
        Assert.IsNotNull(roundTrip);

        var restored = new ExportRoutingConfigurationPresentation();
        restored.Restore(generation, roundTrip);
        var restoredFamily = restored.GetSet(repeated.RowFamilyId);

        Assert.IsFalse(restoredFamily.IsEnabled);
        Assert.AreEqual("Restored Header", restoredFamily.Fields.Single().ExcelHeader);
        Assert.AreEqual(separateWorkbook.WorkbookDefinitionId, restoredFamily.WorkbookDefinitionId);
        Assert.AreEqual(repeated.WorksheetDefinitionId, restoredFamily.WorksheetDefinitionId);
        Assert.AreEqual(repeated.RowFamilyId, restoredFamily.RowFamilyId);
    }

    [TestMethod]
    public void MissingLegacyConfigurationRegeneratesDeterministicFamilyDefaults()
    {
        var generation = CreateFamilyGeneration("Set A", "/root/orders", "/root/vendors");
        var first = new ExportRoutingConfigurationPresentation();
        var second = new ExportRoutingConfigurationPresentation();

        first.Restore(generation, configuration: null);
        second.Restore(generation, configuration: null);

        CollectionAssert.AreEqual(
            first.SourceSets.Select(set => set.WorksheetName).ToArray(),
            second.SourceSets.Select(set => set.WorksheetName).ToArray());
        CollectionAssert.AreEqual(
            first.SourceSets.Select(set => set.WorksheetOrder).ToArray(),
            second.SourceSets.Select(set => set.WorksheetOrder).ToArray());
        Assert.IsTrue(first.SourceSets.All(set => set.IsEnabled));
        Assert.IsTrue(second.SourceSets.All(set => set.IsEnabled));
    }

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

        Assert.IsTrue(validation.Failures.Any(failure => failure.Code == "row-family-route-count"));
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
        Assert.IsTrue(routing.MoveWorksheet(second.RowFamilyId, -1));

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
        var workbook = routing.CreateWorkbookFor(second.RowFamilyId);

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
        routing.CreateWorkbookFor(routing.SourceSets[0].RowFamilyId);

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

    private static DatabaseGenerationSummary CreateFamilyGeneration(
        string displayName,
        params string[] repeatedPaths)
    {
        var sourceSetId = SourceSetId.CreateNew();
        var root = new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/root");
        var families = new[] { root }.Concat(repeatedPaths.Select(path =>
            new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, path))).ToArray();
        var mappings = families.Select((family, index) =>
        {
            var identity = new DiscoveryInformationIdentity(
                sourceSetId,
                $"{family.AnchorPath}/tag",
                "tag",
                SourceValueCandidateKind.Element,
                $"{family.AnchorPath}/tag");
            return new DatabaseFieldMapping(family, "Shared", false, [identity]);
        }).ToArray();
        var columns = mappings.Select((mapping, index) => new DatabaseColumnDefinition(
            new DatabaseColumnIdentity(
                mapping.RowFamilyId,
                mapping.FieldKey,
                DatabaseRepeatCoordinatePath.Empty),
            mapping.EffectiveName,
            index + 1)).ToArray();
        var rowFamilies = families.Select((family, index) =>
            new DatabaseRowFamilySummary(
                family,
                family.Kind == RowFamilyKind.Root ? null : root,
                index + 1,
                family.Kind == RowFamilyKind.Root ? "Root" : family.AnchorPath,
                rowCount: 1,
                includedRowCount: 1,
                valueCount: 1,
                [columns[index] with { Ordinal = 1 }],
                [mappings[index]])).ToArray();
        return new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            [new DatabaseDatasetSummary(
                sourceSetId,
                displayName,
                1,
                RepeatedDataLayout.StructuralRows,
                rowFamilies.Length,
                rowFamilies.Length,
                columns,
                mappings,
                rowFamilies)]);
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
