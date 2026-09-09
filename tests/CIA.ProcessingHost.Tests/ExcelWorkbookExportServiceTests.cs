using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Export;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ExcelWorkbookExportServiceTests
{
    [TestMethod]
    public async Task CurrentExtractionStreamsOneResultsWorksheetWithConfiguredColumns()
    {
        using var workspace = new ExportWorkspace();
        var alphaSource = workspace.CreateSource(
            "alpha.xml",
            "<root><alpha>  First  </alpha><alpha>A2</alpha><excluded>X</excluded></root>");
        var formulaSource = workspace.CreateSource(
            "formula.xml",
            "<root><beta>=SUM(A1:A2)</beta></root>");
        var extraction = await workspace.PrepareExtractionAsync(
            [alphaSource, formulaSource],
            new DatabaseMappingSnapshot(
            [
                new DatabaseColumnMapping("Alpha", ["alpha"]),
                new DatabaseColumnMapping("Formula", ["beta"]),
                new DatabaseColumnMapping("Excluded", ["excluded"])
            ]));
        var configuration = new ExportConfigurationSnapshot(
        [
            new ExportFieldConfiguration("Formula", true, "Calculated text", true),
            new ExportFieldConfiguration("Alpha", true, "Alpha text", false),
            new ExportFieldConfiguration("Excluded", false, "Excluded", false)
        ]);
        var target = workspace.CreateTargetPath("configured.xlsx");

        var result = await workspace.ExportAsync(extraction, configuration, target);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.AreEqual(extraction.OperationId, result.Workbook?.ExtractionResultId);
        Assert.AreEqual(3, result.Workbook?.ColumnCount);
        Assert.AreEqual(2, result.Workbook?.DataRowCount);
        Assert.IsTrue(File.Exists(target));

        using var workbook = SpreadsheetDocument.Open(target, isEditable: false);
        Assert.IsEmpty(new OpenXmlValidator().Validate(workbook));
        var workbookPart = workbook.WorkbookPart
            ?? throw new AssertFailedException("The workbook part is missing.");
        var workbookElement = workbookPart.Workbook
            ?? throw new AssertFailedException("The workbook element is missing.");
        var sheetsElement = workbookElement.Sheets
            ?? throw new AssertFailedException("The workbook sheet collection is missing.");
        var sheets = sheetsElement.Elements<Sheet>().ToArray();
        Assert.HasCount(1, sheets);
        Assert.AreEqual("Results", sheets[0].Name?.Value);
        var relationshipId = sheets[0].Id?.Value
            ?? throw new AssertFailedException("The Results sheet relationship is missing.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(relationshipId);
        var worksheet = worksheetPart.Worksheet
            ?? throw new AssertFailedException("The Results worksheet is missing.");
        var sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new AssertFailedException("The Results sheet data is missing.");
        var rows = sheetData.Elements<Row>().ToArray();
        Assert.HasCount(3, rows);
        CollectionAssert.AreEqual(
            new[] { "Calculated text", "Calculated text SourceId", "Alpha text" },
            ReadCells(rows[0]));
        CollectionAssert.AreEqual(
            new[] { "=SUM(A1:A2)", formulaSource.SourceId.ToString(), "  First  " },
            ReadCells(rows[1]));
        CollectionAssert.AreEqual(
            new[] { string.Empty, string.Empty, "A2" },
            ReadCells(rows[2]));
        Assert.IsNull(rows[1].Elements<Cell>().First().CellFormula);
        Assert.IsTrue(rows
            .SelectMany(row => row.Elements<Cell>())
            .Where(cell => cell.InnerText.Length > 0)
            .All(cell => cell.DataType?.Value == CellValues.InlineString));
        Assert.IsFalse(ReadCells(rows[0]).Any(header =>
            string.Equals(header, "SourceId", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExistingTargetAndExcelCellLimitFailWithoutPartialPublication()
    {
        using var workspace = new ExportWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>retained</tag></root>");
        var extraction = await workspace.PrepareExtractionAsync(
            [source],
            Mapping("Field", "tag"));
        var configuration = Configuration("Field");
        var existingTarget = workspace.CreateTargetPath("existing.xlsx");
        await File.WriteAllTextAsync(existingTarget, "existing-content");

        var existingResult = await workspace.ExportAsync(
            extraction,
            configuration,
            existingTarget);

        Assert.IsFalse(existingResult.Accepted);
        Assert.AreEqual("export-target-exists", existingResult.Failure?.Code);
        Assert.AreEqual("existing-content", await File.ReadAllTextAsync(existingTarget));

        var oversizedSource = workspace.CreateSource(
            "oversized.xml",
            $"<root><tag>{new string('X', ExcelWorkbookLimits.MaximumCellTextLength + 1)}</tag></root>");
        var oversizedExtraction = await workspace.PrepareExtractionAsync(
            [oversizedSource],
            Mapping("Field", "tag"));
        var oversizedTarget = workspace.CreateTargetPath("oversized.xlsx");
        var oversizedResult = await workspace.ExportAsync(
            oversizedExtraction,
            configuration,
            oversizedTarget);

        Assert.IsFalse(oversizedResult.Accepted);
        Assert.AreEqual("export-cell-limit-exceeded", oversizedResult.Failure?.Code);
        Assert.IsFalse(File.Exists(oversizedTarget));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task StaleAndCancelledExportsDoNotPublishAWorkbook()
    {
        using var workspace = new ExportWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>value</tag></root>");
        var database = await workspace.BuildDatabaseAsync([source], Mapping("Field", "tag"));
        var retainedExtraction = await workspace.PrepareExtractionAsync(database);
        _ = await workspace.PrepareExtractionAsync(database);
        var staleTarget = workspace.CreateTargetPath("stale.xlsx");

        var stale = await workspace.ExportAsync(
            retainedExtraction,
            Configuration("Field"),
            staleTarget);

        Assert.IsFalse(stale.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, stale.Completion.Outcome);
        Assert.IsFalse(File.Exists(staleTarget));

        var currentExtraction = (await workspace.Repository.ReadPublishedExtractionResultAsync())!;
        var cancelledTarget = workspace.CreateTargetPath("cancelled.xlsx");
        var cancelled = await workspace.ExportAsync(
            currentExtraction,
            Configuration("Field"),
            cancelledTarget,
            new CancellationToken(canceled: true));

        Assert.IsFalse(cancelled.Accepted);
        Assert.AreEqual(OperationOutcome.Cancelled, cancelled.Completion.Outcome);
        Assert.IsFalse(File.Exists(cancelledTarget));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task ExcessiveOutputColumnsAreRejectedBeforeWorkbookCreation()
    {
        using var workspace = new ExportWorkspace();
        var fieldCount = ExcelWorkbookLimits.MaximumColumns / 2 + 1;
        var mappings = Enumerable.Range(1, fieldCount)
            .Select(index => new DatabaseColumnMapping($"Field{index}", [$"tag{index}"]))
            .ToArray();
        var configurations = mappings
            .Select(mapping => new ExportFieldConfiguration(
                mapping.DatabaseTagName,
                true,
                mapping.DatabaseTagName,
                true))
            .ToArray();
        var database = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            new DatabaseMappingSnapshot(mappings),
            valueCount: 1);
        var extraction = new ExtractionResultSummary(OperationId.CreateNew(), database);
        var target = workspace.CreateTargetPath("too-wide.xlsx");

        var result = await workspace.ExportAsync(
            extraction,
            new ExportConfigurationSnapshot(configurations),
            target);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("export-column-limit-exceeded", result.Failure?.Code);
        Assert.IsFalse(File.Exists(target));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task ExportContractsRoundTripAndProductionCompositionProvidesExporter()
    {
        using var workspace = new ExportWorkspace();
        var source = workspace.CreateSource("source.xml", "<root><tag>value</tag></root>");
        var extraction = await workspace.PrepareExtractionAsync(
            [source],
            Mapping("Field", "tag"));
        var correlation = OperationCorrelation.CreateNew();
        var command = new RunWorkbookExportCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            correlation,
            extraction,
            Configuration("Field"),
            workspace.CreateTargetPath("round-trip.xlsx"));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        stream.Position = 0;
        var roundTrip = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);

        Assert.IsInstanceOfType<RunWorkbookExportCommand>(roundTrip);
        var restored = (RunWorkbookExportCommand)roundTrip;
        Assert.AreEqual(extraction.OperationId, restored.ExtractionResult.OperationId);
        Assert.AreEqual("Field", restored.Configuration.Fields.Single().DatabaseFieldIdentity);

        using var host = Hosting.ProcessingHostApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={workspace.Root}"]);
        Assert.IsNotNull(host.Services.GetRequiredService<ExcelWorkbookExportService>());
    }

    private static string[] ReadCells(Row row)
    {
        return row.Elements<Cell>().Select(cell => cell.InnerText).ToArray();
    }

    private static DatabaseMappingSnapshot Mapping(string databaseField, string informationType)
    {
        return new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping(databaseField, [informationType])
        ]);
    }

    private static ExportConfigurationSnapshot Configuration(string databaseField)
    {
        return new ExportConfigurationSnapshot(
        [
            new ExportFieldConfiguration(databaseField, true, databaseField, false)
        ]);
    }

    private sealed class RecordingHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }

    private sealed class ExportWorkspace : IDisposable
    {
        public ExportWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "CIA.SPR87.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData")));
            Cancellation = new CooperativeOperationCancellation(new RecordingHistory());
            ExtractionService = new DatabaseExtractionService(
                Repository,
                Cancellation,
                NullLogger<DatabaseExtractionService>.Instance);
            ExportService = new ExcelWorkbookExportService(
                Repository,
                Cancellation,
                NullLogger<ExcelWorkbookExportService>.Instance);
        }

        public string Root { get; }

        public StructuredInformationRepository Repository { get; }

        private CooperativeOperationCancellation Cancellation { get; }

        private DatabaseExtractionService ExtractionService { get; }

        private ExcelWorkbookExportService ExportService { get; }

        public LoadedSourceContract CreateSource(string fileName, string xml)
        {
            var path = Path.Combine(Root, fileName);
            File.WriteAllText(path, xml);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                path,
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public string CreateTargetPath(string fileName)
        {
            return Path.Combine(Root, fileName);
        }

        public async Task<ExtractionResultSummary> PrepareExtractionAsync(
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping)
        {
            var database = await BuildDatabaseAsync(sources, mapping);
            return await PrepareExtractionAsync(database);
        }

        public async Task<DatabaseGenerationSummary> BuildDatabaseAsync(
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping)
        {
            var reader = new SourceValueBatchReader(
                [],
                new GenericXmlElementValueSourceAdapter(),
                NullLogger<SourceValueBatchReader>.Instance);
            var service = new DatabaseGenerationService(
                Repository,
                reader,
                Cancellation,
                NullLogger<DatabaseGenerationService>.Instance);
            var result = await service.BuildAsync(
                OperationCorrelation.CreateNew(),
                sources,
                mapping);
            Assert.IsTrue(result.Accepted);
            return result.PublishedGeneration!;
        }

        public async Task<ExtractionResultSummary> PrepareExtractionAsync(
            DatabaseGenerationSummary database)
        {
            var result = await ExtractionService.ExtractAsync(
                OperationCorrelation.CreateNew(),
                database);
            Assert.IsTrue(result.Accepted);
            return result.PublishedResult!;
        }

        public Task<WorkbookExportHostResult> ExportAsync(
            ExtractionResultSummary extraction,
            ExportConfigurationSnapshot configuration,
            string targetPath,
            CancellationToken cancellationToken = default)
        {
            return ExportService.ExportAsync(
                OperationCorrelation.CreateNew(),
                extraction,
                configuration,
                targetPath,
                cancellationToken);
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
}
