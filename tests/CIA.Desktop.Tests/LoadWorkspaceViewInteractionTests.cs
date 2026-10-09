using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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

    [TestMethod]
    public async Task NativeExtendedSelectionKeepsDetailsAndCheckboxActionsIndependent()
    {
        await WpfTestApplication.RunAsync(VerifyExtendedSelectionAsync).WaitAsync(TestTimeout);
    }

    private static async Task VerifyExtendedSelectionAsync()
    {
        var sources = Enumerable.Range(1, 7)
            .Select(index => CreateSource($"source-{index}.xml"))
            .ToArray();
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
            Path.GetFullPath("extended-selection"))).Accepted);
        var originalSet = activeSources.SourceSets.Single();
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
            rows.UpdateLayout();

            GetSelectionProvider(rows, 0).Select();
            GetSelectionProvider(rows, 2).AddToSelection();
            GetSelectionProvider(rows, 6).AddToSelection();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources[0], sources[2], sources[6]);

            GetSelectionProvider(rows, 2).RemoveFromSelection();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources[0], sources[6]);

            GetSelectionProvider(rows, 1).Select();
            foreach (var index in Enumerable.Range(2, 4))
            {
                GetSelectionProvider(rows, index).AddToSelection();
            }
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources.Skip(1).Take(5).ToArray());

            var fourthRow = GetRow(rows, 3);
            RaiseRowPreviewClick(fourthRow);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources.Skip(1).Take(5).ToArray());
            Assert.AreEqual(sources[3].SourceId, viewModel.SelectedSource!.SourceId);

            GetSelectionProvider(rows, 0).Select();
            GetSelectionProvider(rows, 3).AddToSelection();
            GetSelectionProvider(rows, 6).AddToSelection();
            var firstCheckbox = FindVisualChild<CheckBox>(GetRow(rows, 0))!;
            RaiseCheckboxClick(firstCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources[0], sources[3], sources[6]);
            Assert.IsTrue(new[] { 0, 3, 6 }.All(index =>
                !activeSources.Items.Single(item => item.SourceId == sources[index].SourceId).IsIncluded));
            Assert.AreEqual(sources[0].SourceId, viewModel.SelectedSource!.SourceId);

            RaiseCheckboxClick(firstCheckbox);
            var middleCheckbox = FindVisualChild<CheckBox>(GetRow(rows, 3))!;
            RaiseCheckboxClick(middleCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources[0], sources[3], sources[6]);
            Assert.IsTrue(new[] { 0, 3, 6 }.All(index =>
                !activeSources.Items.Single(item => item.SourceId == sources[index].SourceId).IsIncluded));
            Assert.AreEqual(sources[3].SourceId, viewModel.SelectedSource!.SourceId);

            var outsideCheckbox = FindVisualChild<CheckBox>(GetRow(rows, 1))!;
            RaiseCheckboxClick(outsideCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            AssertSelected(rows, sources[1]);
            Assert.IsFalse(activeSources.Items.Single(item =>
                item.SourceId == sources[1].SourceId).IsIncluded);

            var memberships = activeSources.Items.ToDictionary(
                source => source.SourceId,
                source => source.SourceSetId);
            var createSet = (Button)view.FindName("CreateSourceSetButton");
            ((IInvokeProvider)new ButtonAutomationPeer(createSet)
                .GetPattern(PatternInterface.Invoke)!).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.HasCount(2, activeSources.SourceSets);
            Assert.AreNotEqual(
                originalSet.SourceSetId,
                activeSources.ActiveSourceSet!.SourceSetId);
            CollectionAssert.AreEquivalent(
                memberships.ToArray(),
                activeSources.Items.Select(source => new KeyValuePair<SourceId, SourceSetId>(
                    source.SourceId,
                    source.SourceSetId)).ToArray());
            AssertSelected(rows, sources[1]);
        }
        finally
        {
            window.Close();
        }
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

            var firstRow = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(0);
            var firstCheckbox = FindVisualChild<CheckBox>(firstRow)!;
            RaiseCheckboxClick(firstCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.IsFalse(activeSources.Items.Single(item =>
                item.SourceId == sources[0].SourceId).IsIncluded);
            Assert.IsFalse(activeSources.Items.Single(item =>
                item.SourceId == sources[1].SourceId).IsIncluded);
            Assert.IsTrue(activeSources.Items.Single(item =>
                item.SourceId == sources[2].SourceId).IsIncluded);
            Assert.HasCount(2, rows.SelectedItems);
            Assert.AreEqual(sources[0].SourceId, viewModel.SelectedSource!.SourceId);

            RaiseCheckboxClick(firstCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.IsTrue(activeSources.Items.Single(item =>
                item.SourceId == sources[0].SourceId).IsIncluded);
            Assert.IsTrue(activeSources.Items.Single(item =>
                item.SourceId == sources[1].SourceId).IsIncluded);

            rows.ScrollIntoView(rows.Items[2]);
            rows.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var thirdRow = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(2);
            var thirdCheckbox = FindVisualChild<CheckBox>(thirdRow)!;
            RaiseCheckboxClick(thirdCheckbox);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.HasCount(1, rows.SelectedItems);
            Assert.AreEqual(
                sources[2].SourceId,
                ((LoadedSourceItem)rows.SelectedItem).SourceId);
            Assert.AreEqual(sources[2].SourceId, viewModel.SelectedSource!.SourceId);
            Assert.IsTrue(activeSources.Items.Single(item =>
                item.SourceId == sources[0].SourceId).IsIncluded);
            Assert.IsTrue(activeSources.Items.Single(item =>
                item.SourceId == sources[1].SourceId).IsIncluded);
            Assert.IsFalse(activeSources.Items.Single(item =>
                item.SourceId == sources[2].SourceId).IsIncluded);

            RaiseCheckboxClick(thirdCheckbox);
            rows.SelectedItems.Clear();
            rows.SelectedItems.Add(rows.Items[0]);
            rows.SelectedItems.Add(rows.Items[1]);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.IsTrue(activeSources.Items.All(source => source.IsIncluded));

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

    private static void RaiseCheckboxClick(CheckBox checkbox)
    {
        checkbox.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = checkbox
        });
        Assert.IsNotNull(checkbox.Command);
        checkbox.Command.Execute(checkbox.CommandParameter);
    }

    private static void RaiseRowPreviewClick(ListBoxItem row)
    {
        row.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
            Source = row
        });
    }

    private static ISelectionItemProvider GetSelectionProvider(ListBox rows, int index)
    {
        GetRow(rows, index);
        var selectorPeer = UIElementAutomationPeer.CreatePeerForElement(rows)
            as SelectorAutomationPeer ?? new ListBoxAutomationPeer(rows);
        var peer = new ListBoxItemAutomationPeer(rows.Items[index], selectorPeer);
        return (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!;
    }

    private static ListBoxItem GetRow(ListBox rows, int index)
    {
        rows.ScrollIntoView(rows.Items[index]);
        rows.UpdateLayout();
        return (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(index);
    }

    private static void AssertSelected(
        ListBox rows,
        params LoadedSourceContract[] expected)
    {
        CollectionAssert.AreEquivalent(
            expected.Select(source => source.SourceId).ToArray(),
            rows.SelectedItems.Cast<LoadedSourceItem>()
                .Select(source => source.SourceId).ToArray());
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
