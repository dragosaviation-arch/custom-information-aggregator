using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class EscCancellationInteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task EscapeFromAWorkspaceCancelsOnlyTheCurrentActiveOperation()
    {
        await WpfTestApplication.RunAsync(VerifyEscapeCancellationAsync).WaitAsync(TestTimeout);
    }

    [TestMethod]
    public void ProductionShellExposesNoOnScreenOrAlternativeCancellationControl()
    {
        var root = FindRepositoryRoot();
        var desktopDirectory = Path.Combine(root, "src", "CIA.Desktop");
        var mainWindowXaml = File.ReadAllText(Path.Combine(desktopDirectory, "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(Path.Combine(desktopDirectory, "MainWindow.xaml.cs"));
        var allXaml = Directory
            .EnumerateFiles(desktopDirectory, "*.xaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        StringAssert.Contains(mainWindowXaml, "KeyDown=\"OnWindowKeyDown\"");
        Assert.AreEqual(1, CountOccurrences(mainWindowCode, "Key.Escape"));
        Assert.IsFalse(mainWindowCode.Contains("ModifierKeys", StringComparison.Ordinal));
        Assert.IsFalse(mainWindowXaml.Contains("KeyBinding", StringComparison.Ordinal));
        Assert.IsFalse(allXaml.Any(xaml => xaml.Contains(
            "CancelActiveOperationCommand",
            StringComparison.Ordinal)));
    }

    private static async Task VerifyEscapeCancellationAsync()
    {
        using var context = new MainWindowTestContext();
        var window = context.Window;

        try
        {
            window.Show();
            window.UpdateLayout();

            var inactiveEscape = RaiseEscape(window);

            Assert.IsFalse(inactiveEscape.Handled);
            Assert.AreEqual(0, context.Supervisor.CancellationRequestCount);
            Assert.AreEqual(WorkflowStateSnapshot.Empty, context.Workflow.Current);

            context.Workflow.RecordSourceSelectionChanged(true);
            var begin = await context.Workflow.BeginOperationAsync(
                WorkflowOperationKind.Discovery);
            Assert.IsTrue(begin.Accepted);

            context.Shell.SelectedWorkspace = context.Shell.Workspaces.Single(
                workspace => workspace.Area == WorkspaceArea.Settings);
            window.UpdateLayout();
            var settingsView = (FrameworkElement)window.FindName("SettingsWorkspaceView");
            var workspaceInput = FindVisualDescendant<TextBox>(settingsView);
            Assert.IsNotNull(workspaceInput);

            var firstEscape = RaiseEscape(workspaceInput);

            Assert.IsTrue(firstEscape.Handled);
            Assert.AreEqual(1, context.Supervisor.CancellationRequestCount);
            Assert.AreEqual(
                begin.Operation!.OperationId,
                context.Supervisor.LastCancellationOperationId);
            Assert.AreEqual(
                WorkflowOperationState.Cancelling,
                context.Workflow.Current.LatestOperation?.State);
            Assert.AreEqual(
                "Discovery — Cancelling. Cancellation requested.",
                context.GlobalStatus.OperationStatusText);

            RaiseEscape(workspaceInput);
            Assert.AreEqual(1, context.Supervisor.CancellationRequestCount);

            context.Supervisor.AcceptCancellation();
            var cancellationTask = context.GlobalStatus.CancelActiveOperationCommand.ExecutionTask;
            Assert.IsNotNull(cancellationTask);
            await cancellationTask;

            var completed = context.Workflow.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Cancelled);

            Assert.IsTrue(completed.Accepted);
            Assert.AreEqual(
                WorkflowOperationState.Cancelled,
                context.Workflow.Current.LatestOperation?.State);
            await window.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.DataBind);
            Assert.AreEqual(
                "Discovery — Cancelled. The operation was cancelled.",
                context.GlobalStatus.OperationStatusText);
        }
        finally
        {
            window.Close();
        }
    }

    private static KeyEventArgs RaiseEscape(UIElement source)
    {
        var presentationSource = PresentationSource.FromVisual(source)
            ?? throw new InvalidOperationException("The WPF test source is not presented.");
        var keyEvent = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            presentationSource,
            Environment.TickCount,
            Key.Escape)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = source
        };
        source.RaiseEvent(keyEvent);
        return keyEvent;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CIA.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The repository root containing CIA.slnx was not found.");
    }

    private sealed class MainWindowTestContext : IDisposable
    {
        private readonly LoadWorkspaceViewModel _loadWorkspace;
        private readonly DiscoveryWorkspaceViewModel _discoveryWorkspace;
        private readonly DatabaseWorkspaceViewModel _databaseWorkspace;

        public MainWindowTestContext()
        {
            Supervisor = new ControlledProcessingHostSupervisor();
            Workflow = new ApplicationWorkflowCoordinator(
                Supervisor,
                new RecordingProcessingHistoryRecorder());
            GlobalStatus = new GlobalStatusViewModel(Supervisor, Workflow);
            Shell = new MainWindowViewModel(new ApplicationSession());
            var sourceSet = new ActiveLoadedSourceSet();
            var sourceLoading = new SourceLoadingCoordinator(
                new InertSourceIntakeClient(),
                sourceSet,
                Workflow);
            _loadWorkspace = new LoadWorkspaceViewModel(
                new InertSourcePathPicker(),
                sourceLoading,
                sourceSet,
                Workflow,
                Shell);
            var discoveryConfiguration = new ActiveDiscoveryConfiguration();
            _discoveryWorkspace = new DiscoveryWorkspaceViewModel(
                new InertDiscoveryClient(),
                discoveryConfiguration,
                sourceSet,
                Workflow);
            _databaseWorkspace = new DatabaseWorkspaceViewModel(
                discoveryConfiguration,
                Workflow);
            var applicationPaths = ApplicationPaths.FromLocalApplicationData(
                Path.Combine(Path.GetTempPath(), "CIA.SPR-100", Guid.NewGuid().ToString("N")));
            var settingsWorkspace = new SettingsWorkspaceViewModel(
                new EmptyProcessingHistoryReader(),
                new SettingsWorkspaceRuntimePaths(
                    applicationPaths,
                    applicationPaths.LogsDirectory));
            Window = new MainWindow(
                Shell,
                GlobalStatus,
                _loadWorkspace,
                _discoveryWorkspace,
                _databaseWorkspace,
                settingsWorkspace)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
        }

        public ControlledProcessingHostSupervisor Supervisor { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public GlobalStatusViewModel GlobalStatus { get; }

        public MainWindowViewModel Shell { get; }

        public MainWindow Window { get; }

        public void Dispose()
        {
            _loadWorkspace.Dispose();
            _discoveryWorkspace.Dispose();
            _databaseWorkspace.Dispose();
            GlobalStatus.Dispose();
            Workflow.Dispose();
        }
    }

    private sealed class ControlledProcessingHostSupervisor : IProcessingHostSupervisor
    {
        private readonly TaskCompletionSource<bool> _cancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public int CancellationRequestCount { get; private set; }

        public OperationId? LastCancellationOperationId { get; private set; }

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationRequestCount++;
            LastCancellationOperationId = operationId;
            return _cancellation.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void AcceptCancellation()
        {
            _cancellation.TrySetResult(true);
        }
    }

    private sealed class InertSourcePathPicker : ISourcePathPicker
    {
        public string? PickXmlFile() => null;

        public string? PickFolder() => null;

        public string? PickArchive() => null;
    }

    private sealed class InertSourceIntakeClient : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InertDiscoveryClient : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyProcessingHistoryReader : IProcessingHistoryReader
    {
        public ProcessingHistorySnapshot Read()
        {
            return new ProcessingHistorySnapshot([], [], ReadProblem: null);
        }
    }
}
