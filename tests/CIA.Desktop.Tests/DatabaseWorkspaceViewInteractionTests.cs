using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Extraction;
using CIA.Desktop.Export;
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
        var settingsRoot = Directory.CreateTempSubdirectory("CIA.SPR191.DatabaseColumns.");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(settingsRoot.FullName));
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
            extractionCoordinator,
            settingsService: settings);
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
            var metadataHeaders = (ItemsControl)view.FindName("DatabaseMetadataHeaders");
            var reviewRows = (ListBox)view.FindName("DatabaseReviewRows");
            var sourceSetTabs = (ListBox)view.FindName("DatabaseSourceSetTabs");
            var columnsButton = (Button)view.FindName("ColumnsButton");
            var lowerTabs = (Grid)view.FindName("LowerTabs");
            var exportFields = (Border)view.FindName("ExportFieldsPanel");
            var exportRows = (ItemsControl)view.FindName("ExportColumnRows");
            var exportEnabled = (CheckBox)view.FindName("ExportEnabledToggle");
            var workbookSelector = (ComboBox)view.FindName("WorkbookDefinitionSelector");
            var createWorkbook = (Button)view.FindName("CreateWorkbookDefinitionButton");
            var worksheetName = (TextBox)view.FindName("WorksheetNameInput");
            var metadataRows = (ItemsControl)view.FindName("ExportMetadataRows");
            var excelExport = (Border)view.FindName("ExcelExportPanel");
            var exportButton = (Button)view.FindName("ExportToExcelButton");
            var browseOutputFolder = (Button)view.FindName("BrowseOutputFolderButton");
            var workbookExportStatus = (TextBlock)view.FindName("WorkbookExportStatusText");
            var extractionState = (TextBlock)view.FindName("ExtractionReviewStateText");
            var columnSizeFeedback = (Border)view.FindName("DatabaseColumnSizeFeedback");
            var columnSizeFeedbackText = (TextBlock)view.FindName("DatabaseColumnSizeFeedbackText");

            Assert.AreEqual(2, dynamicHeaders.Items.Count);
            Assert.AreEqual(viewModel.VisibleMetadataColumns.Count, metadataHeaders.Items.Count);
            Assert.AreEqual(0, metadataHeaders.Items.Count);
            Assert.IsTrue(viewModel.ColumnChoices.Where(choice => choice.IsGenerated)
                .All(choice => choice.IsIncluded));
            Assert.IsTrue(viewModel.ColumnChoices.Where(choice => !choice.IsGenerated)
                .All(choice => !choice.IsIncluded));
            Assert.HasCount(1, sourceSetTabs.Items);
            Assert.AreSame(viewModel.SelectedDataset, sourceSetTabs.SelectedItem);
            Assert.IsNotNull(columnsButton);
            Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(reviewRows));
            Assert.AreEqual(VirtualizationMode.Recycling,
                VirtualizingPanel.GetVirtualizationMode(reviewRows));
            Assert.AreEqual(250, reviewRows.Items.Count);
            var firstRow = (DatabaseReviewRowPresentation)reviewRows.Items[0];
            Assert.AreEqual("A1", firstRow.Cells[0].DisplayValue);
            StringAssert.Contains(firstRow.Cells[0].SourceContext!, sourceId.ToString());
            Assert.AreEqual("B1", firstRow.Cells[1].DisplayValue);
            Assert.AreEqual(string.Empty, ((DatabaseReviewRowPresentation)reviewRows.Items[1])
                .Cells[1].DisplayValue);
            Assert.IsTrue(((DatabaseReviewRowPresentation)reviewRows.Items[249]).IsLoading);
            reviewRows.ScrollIntoView(reviewRows.Items[249]);
            await WaitForAsync(() =>
                !((DatabaseReviewRowPresentation)reviewRows.Items[249]).IsLoading);
            Assert.AreEqual(
                "A250",
                ((DatabaseReviewRowPresentation)reviewRows.Items[249]).Cells[0].DisplayValue);
            Assert.IsLessThanOrEqualTo(4, viewModel.CachedReviewPageCount);
            Assert.AreEqual(Visibility.Collapsed, lowerTabs.Visibility);
            Assert.AreEqual(Visibility.Visible, exportFields.Visibility);
            Assert.AreEqual(2, exportRows.Items.Count);
            Assert.AreSame(viewModel.ExportColumns, exportRows.ItemsSource);
            Assert.IsTrue(exportEnabled.IsChecked.GetValueOrDefault());
            Assert.AreEqual(1, workbookSelector.Items.Count);
            Assert.IsTrue(createWorkbook.IsEnabled);
            Assert.AreEqual(viewModel.SelectedWorksheetName, worksheetName.Text);
            Assert.AreEqual(Enum.GetValues<DatabaseMetadataField>().Length, metadataRows.Items.Count);
            Assert.IsTrue(viewModel.IsExportConfigurationValid);
            Assert.AreEqual(Visibility.Visible, excelExport.Visibility);
            Assert.IsFalse(exportButton.IsEnabled);

            foreach (var metadataChoice in viewModel.ColumnChoices
                         .Where(choice => !choice.IsGenerated)
                         .Take(2)
                         .ToArray())
            {
                metadataChoice.IsIncluded = true;
            }
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.AreEqual(2, metadataHeaders.Items.Count);
            Assert.AreEqual(2, viewModel.ExportMetadataFields.Count(field => field.IsExported));
            Assert.IsNotNull(exportButton.Command);
            Assert.IsNotNull(browseOutputFolder.Command);
            Assert.AreEqual(viewModel.WorkbookExportStatusText, workbookExportStatus.Text);
            Assert.IsNull(view.FindName("PrepareForExportButton"));
            Assert.AreEqual("Not prepared", extractionState.Text);
            Assert.IsNull(view.FindName("DatabaseFiltersButton"));
            Assert.IsNull(view.FindName("ExtractionReviewRows"));
            Assert.IsNull(view.FindName("GlobalSourceIdExportField"));
            Assert.IsNull(view.FindName("SingleSheetMode"));
            Assert.IsNull(view.FindName("MultipleSheetsMode"));
            Assert.AreEqual(Visibility.Collapsed, columnSizeFeedback.Visibility);
            dynamicHeaders.UpdateLayout();
            var firstHeader = (DependencyObject)dynamicHeaders.ItemContainerGenerator
                .ContainerFromIndex(0);
            var columnDivider = FindVisualChild<Thumb>(firstHeader);
            Assert.IsNotNull(columnDivider);
            var lastHeader = (DependencyObject)dynamicHeaders.ItemContainerGenerator
                .ContainerFromIndex(dynamicHeaders.Items.Count - 1);
            var lastColumnDivider = FindVisualChild<Thumb>(lastHeader);
            Assert.IsNotNull(lastColumnDivider);
            Assert.AreEqual(Visibility.Visible, lastColumnDivider.Visibility);
            var firstWidth = viewModel.VisibleColumns[0].Width;
            var secondWidth = viewModel.VisibleColumns[1].Width;

            columnDivider.RaiseEvent(new DragStartedEventArgs(0, 0)
            {
                RoutedEvent = Thumb.DragStartedEvent
            });
            columnDivider.RaiseEvent(new DragDeltaEventArgs(24, 0)
            {
                RoutedEvent = Thumb.DragDeltaEvent
            });
            Assert.AreEqual(firstWidth + 24, viewModel.VisibleColumns[0].Width);
            Assert.AreEqual(secondWidth - 24, viewModel.VisibleColumns[1].Width);
            Assert.AreEqual(Visibility.Visible, columnSizeFeedback.Visibility);
            StringAssert.Contains(columnSizeFeedbackText.Text, " / ");
            columnDivider.RaiseEvent(new DragCompletedEventArgs(24, 0, false)
            {
                RoutedEvent = Thumb.DragCompletedEvent
            });
            Assert.AreEqual(Visibility.Collapsed, columnSizeFeedback.Visibility);
            Assert.IsTrue(settings.Current.ColumnWidths.Values.Contains(firstWidth + 24));
            Assert.IsTrue(settings.Current.ColumnWidths.Values.Contains(secondWidth - 24));

            metadataHeaders.UpdateLayout();
            var firstMetadataHeader = (DependencyObject)metadataHeaders.ItemContainerGenerator
                .ContainerFromIndex(0);
            var metadataDivider = FindVisualChild<Thumb>(firstMetadataHeader);
            Assert.IsNotNull(metadataDivider);
            Assert.AreEqual(Visibility.Visible, metadataDivider.Visibility);
            var firstMetadataWidth = viewModel.VisibleMetadataColumns[0].Width;
            var nextVisibleWidth = viewModel.VisibleMetadataColumns.Count > 1
                ? viewModel.VisibleMetadataColumns[1].Width
                : viewModel.VisibleColumns[0].Width;
            metadataDivider.RaiseEvent(new DragStartedEventArgs(0, 0)
            {
                RoutedEvent = Thumb.DragStartedEvent
            });
            metadataDivider.RaiseEvent(new DragDeltaEventArgs(50, 0)
            {
                RoutedEvent = Thumb.DragDeltaEvent
            });
            Assert.AreEqual(firstMetadataWidth + 50, viewModel.VisibleMetadataColumns[0].Width);
            Assert.AreEqual(
                nextVisibleWidth - 50,
                viewModel.VisibleMetadataColumns.Count > 1
                    ? viewModel.VisibleMetadataColumns[1].Width
                    : viewModel.VisibleColumns[0].Width);
            Assert.AreEqual(Visibility.Visible, columnSizeFeedback.Visibility);
            metadataDivider.RaiseEvent(new DragCompletedEventArgs(50, 0, false)
            {
                RoutedEvent = Thumb.DragCompletedEvent
            });
            Assert.AreEqual(Visibility.Collapsed, columnSizeFeedback.Visibility);

            var restoredCoordinator = new DatabaseBuildCoordinator(
                configuration,
                sourceSet,
                workflow,
                new SuccessfulDatabaseClient(),
                NullLogger<DatabaseBuildCoordinator>.Instance);
            using var restoredViewModel = new DatabaseWorkspaceViewModel(
                configuration,
                workflow,
                restoredCoordinator,
                settingsService: settings);
            Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
            Assert.IsTrue((await restoredCoordinator.BuildAsync()).Accepted);
            CollectionAssert.AreEqual(
                viewModel.VisibleColumns.Select(column => column.Width).ToArray(),
                restoredViewModel.VisibleColumns.Select(column => column.Width).ToArray());
            CollectionAssert.AreEqual(
                viewModel.MetadataFields.Select(column => column.Width).ToArray(),
                restoredViewModel.MetadataFields.Select(column => column.Width).ToArray());

            var collisionId = WorkbookDefinitionId.CreateNew();
            var collisionDialog = new WorkbookCollisionDialog(
                new WorkbookCollisionResolutionRequest(
                    Path.GetFullPath("."),
                    [new WorkbookCollisionTarget(
                        collisionId,
                        "existing.xlsx",
                        Path.GetFullPath("existing.xlsx"),
                        Exists: true)],
                    []));
            Assert.HasCount(1, collisionDialog.Rows);
            Assert.AreEqual(
                WorkbookCollisionAction.Cancel,
                collisionDialog.Rows[0].SelectedOption.Action);
            collisionDialog.Close();

            Assert.AreEqual(250, reviewRows.Items.Count);
            Assert.IsFalse(exportButton.IsEnabled);

            window.Width = 1100;
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.AreEqual(Visibility.Visible, lowerTabs.Visibility);
            Assert.AreEqual(Visibility.Visible, exportFields.Visibility);
            Assert.AreEqual(Visibility.Collapsed, excelExport.Visibility);
            Assert.AreEqual(Visibility.Collapsed, columnSizeFeedback.Visibility);

            var excelTab = (Button)view.FindName("ExcelExportTabButton");
            excelTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.AreEqual(Visibility.Collapsed, exportFields.Visibility);
            Assert.AreEqual(Visibility.Visible, excelExport.Visibility);
        }
        finally
        {
            window.Close();
            settingsRoot.Delete(recursive: true);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(15, timeout.Token);
        }
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
                    250,
                    499,
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
            var lastOrdinal = Math.Min(250, query.StartRowOrdinal + query.RowCount - 1);
            var rows = Enumerable.Range(
                    query.StartRowOrdinal,
                    lastOrdinal - query.StartRowOrdinal + 1)
                .Select(ordinal => new DatabaseReviewRow(
                    ordinal,
                    true,
                    $"record-{ordinal}",
                    source,
                    ordinal == 2
                        ? [Cell(0, "A2", 3)]
                        :
                        [
                            Cell(0, $"A{ordinal}", (ordinal * 2) - 1),
                            Cell(1, $"B{ordinal}", ordinal * 2)
                        ]))
                .ToArray();
            return Task.FromResult(new DatabaseReviewClientResult(
                true,
                new DatabaseReviewPage(
                    generation.OperationId,
                    dataset,
                    query.StartRowOrdinal,
                    query.RowCount,
                    250,
                    rows),
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
