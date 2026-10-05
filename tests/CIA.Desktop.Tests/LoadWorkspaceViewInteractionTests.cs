using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Views;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LoadWorkspaceViewInteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task RelocatedMoveMenuMovesOnlyHighlightedRowsToNewAndExistingSets()
    {
        await WpfTestApplication.RunAsync(VerifyInteractionAsync).WaitAsync(TestTimeout);
    }

    private static async Task VerifyInteractionAsync()
    {
        var sources = new[]
        {
            CreateSource("first.xml"),
            CreateSource("second.xml"),
            CreateSource("third.xml")
        };
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var activeSources = new ActiveLoadedSourceSet();
        var loading = new SourceLoadingCoordinator(
            new StaticSourceIntakeClient(sources),
            activeSources,
            workflow);
        Assert.IsTrue((await loading.AddAsync(
            SourceSelectionKind.Folder,
            Path.GetFullPath("input"))).Accepted);
        var originalSet = activeSources.SourceSets.Single();
        var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(discovery.Accepted);
        Assert.IsTrue(workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        var shell = new MainWindowViewModel(new ApplicationSession());
        using var viewModel = new LoadWorkspaceViewModel(
            new EmptySourcePathPicker(),
            loading,
            activeSources,
            workflow,
            shell);
        var view = new LoadWorkspaceView { DataContext = viewModel };
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

            var rows = (ListBox)view.FindName("SourceRowsList");
            var activeSetSelector = (ComboBox)view.FindName("ActiveSourceSetSelector");
            var moveSelected = (Button)view.FindName("ReassignSetMenuButton");
            Assert.AreEqual(SelectionMode.Extended, rows.SelectionMode);

            rows.SelectedItems.Add(rows.Items[0]);
            rows.SelectedItems.Add(rows.Items[1]);
            Assert.HasCount(2, rows.SelectedItems);
            Assert.IsTrue(viewModel.HasHighlightedSources);
            Assert.IsTrue(moveSelected.IsEnabled);
            var moveSelectedPeer = new ButtonAutomationPeer(moveSelected);
            ((IInvokeProvider)moveSelectedPeer.GetPattern(PatternInterface.Invoke)!).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var moveToNewSet = moveSelected.ContextMenu!.Items
                .OfType<MenuItem>()
                .Single(item => string.Equals(
                    item.Header?.ToString(),
                    "Move to new Set",
                    StringComparison.Ordinal));
            moveToNewSet.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            if (activeSources.SourceSets.Count != 2)
            {
                Assert.Fail($"{viewModel.StatusTitle}: {viewModel.StatusDetail}");
            }

            Assert.HasCount(2, activeSources.SourceSets);
            var createdSet = activeSources.SourceSets.Single(set => set.SourceSetId != originalSet.SourceSetId);
            CollectionAssert.AreEquivalent(
                sources.Take(2).Select(source => source.SourceId).ToArray(),
                activeSources.Items.Where(item => item.SourceSetId == createdSet.SourceSetId)
                    .Select(item => item.SourceId).ToArray());
            Assert.AreEqual(originalSet.SourceSetId, activeSources.Items[2].SourceSetId);
            Assert.IsTrue(activeSources.Items.All(item => item.IsIncluded));
            Assert.AreSame(createdSet, viewModel.ActiveSourceSet);
            Assert.AreSame(createdSet, activeSetSelector.SelectedItem);
            Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);

            rows.SelectedItems.Clear();
            rows.SelectedItems.Add(rows.Items.Cast<LoadedSourceItem>().Single(item =>
                item.SourceId == sources[2].SourceId));
            ((IInvokeProvider)new ButtonAutomationPeer(moveSelected)
                .GetPattern(PatternInterface.Invoke)!).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var moveToExistingSet = moveSelected.ContextMenu!.Items
                .OfType<MenuItem>()
                .Single(item => string.Equals(
                    item.Header?.ToString(),
                    $"Move to {createdSet.Name}",
                    StringComparison.Ordinal));
            moveToExistingSet.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.IsTrue(activeSources.Items.All(item => item.SourceSetId == createdSet.SourceSetId));
            Assert.IsTrue(activeSources.Items.All(item => item.IsIncluded));
            Assert.AreEqual("Source membership updated", viewModel.StatusTitle);

            activeSetSelector.SelectedItem = originalSet;
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.AreSame(originalSet, viewModel.ActiveSourceSet);
            Assert.AreSame(originalSet, activeSetSelector.SelectedItem);
            Assert.AreEqual("Set 1", viewModel.ActiveSourceSetName);
            Assert.AreEqual(3, rows.Items.Count);
            Assert.AreEqual("Active Source Set changed", viewModel.StatusTitle);
        }
        finally
        {
            window.Close();
        }
    }

    private static LoadedSourceContract CreateSource(string fileName) =>
        new(
            SourceId.CreateNew(),
            Path.GetFullPath(fileName),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);

    private sealed class StaticSourceIntakeClient(
        IReadOnlyList<LoadedSourceContract> sources) : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceIntakeClientResult(
                true,
                sources,
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceRefreshClientResult(
                true,
                source,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class EmptySourcePathPicker : ISourcePathPicker
    {
        public string? PickXmlFile() => null;

        public string? PickFolder() => null;

        public string? PickArchive() => null;
    }

    private sealed class ReadyProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1,
            FailureCode: null);

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
