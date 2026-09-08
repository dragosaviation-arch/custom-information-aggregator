using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
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
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(["Tag_A", "Tag_B"]);
        configuration.SetSelection(["Tag_A", "Tag_B"], isSelected: true);
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
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.Discovery);
        var databaseCoordinator = new DatabaseBuildCoordinator(
            configuration,
            sourceSet,
            workflow,
            new SuccessfulDatabaseClient(),
            NullLogger<DatabaseBuildCoordinator>.Instance);
        using var viewModel = new DatabaseWorkspaceViewModel(
            configuration,
            workflow,
            databaseCoordinator,
            new StaticDatabaseReviewClient(sourceId));
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
            var excelExport = (Border)view.FindName("ExcelExportPanel");
            var exportButton = (Button)view.FindName("ExportToExcelButton");

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
            Assert.AreEqual(Visibility.Visible, excelExport.Visibility);
            Assert.IsFalse(exportButton.IsEnabled);
            Assert.IsNull(view.FindName("DatabaseFiltersButton"));

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
        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully(sources[0].SourceId.ToString())]),
                new DatabaseGenerationSummary(correlation.OperationId, mapping, 3),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class StaticDatabaseReviewClient(SourceId sourceId) : IDatabaseReviewClient
    {
        public Task<DatabaseReviewClientResult> ReadPageAsync(
            OperationId generationId,
            int startRowOrdinal,
            int rowCount,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseReviewClientResult(
                true,
                new DatabaseReviewPage(
                    generationId,
                    startRowOrdinal,
                    rowCount,
                    totalMappedValueCount: 3,
                    [
                        new DatabaseReviewColumn(
                            "Tag_A",
                            2,
                            [
                                new DatabaseReviewValue(1, "A1", "Tag_A", sourceId),
                                new DatabaseReviewValue(2, "A2", "Tag_A", sourceId)
                            ]),
                        new DatabaseReviewColumn(
                            "Tag_B",
                            1,
                            [new DatabaseReviewValue(1, "B1", "Tag_B", sourceId)])
                    ]),
                FailureCode: null,
                FailureDescription: null));
        }
    }
}
