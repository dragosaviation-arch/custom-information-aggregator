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
using CIA.ProcessingHost.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData")));
            Service = new ExcelWorkbookExportService();
        }

        public string Root { get; }

        public StructuredInformationRepository Repository { get; }

        public ExcelWorkbookExportService Service { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
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
