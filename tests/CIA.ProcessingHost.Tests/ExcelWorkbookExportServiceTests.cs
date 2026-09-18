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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ExcelWorkbookExportServiceTests
{
    [TestMethod]
    public async Task HierarchyExtractionIsRejectedWithoutFlatProjectionOrWorkbook()
    {
        using var workspace = new ExportWorkspace();
        var extraction = CreateHierarchyExtraction();
        var target = Path.Combine(workspace.Root, "guarded.xlsx");
        var configuration = CreateConfiguration(extraction);

        var result = await workspace.Service.ExportAsync(
            OperationCorrelation.CreateNew(),
            extraction,
            configuration,
            target);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, result.Completion.Outcome);
        Assert.AreEqual("set-aware-workbook-generation-not-supported", result.Failure?.Code);
        Assert.IsFalse(File.Exists(target));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task SetAwareWorkbookIpcIsTypedAndProductionServiceRetainsSpr141Guard()
    {
        using var workspace = new ExportWorkspace();
        var extraction = CreateHierarchyExtraction();
        var command = new RunWorkbookExportCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            OperationCorrelation.CreateNew(),
            extraction,
            CreateConfiguration(extraction),
            Path.Combine(workspace.Root, "guarded.xlsx"));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);

        Assert.IsGreaterThan(4, stream.Length);
        using var host = ProcessingHostApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={workspace.Root}"]);
        Assert.IsNotNull(host.Services.GetRequiredService<ExcelWorkbookExportService>());
    }

    [TestMethod]
    public async Task StreamingFoundationWritesLiteralWhitespaceSafeOpenXml()
    {
        using var workspace = new ExportWorkspace();
        var target = Path.Combine(workspace.Root, "foundation.xlsx");
        using (var publication = WorkbookPublicationScope.Create(target))
        {
            await ExcelWorkbookExportFoundation.WriteTemporaryWorkbookAsync(
                publication.TemporaryPath,
                [new ExcelWorksheetWritePlan(
                    "Results",
                    (writer, _) =>
                    {
                        writer.WriteStartElement(new Row { RowIndex = 1U });
                        ExcelWorkbookExportFoundation.WriteTextCell(writer, 1, 1, "Header");
                        writer.WriteEndElement();
                        writer.WriteStartElement(new Row { RowIndex = 2U });
                        ExcelWorkbookExportFoundation.WriteTextCell(
                            writer,
                            1,
                            2,
                            "  =SUM(A1:A2)  ");
                        ExcelWorkbookExportFoundation.WriteBlankCell(writer, 2, 2);
                        writer.WriteEndElement();
                        return ValueTask.CompletedTask;
                    })]);
            publication.Publish(() => true);
        }

        using var document = SpreadsheetDocument.Open(target, isEditable: false);
        var workbookPart = document.WorkbookPart!;
        var workbook = workbookPart.Workbook
            ?? throw new AssertFailedException("The workbook part requires a workbook.");
        var sheets = workbook.Sheets
            ?? throw new AssertFailedException("The workbook requires a Sheets collection.");
        var sheet = sheets.Elements<Sheet>().Single();
        Assert.AreEqual("Results", sheet.Name?.Value);
        var sheetId = sheet.Id?.Value
            ?? throw new AssertFailedException("The worksheet requires a relationship ID.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetId);
        var worksheet = worksheetPart.Worksheet
            ?? throw new AssertFailedException("The worksheet part requires a worksheet.");
        var cells = worksheet.Descendants<Cell>().ToArray();
        Assert.HasCount(3, cells);
        Assert.AreEqual(CellValues.InlineString, cells[1].DataType?.Value);
        Assert.IsNull(cells[1].CellFormula);
        Assert.AreEqual("  =SUM(A1:A2)  ", cells[1].InlineString?.Text?.Text);
        Assert.AreEqual(SpaceProcessingModeValues.Preserve, cells[1].InlineString?.Text?.Space?.Value);
        Assert.AreEqual("B2", cells[2].CellReference?.Value);
    }

    [TestMethod]
    public void FoundationEnforcesExcelCoordinatesAndCellTextLimits()
    {
        Assert.AreEqual(
            "XFD1048576",
            ExcelWorkbookExportFoundation.CreateCellReference(
                ExcelWorkbookLimits.MaximumColumns,
                ExcelWorkbookLimits.MaximumRows));
        var columnFailure = Assert.ThrowsExactly<WorkbookExportException>(() =>
            ExcelWorkbookExportFoundation.CreateCellReference(
                ExcelWorkbookLimits.MaximumColumns + 1,
                1));
        Assert.AreEqual("export-column-limit-exceeded", columnFailure.Code);
        var rowFailure = Assert.ThrowsExactly<WorkbookExportException>(() =>
            ExcelWorkbookExportFoundation.CreateCellReference(
                1,
                ExcelWorkbookLimits.MaximumRows + 1));
        Assert.AreEqual("export-row-limit-exceeded", rowFailure.Code);
        var textFailure = Assert.ThrowsExactly<WorkbookExportException>(() =>
            ExcelWorkbookExportFoundation.ValidateCellText(
                new string('x', ExcelWorkbookLimits.MaximumCellTextLength + 1),
                "value"));
        Assert.AreEqual("export-cell-limit-exceeded", textFailure.Code);
        var xmlFailure = Assert.ThrowsExactly<WorkbookExportException>(() =>
            ExcelWorkbookExportFoundation.ValidateCellText("invalid\u0001text", "value"));
        Assert.AreEqual("invalid-export-text", xmlFailure.Code);
    }

    [TestMethod]
    public void PublicationScopePublishesAtomicallyAndNeverOverwrites()
    {
        using var workspace = new ExportWorkspace();
        var publishedTarget = Path.Combine(workspace.Root, "published.xlsx");
        string publishedTemporaryPath;
        using (var publication = WorkbookPublicationScope.Create(publishedTarget))
        {
            publishedTemporaryPath = publication.TemporaryPath;
            File.WriteAllText(publication.TemporaryPath, "complete workbook");
            publication.Publish(() => true);
        }

        Assert.IsTrue(File.Exists(publishedTarget));
        Assert.IsFalse(File.Exists(publishedTemporaryPath));

        var racedTarget = Path.Combine(workspace.Root, "raced.xlsx");
        string racedTemporaryPath;
        using (var publication = WorkbookPublicationScope.Create(racedTarget))
        {
            racedTemporaryPath = publication.TemporaryPath;
            File.WriteAllText(publication.TemporaryPath, "candidate");
            File.WriteAllText(racedTarget, "existing");
            var failure = Assert.ThrowsExactly<WorkbookExportException>(() =>
                publication.Publish(() => true));
            Assert.AreEqual("export-target-exists", failure.Code);
            Assert.AreEqual("existing", File.ReadAllText(racedTarget));
        }

        Assert.IsFalse(File.Exists(racedTemporaryPath));
    }

    [TestMethod]
    public void PublicationScopeCleansTemporaryOutputOnFailureAndCancellation()
    {
        using var workspace = new ExportWorkspace();
        var failedTarget = Path.Combine(workspace.Root, "failed.xlsx");
        string failedTemporaryPath;
        using (var publication = WorkbookPublicationScope.Create(failedTarget))
        {
            failedTemporaryPath = publication.TemporaryPath;
            File.WriteAllText(publication.TemporaryPath, "incomplete");
        }

        Assert.IsFalse(File.Exists(failedTemporaryPath));
        Assert.IsFalse(File.Exists(failedTarget));

        var cancelledTarget = Path.Combine(workspace.Root, "cancelled.xlsx");
        string cancelledTemporaryPath;
        var boundaryEntered = false;
        using (var publication = WorkbookPublicationScope.Create(cancelledTarget))
        {
            cancelledTemporaryPath = publication.TemporaryPath;
            File.WriteAllText(publication.TemporaryPath, "incomplete");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => publication.Publish(
                () => boundaryEntered = true,
                cancellation.Token));
        }

        Assert.IsFalse(boundaryEntered);
        Assert.IsFalse(File.Exists(cancelledTemporaryPath));
        Assert.IsFalse(File.Exists(cancelledTarget));

        var boundaryTarget = Path.Combine(workspace.Root, "boundary-rejected.xlsx");
        string boundaryTemporaryPath;
        using (var publication = WorkbookPublicationScope.Create(boundaryTarget))
        {
            boundaryTemporaryPath = publication.TemporaryPath;
            File.WriteAllText(publication.TemporaryPath, "incomplete");
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                publication.Publish(() => false));
        }

        Assert.IsFalse(File.Exists(boundaryTemporaryPath));
        Assert.IsFalse(File.Exists(boundaryTarget));
    }

    [TestMethod]
    public void ServiceExposesNoLegacyFlatWorkbookEntryPoint()
    {
        var publicDeclaredMethods = typeof(ExcelWorkbookExportService)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.HasCount(1, publicDeclaredMethods);
        Assert.AreEqual(nameof(ExcelWorkbookExportService.ExportAsync), publicDeclaredMethods[0].Name);
        Assert.IsFalse(publicDeclaredMethods.Any(method =>
            method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(DatabaseMappingSnapshot))));
    }

    private static ExtractionResultSummary CreateHierarchyExtraction()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var identity = new DiscoveryInformationIdentity(
            sourceSetId,
            "/root/tag",
            "tag",
            SourceValueCandidateKind.Element,
            "/root/tag");
        var mapping = new DatabaseFieldMapping(
            DatabaseLogicalFieldIdentity.Create(identity),
            "Tag",
            false,
            [identity]);
        var column = new DatabaseColumnDefinition(
            new DatabaseColumnIdentity(
                sourceSetId,
                mapping.FieldKey,
                DatabaseRepeatCoordinatePath.Empty),
            "Tag",
            1);
        var dataset = new DatabaseDatasetSummary(
            sourceSetId,
            "Set 1",
            1,
            RepeatedDataLayout.StructuralRows,
            1,
            1,
            [column],
            [mapping]);
        var database = new DatabaseGenerationSummary(OperationId.CreateNew(), [dataset]);
        return new ExtractionResultSummary(
            OperationId.CreateNew(),
            database,
            [new ExtractionDatasetSummary(
                sourceSetId,
                "Set 1",
                1,
                RepeatedDataLayout.StructuralRows,
                1,
                1,
                [column])]);
    }

    private static ExportConfigurationSnapshot CreateConfiguration(
        ExtractionResultSummary extraction)
    {
        var dataset = extraction.Datasets.Single();
        var workbookId = WorkbookDefinitionId.CreateNew();
        var worksheetId = WorksheetDefinitionId.CreateNew();
        return new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                workbookId,
                "CIA Export.xlsx",
                1,
                [new WorksheetDefinition(
                    worksheetId,
                    workbookId,
                    dataset.SourceSetId,
                    dataset.DisplayName,
                    1)])],
            [new SourceSetExportConfiguration(
                dataset.SourceSetId,
                true,
                worksheetId,
                dataset.Columns.Select(column => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    false)).ToArray(),
                [])]);
    }

    private sealed class ExportWorkspace : IDisposable
    {
        public ExportWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CIA.SPR139.ExportGuard.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Service = new ExcelWorkbookExportService();
        }

        public string Root { get; }

        public ExcelWorkbookExportService Service { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

}
