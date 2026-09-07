using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CIA.Contracts.Operations;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Views;
using CIA.Desktop.Workflow;

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
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.Discovery);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
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
            var lowerTabs = (Grid)view.FindName("LowerTabs");
            var exportFields = (Border)view.FindName("ExportFieldsPanel");
            var excelExport = (Border)view.FindName("ExcelExportPanel");
            var exportButton = (Button)view.FindName("ExportToExcelButton");

            Assert.AreEqual(2, dynamicHeaders.Items.Count);
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
}
