using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Core.Runtime;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Views;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ColumnResizeInteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task LoadAndDiscoveryTablesResizeAdjustableColumnsAndScrollFixedFinalColumnsInAlignment()
    {
        await WpfTestApplication.RunAsync(VerifyInteractionAsync).WaitAsync(TestTimeout);
    }

    private static async Task VerifyInteractionAsync()
    {
        var root = Directory.CreateTempSubdirectory("CIA.SPR191.ColumnWidths.");
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var sources = new ActiveLoadedSourceSet();
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.Combine(root.FullName, "source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var loading = new SourceLoadingCoordinator(
            new StaticSourceIntakeClient(source),
            sources,
            workflow);
        var loadResult = await loading.AddAsync(SourceSelectionKind.XmlFile, source.Path);
        Assert.IsTrue(loadResult.Accepted, loadResult.FailureDescription);
        source = sources.CreateIncludedReadySnapshot().Single();
        var shell = new MainWindowViewModel(new ApplicationSession());
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(root.FullName));
        using var loadViewModel = new LoadWorkspaceViewModel(
            new EmptySourcePathPicker(),
            loading,
            sources,
            workflow,
            shell,
            settings);
        using var discoveryViewModel = new DiscoveryWorkspaceViewModel(
            new StaticDiscoveryClient(source),
            new ActiveDiscoveryConfiguration(),
            sources,
            workflow,
            shell: shell,
            settingsService: settings);
        var loadView = new LoadWorkspaceView { DataContext = loadViewModel };
        var discoveryView = new DiscoveryWorkspaceView { DataContext = discoveryViewModel };
        var window = new Window
        {
            Width = 1400,
            Height = 760,
            Content = loadView,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var loadDivider = FindThumb(loadView, "load.source|load.path");
            Drag(loadDivider, 80);
            Assert.AreEqual(240, loadViewModel.SourceColumnWidth);
            Assert.AreEqual(110, loadViewModel.PathColumnWidth);
            var loadSizeDivider = FindThumb(loadView, "load.size");
            Drag(loadSizeDivider, 20);
            Assert.AreEqual(92, loadViewModel.SizeColumnWidth);
            Assert.AreEqual(100, loadViewModel.StatusColumnWidth);
            Assert.IsNull(FindThumbOrDefault(loadView, "load.status"));
            VerifyFinalColumnAlignment(loadView, "SourceRowsList", "Status", "Ready", 6);
            window.Width = 620;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            VerifyFinalColumnScrollsWithRows(loadView, "SourceRowsList", "Status", "Ready", 6);

            await discoveryViewModel.RunDiscoveryCommand.ExecuteAsync(null);
            window.Width = 1400;
            window.Content = discoveryView;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(
                ((FrameworkElement)loadView.FindName("LoadSearchBox")).Width,
                ((FrameworkElement)discoveryView.FindName("DiscoverySearchBox")).Width);
            VerifyPrimaryActionIsRightAligned(discoveryView);
            var discoveryDivider = FindThumb(
                discoveryView,
                "discovery.tag|discovery.databaseTag");
            Drag(discoveryDivider, 50);
            Assert.AreEqual(160, discoveryViewModel.TagColumnWidth);
            Assert.AreEqual(70, discoveryViewModel.DatabaseTagColumnWidth);
            var blacklistDivider = FindThumb(discoveryView, "discovery.sample");
            Drag(blacklistDivider, -30);
            Assert.AreEqual(140, discoveryViewModel.SampleColumnWidth);
            Assert.AreEqual(76, discoveryViewModel.BlacklistColumnWidth);
            Assert.IsNull(FindThumbOrDefault(discoveryView, "discovery.blacklist"));
            VerifyFinalColumnAlignment(
                discoveryView,
                "DiscoveryRows",
                "Blacklist",
                "Blacklist",
                7);
            window.Width = 620;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            VerifyPrimaryActionIsRightAligned(discoveryView);
            VerifyFinalColumnScrollsWithRows(
                discoveryView,
                "DiscoveryRows",
                "Blacklist",
                "Blacklist",
                7);
        }
        finally
        {
            window.Close();
        }

        var reopened = new ApplicationSettingsService(new ApplicationSettingsStore(root.FullName));
        using var restoredLoad = new LoadWorkspaceViewModel(
            new EmptySourcePathPicker(),
            loading,
            sources,
            workflow,
            shell,
            reopened);
        using var restoredDiscovery = new DiscoveryWorkspaceViewModel(
            new StaticDiscoveryClient(source),
            new ActiveDiscoveryConfiguration(),
            sources,
            workflow,
            shell: shell,
            settingsService: reopened);

        Assert.AreEqual(240, restoredLoad.SourceColumnWidth);
        Assert.AreEqual(110, restoredLoad.PathColumnWidth);
        Assert.AreEqual(92, restoredLoad.SizeColumnWidth);
        Assert.AreEqual(100, restoredLoad.StatusColumnWidth);
        Assert.AreEqual(160, restoredDiscovery.TagColumnWidth);
        Assert.AreEqual(70, restoredDiscovery.DatabaseTagColumnWidth);
        Assert.AreEqual(140, restoredDiscovery.SampleColumnWidth);
        Assert.AreEqual(76, restoredDiscovery.BlacklistColumnWidth);
        root.Delete(recursive: true);
    }

    private static Thumb FindThumb(DependencyObject parent, string tag)
    {
        var result = FindThumbOrDefault(parent, tag);
        Assert.IsNotNull(result, $"Column divider '{tag}' was not present in the real view.");
        return result;
    }

    private static Thumb? FindThumbOrDefault(DependencyObject parent, string tag) =>
        FindVisualChildren<Thumb>(parent).SingleOrDefault(thumb =>
            string.Equals(thumb.Tag as string, tag, StringComparison.Ordinal));

    private static void VerifyFinalColumnAlignment(
        FrameworkElement view,
        string listName,
        string headerText,
        string cellText,
        int column)
    {
        var list = FindVisualChildren<ListBox>(view).Single(item => item.Name == listName);
        var header = FindVisualChildren<FrameworkElement>(view).Single(element =>
            Grid.GetColumn(element) == column
            && string.Equals(GetDisplayedText(element), headerText, StringComparison.Ordinal)
            && !IsDescendantOf(element, list));
        var cell = FindVisualChildren<FrameworkElement>(list).First(element =>
            Grid.GetColumn(element) == column
            && string.Equals(GetDisplayedText(element), cellText, StringComparison.Ordinal));
        Assert.AreEqual(
            GetX(header, view),
            GetX(cell, view),
            1.5,
            $"The {headerText} header and row cell must share the same table column.");
        Assert.AreEqual(header.ActualWidth, cell.ActualWidth, 1.5);
    }

    private static void VerifyFinalColumnScrollsWithRows(
        FrameworkElement view,
        string listName,
        string headerText,
        string cellText,
        int column)
    {
        var list = FindVisualChildren<ListBox>(view).Single(item => item.Name == listName);
        var scrollViewer = FindVisualChildren<ScrollViewer>(list).Single();
        Assert.IsGreaterThan(0, scrollViewer.ScrollableWidth);
        scrollViewer.ScrollToHorizontalOffset(0);
        view.UpdateLayout();
        var header = FindVisualChildren<FrameworkElement>(view).Single(element =>
            Grid.GetColumn(element) == column
            && string.Equals(GetDisplayedText(element), headerText, StringComparison.Ordinal)
            && !IsDescendantOf(element, list));
        var cell = FindVisualChildren<FrameworkElement>(list).First(element =>
            Grid.GetColumn(element) == column
            && string.Equals(GetDisplayedText(element), cellText, StringComparison.Ordinal));
        var initialHeaderX = GetX(header, view);
        var initialCellX = GetX(cell, view);

        scrollViewer.ScrollToHorizontalOffset(scrollViewer.ScrollableWidth);
        view.UpdateLayout();

        var scrolledHeaderX = GetX(header, view);
        var scrolledCellX = GetX(cell, view);
        Assert.IsLessThan(initialHeaderX - 10, scrolledHeaderX);
        Assert.IsLessThan(initialCellX - 10, scrolledCellX);
        Assert.AreEqual(scrolledHeaderX, scrolledCellX, 1.5);
        Assert.AreEqual(
            initialHeaderX - scrolledHeaderX,
            initialCellX - scrolledCellX,
            1.5,
            "The fixed final column must scroll as one aligned part of the table.");
    }

    private static string? GetDisplayedText(FrameworkElement element) => element switch
    {
        Button button => button.Content as string,
        TextBlock text => text.Text,
        Border border => border.Child is FrameworkElement child ? GetDisplayedText(child) : null,
        StackPanel panel => panel.Children.OfType<FrameworkElement>()
            .Select(GetDisplayedText)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
        _ => null
    };

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static double GetX(Visual element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new Point()).X;

    private static void VerifyPrimaryActionIsRightAligned(FrameworkElement view)
    {
        var button = FindVisualChildren<Button>(view).Single(candidate =>
            candidate.Name == "BuildDatabaseButton");
        var actionGrid = VisualTreeHelper.GetParent(button) as Grid;
        Assert.IsNotNull(actionGrid);
        Assert.AreEqual(
            actionGrid.ActualWidth,
            GetX(button, actionGrid) + button.ActualWidth + button.Margin.Right,
            1.5);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void Drag(Thumb divider, double horizontalChange)
    {
        divider.RaiseEvent(new DragDeltaEventArgs(horizontalChange, 0)
        {
            RoutedEvent = Thumb.DragDeltaEvent
        });
        divider.RaiseEvent(new DragCompletedEventArgs(horizontalChange, 0, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });
    }

    private sealed class StaticSourceIntakeClient(LoadedSourceContract source) : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceIntakeClientResult(
                true,
                [source],
                FailureCode: null,
                FailureDescription: null));

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceRefreshClientResult(
                false,
                source,
                "not-used",
                "This client is not used by the interaction."));
    }

    private sealed class EmptySourcePathPicker : ISourcePathPicker
    {
        public string? PickXmlFile() => null;

        public string? PickFolder() => null;

        public string? PickArchive() => null;
    }

    private sealed class StaticDiscoveryClient(LoadedSourceContract source) : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            var family = new RowFamilyId(
                source.SourceSetId,
                RowFamilyKind.Root,
                "/root");
            var information = new DiscoveredInformation(
                new DiscoveryInformationIdentity(source.SourceSetId, "/root/code", "code"),
                1,
                [new DiscoveredSourceContribution(source.SourceId, "source.xml", 1)],
                "value")
            {
                RowFamilyId = family
            };
            return Task.FromResult(new DiscoveryClientResult(
                true,
                [information],
                [],
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully(source.SourceId.ToString())]),
                FailureCode: null,
                FailureDescription: null)
            {
                FamilyCatalog = new DiscoveryFamilyCatalog(
                    [new RowFamilyDefinition(family, null)])
            });
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview is not used by the interaction.");
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
