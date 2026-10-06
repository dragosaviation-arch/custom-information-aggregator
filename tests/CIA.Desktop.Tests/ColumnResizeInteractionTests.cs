using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Contracts.Discovery;
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
    public async Task LoadAndDiscoveryHeaderDraggingPersistsAdjacentWidthsAcrossRestart()
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
        var loading = new SourceLoadingCoordinator(
            new EmptySourceIntakeClient(),
            sources,
            workflow);
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
            new EmptyDiscoveryClient(),
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

            window.Content = discoveryView;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var discoveryDivider = FindThumb(
                discoveryView,
                "discovery.tag|discovery.databaseTag");
            Drag(discoveryDivider, 50);
            Assert.AreEqual(160, discoveryViewModel.TagColumnWidth);
            Assert.AreEqual(70, discoveryViewModel.DatabaseTagColumnWidth);
            var blacklistDivider = FindThumb(
                discoveryView,
                "discovery.sample|discovery.blacklist");
            Drag(blacklistDivider, -30);
            Assert.AreEqual(140, discoveryViewModel.SampleColumnWidth);
            Assert.AreEqual(134, discoveryViewModel.BlacklistColumnWidth);
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
            new EmptyDiscoveryClient(),
            new ActiveDiscoveryConfiguration(),
            sources,
            workflow,
            shell: shell,
            settingsService: reopened);

        Assert.AreEqual(240, restoredLoad.SourceColumnWidth);
        Assert.AreEqual(110, restoredLoad.PathColumnWidth);
        Assert.AreEqual(160, restoredDiscovery.TagColumnWidth);
        Assert.AreEqual(70, restoredDiscovery.DatabaseTagColumnWidth);
        Assert.AreEqual(140, restoredDiscovery.SampleColumnWidth);
        Assert.AreEqual(134, restoredDiscovery.BlacklistColumnWidth);
        root.Delete(recursive: true);
    }

    private static Thumb FindThumb(DependencyObject parent, string tag)
    {
        var result = FindVisualChildren<Thumb>(parent).SingleOrDefault(thumb =>
            string.Equals(thumb.Tag as string, tag, StringComparison.Ordinal));
        Assert.IsNotNull(result, $"Column divider '{tag}' was not present in the real view.");
        return result;
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

    private sealed class EmptySourceIntakeClient : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceIntakeClientResult(
                false,
                [],
                "not-used",
                "This client is not used by the interaction."));

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

    private sealed class EmptyDiscoveryClient : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery is not used by the interaction.");

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
