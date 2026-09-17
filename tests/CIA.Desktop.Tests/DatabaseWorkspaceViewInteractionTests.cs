using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Extraction;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Views;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DatabaseWorkspaceViewInteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task RealViewUsesDynamicHeadersAndResponsiveLowerPanelTabs()
    {
        await WpfTestApplication.RunAsync(VerifyInteractionAsync).WaitAsync(TestTimeout);
    }

    private static async Task VerifyInteractionAsync()
    {
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var sourceId = SourceId.CreateNew();
        var source = new LoadedSourceContract(
            sourceId,
            Path.GetFullPath("database-review.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var sourceSet = new ActiveLoadedSourceSet();
        var loading = new SourceLoadingCoordinator(
            new StaticSourceIntakeClient(source),
            sourceSet,
            workflow);
        Assert.IsTrue((await loading.AddAsync(
            SourceSelectionKind.XmlFile,
            source.Path)).Accepted);
        var loadedSetId = sourceSet.SourceSets.Single().SourceSetId;
        var identities = new[]
        {
            new DiscoveryInformationIdentity(loadedSetId, "/root/Tag_A", "Tag_A"),
            new DiscoveryInformationIdentity(loadedSetId, "/root/Tag_B", "Tag_B")
        };
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(identities);
        configuration.SetSelection(identities, isSelected: true);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.Discovery);
        var databaseClient = new SuccessfulDatabaseClient();
        var databaseCoordinator = new DatabaseBuildCoordinator(
            configuration,
            sourceSet,
            workflow,
            databaseClient,
            NullLogger<DatabaseBuildCoordinator>.Instance);
        var extractionCoordinator = new ExtractionCoordinator(
            databaseCoordinator,
            workflow,
            new SuccessfulExtractionClient(),
            NullLogger<ExtractionCoordinator>.Instance);
        using var viewModel = new DatabaseWorkspaceViewModel(
            configuration,
            workflow,
            databaseCoordinator,
            new StaticDatabaseReviewClient(sourceId, databaseClient),
            extractionCoordinator);
        Assert.IsTrue((await databaseCoordinator.BuildAsync()).Accepted);
        var view = new DatabaseWorkspaceView
        {
            DataContext = viewModel
        };
        var window = new Window
        {
            Width = 1400,
            Height = 760,
            Content = view,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var dynamicHeaders = (ItemsControl)view.FindName("DynamicDatabaseHeaders");
            var reviewRows = (ItemsControl)view.FindName("DatabaseReviewRows");
            var lowerTabs = (Grid)view.FindName("LowerTabs");
            var exportFields = (Border)view.FindName("ExportFieldsPanel");
            var exportRows = (ItemsControl)view.FindName("ExportColumnRows");
            var excelExport = (Border)view.FindName("ExcelExportPanel");
            var exportButton = (Button)view.FindName("ExportToExcelButton");
            var prepareButton = (Button)view.FindName("PrepareForExportButton");
            var extractionState = (TextBlock)view.FindName("ExtractionReviewStateText");

            Assert.AreEqual(2, dynamicHeaders.Items.Count);
            Assert.AreEqual(2, reviewRows.Items.Count);
            var firstRow = (DatabaseReviewRowPresentation)reviewRows.Items[0];
            Assert.AreEqual("A1", firstRow.Cells[0].DisplayValue);
            StringAssert.Contains(firstRow.Cells[0].SourceContext!, sourceId.ToString());
            Assert.AreEqual("B1", firstRow.Cells[1].DisplayValue);
            Assert.AreEqual(string.Empty, ((DatabaseReviewRowPresentation)reviewRows.Items[1])
                .Cells[1].DisplayValue);
            Assert.AreEqual(Visibility.Collapsed, lowerTabs.Visibility);
            Assert.AreEqual(Visibility.Visible, exportFields.Visibility);
            Assert.AreEqual(2, exportRows.Items.Count);
            Assert.AreSame(viewModel.ExportColumns, exportRows.ItemsSource);
            Assert.AreEqual(Visibility.Visible, excelExport.Visibility);
            Assert.IsFalse(exportButton.IsEnabled);
            Assert.IsTrue(prepareButton.IsEnabled);
            Assert.AreEqual("Not prepared", extractionState.Text);
            Assert.IsNull(view.FindName("DatabaseFiltersButton"));
            Assert.IsNull(view.FindName("ExtractionReviewRows"));
            Assert.IsNull(view.FindName("GlobalSourceIdExportField"));

            Assert.IsNotNull(prepareButton.Command);
            Assert.AreEqual(2, reviewRows.Items.Count);
            Assert.IsFalse(exportButton.IsEnabled);

            window.Width = 1100;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.AreEqual(Visibility.Visible, lowerTabs.Visibility);
            Assert.AreEqual(Visibility.Visible, exportFields.Visibility);
            Assert.AreEqual(Visibility.Collapsed, excelExport.Visibility);

            var excelTab = (Button)view.FindName("ExcelExportTabButton");
            excelTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.AreEqual(Visibility.Collapsed, exportFields.Visibility);
            Assert.AreEqual(Visibility.Visible, excelExport.Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CompleteSuccessfullyAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind)
    {
        var begin = await workflow.BeginOperationAsync(operationKind);
        Assert.IsTrue(begin.Accepted);
        Assert.IsTrue(workflow.CompleteOperation(
            begin.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
    }

    private sealed class ReadyProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Current);
        }

        public Task<bool> RequestOperationCancellationAsync(
            CIA.Contracts.Operations.OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSourceIntakeClient(LoadedSourceContract source)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceIntakeClientResult(
                true,
                [source],
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract refreshedSource,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceRefreshClientResult(
                true,
                refreshedSource,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class SuccessfulDatabaseClient : IDatabaseClient
    {
        public DatabaseGenerationSummary? PublishedGeneration { get; private set; }

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var dataset = specification.Datasets.Single();
            var columns = dataset.Fields.Select((field, index) => new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    dataset.SourceSetId,
                    field.FieldKey,
                    DatabaseRepeatCoordinatePath.Empty),
                field.EffectiveName,
                index + 1)).ToArray();
            PublishedGeneration = new DatabaseGenerationSummary(
                correlation.OperationId,
                [new DatabaseDatasetSummary(
                    dataset.SourceSetId,
                    dataset.DisplayName,
                    dataset.Ordinal,
                    dataset.RepeatedDataLayout,
                    2,
                    3,
                    columns,
                    dataset.Fields)]);
            return Task.FromResult(new DatabaseClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully(
                        dataset.Sources[0].SourceId.ToString())]),
                PublishedGeneration,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class StaticDatabaseReviewClient(
        SourceId sourceId,
        SuccessfulDatabaseClient databaseClient) : IDatabaseReviewClient
    {
        public Task<DatabaseReviewClientResult> ReadPageAsync(
            DatabaseReviewQuery query,
            CancellationToken cancellationToken = default)
        {
            var generation = databaseClient.PublishedGeneration!;
            var dataset = generation.Datasets.Single();
            var source = new DatabaseSourceMetadata(
                dataset.SourceSetId,
                dataset.DisplayName,
                sourceId,
                "database-review.xml",
                Path.GetFullPath("database-review.xml"),
                LoadedSourceKind.XmlFile,
                null,
                null,
                null);
            DatabaseReviewCell Cell(int columnIndex, string value, int traversal)
            {
                var column = dataset.Columns[columnIndex];
                var identity = dataset.Mappings[columnIndex].DetailedIdentities[0];
                var lineage = new DatabaseLineageEvidence(
                    identity.StructuralPath,
                    traversal,
                    null,
                    [],
                    traversal,
                    [new DatabaseSourceElementEvidence(
                        identity.InformationType,
                        string.Empty,
                        identity.InformationType,
                        traversal,
                        1)]);
                return new DatabaseReviewCell(
                    column.Identity,
                    false,
                    [new DatabaseReviewValue(
                        value,
                        identity,
                        sourceId,
                        lineage,
                        column.Identity.RepeatCoordinates)]);
            }
            return Task.FromResult(new DatabaseReviewClientResult(
                true,
                new DatabaseReviewPage(
                    generation.OperationId,
                    dataset,
                    query.StartRowOrdinal,
                    query.RowCount,
                    2,
                    [
                        new DatabaseReviewRow(1, true, "record-1", source,
                            [Cell(0, "A1", 1), Cell(1, "B1", 2)]),
                        new DatabaseReviewRow(2, true, "record-2", source,
                            [Cell(0, "A2", 3)])
                    ]),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class SuccessfulExtractionClient : IExtractionClient
    {
        public Task<ExtractionClientResult> ExtractAsync(
            OperationCorrelation correlation,
            DatabaseGenerationSummary databaseGeneration,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExtractionClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully("extraction-publication")]),
                new CIA.Contracts.Extraction.ExtractionResultSummary(
                    correlation.OperationId,
                    databaseGeneration,
                    databaseGeneration.Datasets.Select(dataset =>
                        new CIA.Contracts.Extraction.ExtractionDatasetSummary(
                            dataset.SourceSetId,
                            dataset.DisplayName,
                            dataset.Ordinal,
                            dataset.RepeatedDataLayout,
                            dataset.RowCount,
                            dataset.ValueCount,
                            dataset.Columns)).ToArray()),
                FailureCode: null,
                FailureDescription: null));
        }
    }
}
