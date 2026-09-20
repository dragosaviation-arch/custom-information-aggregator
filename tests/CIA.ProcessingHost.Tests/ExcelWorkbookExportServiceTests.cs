using System.Runtime.CompilerServices;
using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Export;
using CIA.ProcessingHost.Hosting;
using CIA.ProcessingHost.Operations;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ExcelWorkbookExportServiceTests
{
    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task RoutedBatchStreamsSemanticRowsAcrossWorkbooksAndWorksheets()
    {
        using var workspace = new ExportWorkspace();
        var first = DatasetFixture.Create("Set A", 1, twoColumns: true);
        var second = DatasetFixture.Create("Set B", 2);
        var third = DatasetFixture.Create(
            "Set C",
            3,
            repeatCoordinates: new DatabaseRepeatCoordinatePath([2, 1]));
        var disabled = DatasetFixture.Create("Disabled", 4);
        first.AddRow(
            first.Cell("  value one  ", "second"),
            first.CellForColumn(1, "other one"));
        first.AddRow(first.CellForColumn(1, "other two"));
        second.AddRow(second.Cell("  =SUM(A1:A2)  "));
        third.AddRow(third.Cell("third"));
        disabled.AddRow(disabled.Cell("disabled"));
        var extraction = CreateExtraction(first, second, third, disabled);
        var configuration = CreateConfiguration(first, second, third, disabled);
        workspace.Source.Set(extraction, first, second, third, disabled);

        var correlation = OperationCorrelation.CreateNew();
        var result = await workspace.Service.ExportAsync(
            correlation,
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, workspace.Root));

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.IsNotNull(result.Batch);
        Assert.AreEqual(correlation.OperationId, result.Batch.OperationId);
        Assert.AreEqual(extraction.OperationId, result.Batch.ExtractionResultId);
        Assert.HasCount(2, result.Batch.Workbooks);
        Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "Disabled.xlsx")));
        CollectionAssert.AreEqual(
            new[] { second.SourceSetId, first.SourceSetId, third.SourceSetId },
            workspace.Source.StreamedSourceSets.ToArray());

        var combinedPath = Path.Combine(workspace.Root, "Combined.xlsx");
        var separatePath = Path.Combine(workspace.Root, "Separate.xlsx");
        Assert.IsTrue(File.Exists(combinedPath));
        Assert.IsTrue(File.Exists(separatePath));
        using var combined = SpreadsheetDocument.Open(combinedPath, isEditable: false);
        CollectionAssert.AreEqual(
            new[] { "Set B", "Set A" },
            SheetNames(combined).ToArray());

        var secondCells = ReadCells(combined, "Set B");
        Assert.AreEqual("Shared", secondCells["A1"]);
        Assert.AreEqual("  =SUM(A1:A2)  ", secondCells["A2"]);
        Assert.AreEqual(CellValues.InlineString, ReadCell(combined, "Set B", "A2").DataType?.Value);
        Assert.IsNull(ReadCell(combined, "Set B", "A2").CellFormula);
        Assert.AreEqual(
            SpaceProcessingModeValues.Preserve,
            ReadCell(combined, "Set B", "A2").InlineString?.Text?.Space?.Value);

        var firstCells = ReadCells(combined, "Set A");
        Assert.AreEqual("Shared", firstCells["A1"]);
        Assert.AreEqual("Shared SourceId", firstCells["B1"]);
        Assert.AreEqual("Other", firstCells["C1"]);
        Assert.AreEqual("Source File", firstCells["D1"]);
        Assert.AreEqual("Record Hierarchy", firstCells["E1"]);
        Assert.AreEqual("  value one   | second", firstCells["A2"]);
        Assert.AreEqual(first.Rows[0].Source.SourceId.ToString(), firstCells["B2"]);
        Assert.AreEqual("other one", firstCells["C2"]);
        Assert.AreEqual(first.Rows[0].Source.SourceFileName, firstCells["D2"]);
        Assert.AreEqual(
            DatabaseRowMetadataProjection.GetValue(
                first.Rows[0].Source,
                DatabaseRowMetadataProjection.CreateRecordHierarchy(
                    first.Rows[0].Cells.SelectMany(cell => cell.Values)
                        .Select(value => value.Lineage)),
                first.Rows[0].Cells.SelectMany(cell => cell.Values).Select(value =>
                    new DatabaseRowMetadataValue(value.DetailedIdentity, value.Lineage)),
                DatabaseMetadataField.RecordHierarchy),
            firstCells["E2"]);
        Assert.IsFalse(firstCells.ContainsKey("A3"));
        Assert.IsFalse(firstCells.ContainsKey("B3"));
        Assert.AreEqual("other two", firstCells["C3"]);

        var combinedSummary = result.Batch.Workbooks[0];
        Assert.HasCount(2, combinedSummary.Worksheets);
        Assert.AreEqual(1, combinedSummary.Worksheets[0].RowCount);
        Assert.AreEqual(2, combinedSummary.Worksheets[1].RowCount);
        Assert.AreEqual(5, combinedSummary.Worksheets[1].ColumnCount);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task CandidateFailureAndCancellationPublishNoWorkbook()
    {
        using var failedWorkspace = new ExportWorkspace();
        var first = DatasetFixture.Create("First", 1);
        var second = DatasetFixture.Create("Second", 2);
        first.AddRow(first.Cell("valid"));
        second.AddRow(second.Cell(new string('x', ExcelWorkbookLimits.MaximumCellTextLength + 1)));
        var extraction = CreateExtraction(first, second);
        var configuration = CreateSeparateWorkbookConfiguration(first, second);
        failedWorkspace.Source.Set(extraction, first, second);

        var failed = await failedWorkspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, failedWorkspace.Root));

        Assert.IsFalse(failed.Accepted);
        Assert.AreEqual("export-cell-limit-exceeded", failed.Failure?.Code);
        Assert.IsEmpty(Directory.GetFiles(failedWorkspace.Root, "*.xlsx"));
        Assert.IsEmpty(Directory.GetFiles(failedWorkspace.Root, "*.incomplete"));

        using var cancelledWorkspace = new ExportWorkspace();
        var cancellable = DatasetFixture.Create("Cancellable", 1);
        cancellable.AddRow(cancellable.Cell("value"));
        var cancellableExtraction = CreateExtraction(cancellable);
        var cancellableConfiguration = CreateSeparateWorkbookConfiguration(cancellable);
        using var cancellation = new CancellationTokenSource();
        cancelledWorkspace.Source.Set(cancellableExtraction, cancellable);
        cancelledWorkspace.Source.BeforeYield = cancellation.Cancel;

        var cancelled = await cancelledWorkspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            cancellableExtraction,
            cancellableConfiguration,
            CreatePublicationPlan(
                cancellableConfiguration,
                cancellableExtraction,
                cancelledWorkspace.Root),
            cancellation.Token);

        Assert.IsFalse(cancelled.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, cancelled.Completion.Outcome);
        Assert.IsEmpty(Directory.GetFiles(cancelledWorkspace.Root, "*.xlsx"));
        Assert.IsEmpty(Directory.GetFiles(cancelledWorkspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task ExistingTargetIsRejectedWithoutPerWorkbookOverwriteAuthorization()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("value"));
        var extraction = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        workspace.Source.Set(extraction, dataset);
        var target = Path.Combine(workspace.Root, "Set.xlsx");
        await File.WriteAllTextAsync(target, "existing");

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, workspace.Root));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("export-overwrite-not-authorized", result.Failure?.Code);
        Assert.AreEqual("existing", await File.ReadAllTextAsync(target));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
        Assert.IsEmpty(workspace.Source.StreamedSourceSets);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task MixedAuthorizedOverwriteRenamedTargetAndNewTargetPublishTogether()
    {
        using var workspace = new ExportWorkspace();
        var overwritten = DatasetFixture.Create("Overwrite", 1);
        var renamed = DatasetFixture.Create("Renamed", 2);
        var created = DatasetFixture.Create("Created", 3);
        overwritten.AddRow(overwritten.Cell("replacement"));
        renamed.AddRow(renamed.Cell("renamed"));
        created.AddRow(created.Cell("new"));
        var extraction = CreateExtraction(overwritten, renamed, created);
        var configuration = CreateSeparateWorkbookConfiguration(overwritten, renamed, created);
        var renamedWorkbook = configuration.Workbooks[1];
        configuration = new ExportConfigurationSnapshot(
            [configuration.Workbooks[0],
             new WorkbookDefinition(
                 renamedWorkbook.WorkbookDefinitionId,
                 "Different name.xlsx",
                 renamedWorkbook.Order,
                 renamedWorkbook.Worksheets),
             configuration.Workbooks[2]],
            configuration.SourceSets);
        workspace.Source.Set(extraction, overwritten, renamed, created);
        var overwrittenPath = Path.Combine(workspace.Root, "Overwrite.xlsx");
        var unrelatedOriginalPath = Path.Combine(workspace.Root, "Renamed.xlsx");
        await File.WriteAllTextAsync(overwrittenPath, "original overwrite");
        await File.WriteAllTextAsync(unrelatedOriginalPath, "original renamed collision");
        var overwriteIds = new HashSet<WorkbookDefinitionId>
        {
            configuration.Workbooks[0].WorkbookDefinitionId
        };

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(
                configuration,
                extraction,
                workspace.Root,
                overwriteIds));

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.AreEqual("original renamed collision", await File.ReadAllTextAsync(unrelatedOriginalPath));
        Assert.IsTrue(File.Exists(overwrittenPath));
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "Different name.xlsx")));
        Assert.IsTrue(File.Exists(Path.Combine(workspace.Root, "Created.xlsx")));
        Assert.HasCount(3, result.Batch!.Workbooks);
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.backup"));
    }

    [TestMethod]
    public async Task CandidateFailurePreservesEveryAuthorizedExistingTarget()
    {
        using var workspace = new ExportWorkspace();
        var existing = DatasetFixture.Create("Existing", 1);
        var failing = DatasetFixture.Create("Failing", 2);
        existing.AddRow(existing.Cell("replacement"));
        failing.AddRow(failing.Cell(new string('x', ExcelWorkbookLimits.MaximumCellTextLength + 1)));
        var extraction = CreateExtraction(existing, failing);
        var configuration = CreateSeparateWorkbookConfiguration(existing, failing);
        workspace.Source.Set(extraction, existing, failing);
        var existingPath = Path.Combine(workspace.Root, "Existing.xlsx");
        await File.WriteAllTextAsync(existingPath, "original");

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(
                configuration,
                extraction,
                workspace.Root,
                new HashSet<WorkbookDefinitionId>
                {
                    configuration.Workbooks[0].WorkbookDefinitionId
                }));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("export-cell-limit-exceeded", result.Failure?.Code);
        Assert.AreEqual("original", await File.ReadAllTextAsync(existingPath));
        Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "Failing.xlsx")));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.backup"));
    }

    [TestMethod]
    public async Task ShortStreamCannotPublishCapturedExtractionDataset()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("first"));
        dataset.AddRow(dataset.Cell("second"));
        var extraction = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        workspace.Source.Set(extraction, dataset);
        workspace.Source.SetRows(dataset.SourceSetId, dataset.Rows.Take(1).ToArray());

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, workspace.Root));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("extraction-row-count-mismatch", result.Failure?.Code);
        AssertNoExportArtifacts(workspace.Root);
    }

    [TestMethod]
    public async Task ExtraStreamCannotPublishCapturedExtractionDataset()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("captured"));
        var extraction = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        dataset.AddRow(dataset.Cell("unexpected"));
        workspace.Source.Set(extraction, dataset);

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, workspace.Root));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("extraction-row-count-mismatch", result.Failure?.Code);
        AssertNoExportArtifacts(workspace.Root);
    }

    [TestMethod]
    public async Task PublicationChangeDuringWorksheetGenerationPublishesNothing()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("first"));
        dataset.AddRow(dataset.Cell("second"));
        var captured = CreateExtraction(dataset);
        var replacement = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        workspace.Source.Set(captured, dataset);
        workspace.Source.BeforeYield = () =>
        {
            workspace.Source.ChangePublishedResult(replacement);
            workspace.Source.BeforeYield = null;
        };

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            captured,
            configuration,
            CreatePublicationPlan(configuration, captured, workspace.Root));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("extraction-result-changed-during-export", result.Failure?.Code);
        AssertNoExportArtifacts(workspace.Root);
    }

    [TestMethod]
    public async Task PublicationChangeAfterCandidatesBeforeBatchPublicationPublishesNothing()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("value"));
        var captured = CreateExtraction(dataset);
        var replacement = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        workspace.Source.Set(captured, dataset);
        workspace.Source.BeforePublishedResultRead = readNumber =>
        {
            if (readNumber == 2)
            {
                workspace.Source.ChangePublishedResult(replacement);
            }
        };

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            captured,
            configuration,
            CreatePublicationPlan(configuration, captured, workspace.Root));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("extraction-result-changed-during-export", result.Failure?.Code);
        Assert.AreEqual(2, workspace.Source.PublishedResultReadCount);
        AssertNoExportArtifacts(workspace.Root);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public void BatchPublicationRollsBackFilesMovedBeforeARace()
    {
        using var workspace = new ExportWorkspace();
        var firstPath = Path.Combine(workspace.Root, "first.xlsx");
        var secondPath = Path.Combine(workspace.Root, "second.xlsx");
        using (var publication = WorkbookBatchPublicationScope.Create(
                   [new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        firstPath,
                        WorkbookPublicationDisposition.CreateNew),
                    new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        secondPath,
                        WorkbookPublicationDisposition.CreateNew)]))
        {
            foreach (var candidate in publication.Candidates)
            {
                File.WriteAllText(candidate.TemporaryPath, "candidate");
            }

            var moves = 0;
            var failure = Assert.ThrowsExactly<WorkbookExportException>(() =>
                publication.Publish(
                    () => true,
                    moveFile: (source, destination) =>
                    {
                        moves++;
                        if (moves == 2)
                        {
                            File.WriteAllText(destination, "raced file");
                        }

                        File.Move(source, destination, overwrite: false);
                    }));
            Assert.AreEqual("export-target-exists", failure.Code);
        }

        Assert.IsFalse(File.Exists(firstPath));
        Assert.AreEqual("raced file", File.ReadAllText(secondPath));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public void AuthorizedOverwriteWaitsUntilEveryCandidateExists()
    {
        using var workspace = new ExportWorkspace();
        var existingPath = Path.Combine(workspace.Root, "existing.xlsx");
        var missingCandidatePath = Path.Combine(workspace.Root, "new.xlsx");
        File.WriteAllText(existingPath, "original");
        using (var publication = WorkbookBatchPublicationScope.Create(
                   [new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        existingPath,
                        WorkbookPublicationDisposition.OverwriteExisting),
                    new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        missingCandidatePath,
                        WorkbookPublicationDisposition.CreateNew)]))
        {
            File.WriteAllText(publication.Candidates[0].TemporaryPath, "replacement");

            var failure = Assert.ThrowsExactly<WorkbookExportException>(() =>
                publication.Publish(() => true));

            Assert.AreEqual("export-temporary-file-missing", failure.Code);
            Assert.AreEqual("original", File.ReadAllText(existingPath));
            Assert.IsFalse(File.Exists(missingCandidatePath));
        }

        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.backup"));
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public void PublicationFailureRestoresOriginalAndRemovesNewFiles()
    {
        using var workspace = new ExportWorkspace();
        var overwrittenPath = Path.Combine(workspace.Root, "overwritten.xlsx");
        var newPath = Path.Combine(workspace.Root, "new.xlsx");
        var racedPath = Path.Combine(workspace.Root, "raced.xlsx");
        File.WriteAllText(overwrittenPath, "original");
        using (var publication = WorkbookBatchPublicationScope.Create(
                   [new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        overwrittenPath,
                        WorkbookPublicationDisposition.OverwriteExisting),
                    new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        newPath,
                        WorkbookPublicationDisposition.CreateNew),
                    new WorkbookPublicationTarget(
                        WorkbookDefinitionId.CreateNew(),
                        racedPath,
                        WorkbookPublicationDisposition.CreateNew)]))
        {
            foreach (var candidate in publication.Candidates)
            {
                File.WriteAllText(candidate.TemporaryPath, "candidate");
            }

            var moves = 0;
            var failure = Assert.ThrowsExactly<WorkbookExportException>(() =>
                publication.Publish(
                    () => true,
                    moveFile: (source, destination) =>
                    {
                        moves++;
                        if (moves == 2)
                        {
                            File.WriteAllText(destination, "unrelated late collision");
                        }

                        File.Move(source, destination, overwrite: false);
                    }));

            Assert.AreEqual("export-target-exists", failure.Code);
        }

        Assert.AreEqual("original", File.ReadAllText(overwrittenPath));
        Assert.IsFalse(File.Exists(newPath));
        Assert.AreEqual("unrelated late collision", File.ReadAllText(racedPath));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.backup"));
    }

    [TestMethod]
    public void IrrecoverableReplacementRollbackIsReportedTruthfully()
    {
        using var workspace = new ExportWorkspace();
        var overwrittenPath = Path.Combine(workspace.Root, "overwritten.xlsx");
        var racedPath = Path.Combine(workspace.Root, "raced.xlsx");
        File.WriteAllText(overwrittenPath, "original");
        using var publication = WorkbookBatchPublicationScope.Create(
            [new WorkbookPublicationTarget(
                 WorkbookDefinitionId.CreateNew(),
                 overwrittenPath,
                 WorkbookPublicationDisposition.OverwriteExisting),
             new WorkbookPublicationTarget(
                 WorkbookDefinitionId.CreateNew(),
                 racedPath,
                 WorkbookPublicationDisposition.CreateNew)]);
        foreach (var candidate in publication.Candidates)
        {
            File.WriteAllText(candidate.TemporaryPath, "candidate");
        }

        var failure = Assert.ThrowsExactly<WorkbookExportException>(() => publication.Publish(
            () => true,
            moveFile: (source, destination) =>
            {
                File.WriteAllText(destination, "unrelated late collision");
                File.Move(source, destination, overwrite: false);
            },
            replaceFile: (source, destination, backup) =>
            {
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
                File.Delete(backup);
            }));

        Assert.AreEqual("export-batch-rollback-failed", failure.Code);
        Assert.AreEqual("candidate", File.ReadAllText(overwrittenPath));
        Assert.AreEqual("unrelated late collision", File.ReadAllText(racedPath));
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task BatchContractsRoundTripThroughTypedIpc()
    {
        using var workspace = new ExportWorkspace();
        var dataset = DatasetFixture.Create("Set", 1);
        dataset.AddRow(dataset.Cell("value"));
        var extraction = CreateExtraction(dataset);
        var configuration = CreateSeparateWorkbookConfiguration(dataset);
        var command = new RunWorkbookExportCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            CreatePublicationPlan(configuration, extraction, workspace.Root));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        stream.Position = 0;
        var roundTrip = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);

        Assert.IsInstanceOfType<RunWorkbookExportCommand>(roundTrip);
        Assert.AreEqual(
            workspace.Root,
            ((RunWorkbookExportCommand)roundTrip).PublicationPlan.OutputDirectory);
        var correlation = command.Correlation;
        var workbook = configuration.Workbooks.Single();
        var worksheet = workbook.Worksheets.Single();
        var response = new RunWorkbookExportResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Accepted,
            OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully("workbook-batch-publication")]),
            new WorkbookExportBatchSummary(
                correlation.OperationId,
                extraction.OperationId,
                workspace.Root,
                [new WorkbookExportFileSummary(
                    workbook.WorkbookDefinitionId,
                    Path.Combine(workspace.Root, workbook.FileName),
                    workbook.Order,
                    [new WorkbookExportWorksheetSummary(
                        worksheet.WorksheetDefinitionId,
                        worksheet.SourceSetId,
                        worksheet.Name,
                        worksheet.Order,
                        dataset.Rows.Count,
                        configuration.CreateIncludedOutputColumns(dataset.SourceSetId).Count)])]),
            null);
        await using var responseStream = new MemoryStream();
        await LengthPrefixedJsonMessageFramer.WriteAsync(responseStream, response);
        responseStream.Position = 0;
        var responseRoundTrip = await LengthPrefixedJsonMessageFramer.ReadAsync(responseStream);
        Assert.IsInstanceOfType<RunWorkbookExportResponse>(responseRoundTrip);
        Assert.HasCount(1, ((RunWorkbookExportResponse)responseRoundTrip).Batch!.Workbooks);
        using var host = ProcessingHostApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={workspace.Root}"]);
        Assert.IsNotNull(host.Services.GetRequiredService<ExcelWorkbookExportService>());
    }

    [TestMethod]
    public void FoundationEnforcesExcelCoordinatesAndCellTextLimits()
    {
        Assert.AreEqual(
            "XFD1048576",
            ExcelWorkbookExportFoundation.CreateCellReference(
                ExcelWorkbookLimits.MaximumColumns,
                ExcelWorkbookLimits.MaximumRows));
        Assert.AreEqual(
            "export-column-limit-exceeded",
            Assert.ThrowsExactly<WorkbookExportException>(() =>
                ExcelWorkbookExportFoundation.CreateCellReference(
                    ExcelWorkbookLimits.MaximumColumns + 1,
                    1)).Code);
        Assert.AreEqual(
            "export-row-limit-exceeded",
            Assert.ThrowsExactly<WorkbookExportException>(() =>
                ExcelWorkbookExportFoundation.CreateCellReference(
                    1,
                    ExcelWorkbookLimits.MaximumRows + 1)).Code);
        Assert.AreEqual(
            "invalid-export-text",
            Assert.ThrowsExactly<WorkbookExportException>(() =>
                ExcelWorkbookExportFoundation.ValidateCellText("invalid\u0001text", "value")).Code);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public void ServiceExposesNoLegacyFlatOrSingleWorkbookEntryPoint()
    {
        var methods = typeof(ExcelWorkbookExportService).GetMethods(
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.HasCount(1, methods);
        Assert.AreEqual(nameof(ExcelWorkbookExportService.ExportAsync), methods[0].Name);
        Assert.IsFalse(methods[0].GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(DatabaseMappingSnapshot)));
        Assert.IsFalse(methods[0].GetParameters().Any(parameter =>
            parameter.Name?.Contains("targetPath", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static ExtractionResultSummary CreateExtraction(params DatasetFixture[] fixtures)
    {
        var database = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            fixtures.Select(fixture => fixture.DatabaseSummary()).ToArray());
        return new ExtractionResultSummary(
            OperationId.CreateNew(),
            database,
            fixtures.Select(fixture => fixture.ExtractionSummary()).ToArray());
    }

    private static void AssertNoExportArtifacts(string directory)
    {
        Assert.IsEmpty(Directory.GetFiles(directory, "*.xlsx"));
        Assert.IsEmpty(Directory.GetFiles(directory, "*.incomplete"));
        Assert.IsEmpty(Directory.GetFiles(directory, "*.backup"));
    }

    private static WorkbookPublicationPlan CreatePublicationPlan(
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extraction,
        string outputDirectory,
        IReadOnlySet<WorkbookDefinitionId>? overwrite = null)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extraction);
        Assert.IsTrue(validation.IsValid);
        return new WorkbookPublicationPlan(
            outputDirectory,
            validation.RunnableWorkbooks.Select(workbook => new WorkbookPublicationTarget(
                workbook.Workbook.WorkbookDefinitionId,
                Path.Combine(outputDirectory, workbook.Workbook.FileName),
                overwrite?.Contains(workbook.Workbook.WorkbookDefinitionId) == true
                    ? WorkbookPublicationDisposition.OverwriteExisting
                    : WorkbookPublicationDisposition.CreateNew)).ToArray());
    }

    private static ExportConfigurationSnapshot CreateConfiguration(
        DatasetFixture first,
        DatasetFixture second,
        DatasetFixture third,
        DatasetFixture disabled)
    {
        var combinedId = WorkbookDefinitionId.CreateNew();
        var separateId = WorkbookDefinitionId.CreateNew();
        var disabledId = WorkbookDefinitionId.CreateNew();
        var firstSheetId = WorksheetDefinitionId.CreateNew();
        var secondSheetId = WorksheetDefinitionId.CreateNew();
        var thirdSheetId = WorksheetDefinitionId.CreateNew();
        var disabledSheetId = WorksheetDefinitionId.CreateNew();
        return new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                combinedId,
                "Combined.xlsx",
                1,
                [new WorksheetDefinition(secondSheetId, combinedId, second.SourceSetId, "Set B", 1),
                 new WorksheetDefinition(firstSheetId, combinedId, first.SourceSetId, "Set A", 2)]),
             new WorkbookDefinition(
                separateId,
                "Separate.xlsx",
                2,
                [new WorksheetDefinition(thirdSheetId, separateId, third.SourceSetId, "Set C", 1)]),
             new WorkbookDefinition(
                disabledId,
                "Disabled.xlsx",
                3,
                [new WorksheetDefinition(
                    disabledSheetId,
                    disabledId,
                    disabled.SourceSetId,
                    "Disabled",
                    1)])],
            [SourceConfiguration(first, firstSheetId, includeSourceId: true, includeMetadata: true),
             SourceConfiguration(second, secondSheetId),
             SourceConfiguration(third, thirdSheetId),
             SourceConfiguration(disabled, disabledSheetId, enabled: false)]);
    }

    private static ExportConfigurationSnapshot CreateSeparateWorkbookConfiguration(
        params DatasetFixture[] fixtures)
    {
        var workbooks = new List<WorkbookDefinition>();
        var sourceSets = new List<SourceSetExportConfiguration>();
        for (var index = 0; index < fixtures.Length; index++)
        {
            var workbookId = WorkbookDefinitionId.CreateNew();
            var worksheetId = WorksheetDefinitionId.CreateNew();
            workbooks.Add(new WorkbookDefinition(
                workbookId,
                $"{fixtures[index].DisplayName}.xlsx",
                index + 1,
                [new WorksheetDefinition(
                    worksheetId,
                    workbookId,
                    fixtures[index].SourceSetId,
                    fixtures[index].DisplayName,
                    1)]));
            sourceSets.Add(SourceConfiguration(fixtures[index], worksheetId));
        }

        return new ExportConfigurationSnapshot(workbooks, sourceSets);
    }

    private static SourceSetExportConfiguration SourceConfiguration(
        DatasetFixture fixture,
        WorksheetDefinitionId worksheetId,
        bool includeSourceId = false,
        bool includeMetadata = false,
        bool enabled = true) =>
        new(
            fixture.SourceSetId,
            enabled,
            worksheetId,
            fixture.Columns.Select((column, index) => new ExportFieldConfiguration(
                column.Identity,
                true,
                index == 0 ? "Shared" : column.EffectiveName,
                includeSourceId && index == 0)).ToArray(),
            includeMetadata
                ? [new ExportMetadataFieldConfiguration(
                       DatabaseMetadataField.SourceFile,
                       true,
                       "Source File"),
                   new ExportMetadataFieldConfiguration(
                       DatabaseMetadataField.RecordHierarchy,
                       true,
                       "Record Hierarchy")]
                : []);

    private static IReadOnlyList<string> SheetNames(SpreadsheetDocument document)
    {
        var sheets = document.WorkbookPart?.Workbook?.Sheets
            ?? throw new AssertFailedException("The workbook requires sheets.");
        return sheets.Elements<Sheet>().Select(sheet =>
            sheet.Name?.Value
            ?? throw new AssertFailedException("A worksheet requires a name.")).ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadCells(
        SpreadsheetDocument document,
        string sheetName)
    {
        var worksheet = GetWorksheet(document, sheetName);
        return worksheet.Descendants<Cell>().Where(cell => cell.InlineString?.Text is not null)
            .ToDictionary(
                cell => cell.CellReference!.Value!,
                cell => cell.InlineString!.Text!.Text);
    }

    private static Cell ReadCell(
        SpreadsheetDocument document,
        string sheetName,
        string cellReference) =>
        GetWorksheet(document, sheetName).Descendants<Cell>().Single(cell =>
            string.Equals(cell.CellReference?.Value, cellReference, StringComparison.Ordinal));

    private static Worksheet GetWorksheet(SpreadsheetDocument document, string sheetName)
    {
        var workbookPart = document.WorkbookPart
            ?? throw new AssertFailedException("The document requires a workbook part.");
        var sheets = workbookPart.Workbook?.Sheets
            ?? throw new AssertFailedException("The workbook requires sheets.");
        var sheet = sheets.Elements<Sheet>().Single(candidate =>
            string.Equals(candidate.Name?.Value, sheetName, StringComparison.Ordinal));
        var relationshipId = sheet.Id?.Value
            ?? throw new AssertFailedException("A worksheet requires a relationship ID.");
        return ((WorksheetPart)workbookPart.GetPartById(relationshipId)).Worksheet
            ?? throw new AssertFailedException("The worksheet part requires content.");
    }

    private sealed class ExportWorkspace : IDisposable
    {
        public ExportWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CIA.SPR141.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Source = new FakeExtractionExportRowSource();
            Service = new ExcelWorkbookExportService(
                Source,
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<ExcelWorkbookExportService>.Instance);
        }

        public string Root { get; }
        public FakeExtractionExportRowSource Source { get; }
        public ExcelWorkbookExportService Service { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeExtractionExportRowSource : IExtractionExportRowSource
    {
        private readonly Dictionary<SourceSetId, IReadOnlyList<ExtractionResultRow>> _rows = [];

        internal ExtractionResultSummary? Result { get; private set; }
        internal List<SourceSetId> StreamedSourceSets { get; } = [];
        internal Action? BeforeYield { get; set; }
        internal Action<int>? BeforePublishedResultRead { get; set; }
        internal int PublishedResultReadCount { get; private set; }

        internal void Set(ExtractionResultSummary result, params DatasetFixture[] fixtures)
        {
            Result = result;
            foreach (var fixture in fixtures)
            {
                _rows[fixture.SourceSetId] = fixture.Rows;
            }
        }

        internal void SetRows(
            SourceSetId sourceSetId,
            IReadOnlyList<ExtractionResultRow> rows) => _rows[sourceSetId] = rows;

        internal void ChangePublishedResult(ExtractionResultSummary result) => Result = result;

        public Task<ExtractionResultSummary?> ReadPublishedResultAsync(
            CancellationToken cancellationToken = default)
        {
            PublishedResultReadCount++;
            BeforePublishedResultRead?.Invoke(PublishedResultReadCount);
            return Task.FromResult(Result);
        }

        public async IAsyncEnumerable<ExtractionResultRow> StreamRowsAsync(
            OperationId extractionResultId,
            SourceSetId sourceSetId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamedSourceSets.Add(sourceSetId);
            foreach (var row in _rows[sourceSetId])
            {
                BeforeYield?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }
    }

    private sealed class DatasetFixture
    {
        private readonly IReadOnlyList<DatabaseFieldMapping> _mappings;
        private SourceId? _pendingSourceId;

        private DatasetFixture(
            SourceSetId sourceSetId,
            string displayName,
            int ordinal,
            IReadOnlyList<DatabaseColumnDefinition> columns,
            IReadOnlyList<DatabaseFieldMapping> mappings)
        {
            SourceSetId = sourceSetId;
            DisplayName = displayName;
            Ordinal = ordinal;
            Columns = columns;
            _mappings = mappings;
        }

        internal SourceSetId SourceSetId { get; }
        internal string DisplayName { get; }
        internal int Ordinal { get; }
        internal IReadOnlyList<DatabaseColumnDefinition> Columns { get; }
        internal List<ExtractionResultRow> Rows { get; } = [];

        internal static DatasetFixture Create(
            string displayName,
            int ordinal,
            bool twoColumns = false,
            DatabaseRepeatCoordinatePath? repeatCoordinates = null)
        {
            var sourceSetId = SourceSetId.CreateNew();
            var names = twoColumns ? new[] { "tag", "other" } : ["tag"];
            var mappings = names.Select(name =>
            {
                var identity = new DiscoveryInformationIdentity(
                    sourceSetId,
                    $"/root/record/{name}",
                    name,
                    SourceValueCandidateKind.Element,
                    $"/root/record/{name}");
                return new DatabaseFieldMapping(
                    DatabaseLogicalFieldIdentity.Create(identity),
                    name == "tag" ? "Shared" : "Other",
                    false,
                    [identity]);
            }).ToArray();
            var columns = mappings.Select((mapping, index) => new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    sourceSetId,
                    mapping.FieldKey,
                    repeatCoordinates ?? DatabaseRepeatCoordinatePath.Empty),
                mapping.EffectiveName,
                index + 1)).ToArray();
            return new DatasetFixture(sourceSetId, displayName, ordinal, columns, mappings);
        }

        internal ExtractionResultCell Cell(params string[] values) =>
            CellForColumn(0, values);

        internal ExtractionResultCell CellForColumn(int columnIndex, params string[] values)
        {
            var mapping = _mappings[columnIndex];
            var identity = mapping.DetailedIdentities.Single();
            var sourceId = _pendingSourceId ??= SourceId.CreateNew();
            var lineage = CreateLineage(identity, Rows.Count + 1, columnIndex + 1);
            return new ExtractionResultCell(
                Columns[columnIndex].Identity,
                Columns[columnIndex].EffectiveName,
                values.Distinct(StringComparer.Ordinal).Skip(1).Any(),
                values.Select((value, index) => new ExtractionResultValue(
                    index + 1,
                    value,
                    identity,
                    sourceId,
                    lineage,
                    Columns[columnIndex].Identity.RepeatCoordinates)).ToArray());
        }

        internal void AddRow(params ExtractionResultCell[] cells)
        {
            var ordinal = Rows.Count + 1;
            var sourceId = cells.SelectMany(cell => cell.Values).First().SourceId;
            var source = new DatabaseSourceMetadata(
                SourceSetId,
                DisplayName,
                sourceId,
                $"source-{ordinal}.xml",
                $"C:\\input\\{DisplayName}\\source-{ordinal}.xml",
                LoadedSourceKind.XmlFile,
                null,
                null,
                new DateTimeOffset(2026, 9, 18, 10, ordinal, 0, TimeSpan.Zero));
            Rows.Add(new ExtractionResultRow(
                ordinal,
                ordinal,
                ordinal,
                $"record-{ordinal}",
                source,
                cells));
            _pendingSourceId = null;
        }

        internal DatabaseDatasetSummary DatabaseSummary() => new(
            SourceSetId,
            DisplayName,
            Ordinal,
            RepeatedDataLayout.StructuralRows,
            Rows.Count,
            Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values).Count(),
            Columns,
            _mappings);

        internal ExtractionDatasetSummary ExtractionSummary() => new(
            SourceSetId,
            DisplayName,
            Ordinal,
            RepeatedDataLayout.StructuralRows,
            Rows.Count,
            Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Values).Count(),
            Columns);

        private static DatabaseLineageEvidence CreateLineage(
            DiscoveryInformationIdentity identity,
            int recordOrdinal,
            int fieldOrdinal) => new(
                identity.StructuralPath,
                recordOrdinal * 10L + fieldOrdinal,
                recordOrdinal,
                [1, recordOrdinal],
                recordOrdinal * 10L + fieldOrdinal,
                [new DatabaseSourceElementEvidence("root", string.Empty, "root", 1, 1),
                 new DatabaseSourceElementEvidence(
                    "record",
                    string.Empty,
                    "record",
                    recordOrdinal,
                    recordOrdinal),
                 new DatabaseSourceElementEvidence(
                    identity.InformationType,
                    string.Empty,
                    identity.InformationType,
                    recordOrdinal * 10L + fieldOrdinal,
                    fieldOrdinal)]);
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
