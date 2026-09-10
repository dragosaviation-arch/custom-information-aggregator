using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Views;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DiscoveryWorkspaceViewInteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task SelectionCheckboxAlsoSelectsPreviewAndLoadsFirstOccurrence()
    {
        await WpfTestApplication.RunAsync(VerifyInteractionAsync).WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task SameNameRowsSwitchPreviewByFullIdentityWithoutConcurrentReads()
    {
        await WpfTestApplication.RunAsync(VerifySameNameSwitchAsync).WaitAsync(TestTimeout);
    }

    private static async Task VerifySameNameSwitchAsync()
    {
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath("wpf-same-name-source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var loading = new SourceLoadingCoordinator(
            new SuccessfulSourceIntakeClient(source),
            sourceSet,
            workflow);
        Assert.IsTrue((await loading.AddAsync(SourceSelectionKind.XmlFile, source.Path)).Accepted);

        var client = new SameNameWpfDiscoveryClient();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);
        var view = new DiscoveryWorkspaceView { DataContext = viewModel };
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
            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            await client.FirstRequestStarted;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var second = viewModel.Information.Single(item =>
                item.StructuralPath == "/root/second/slot");
            var rows = (ListBox)view.FindName("DiscoveryRows");
            rows.ScrollIntoView(second);
            view.UpdateLayout();
            var row = (ListBoxItem?)rows.ItemContainerGenerator.ContainerFromItem(second);
            Assert.IsNotNull(row);

            row.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = row
            });
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.AreSame(second, rows.SelectedItem);
            Assert.AreSame(second, viewModel.SelectedInformation);
            Assert.AreEqual(3, viewModel.OccurrenceTotal);
            Assert.AreEqual(1, client.OccurrenceCallCount);

            client.CompleteFirstRequest();
            await client.SecondRequestCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            if (viewModel.SelectInformationCommand.ExecutionTask is { } selectionTask)
            {
                await selectionTask;
            }

            Assert.AreEqual(1, client.MaximumConcurrentRequests);
            Assert.AreEqual(second.Identity, client.LastLookup?.Identity);
            Assert.AreEqual("second identity value", viewModel.OccurrencePreviewText);
            Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
            Assert.AreEqual(3, viewModel.OccurrenceTotal);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task VerifyInteractionAsync()
    {
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath("wpf-interaction-source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var loading = new SourceLoadingCoordinator(
            new SuccessfulSourceIntakeClient(source),
            sourceSet,
            workflow);
        var load = await loading.AddAsync(SourceSelectionKind.XmlFile, source.Path);
        Assert.IsTrue(load.Accepted);

        var client = new RecordingDiscoveryClient(source.SourceId);
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);
        var view = new DiscoveryWorkspaceView
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
            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var beta = viewModel.Information.Single(item => item.InformationType == "beta");
            var rows = (ListBox)view.FindName("DiscoveryRows");
            rows.ScrollIntoView(beta);
            view.UpdateLayout();
            var row = (ListBoxItem?)rows.ItemContainerGenerator.ContainerFromItem(beta);
            Assert.IsNotNull(row);
            var selectionCheckBox = FindVisualDescendant<CheckBox>(
                row,
                checkBox => string.Equals(
                    AutomationProperties.GetName(checkBox),
                    "Select discovered information",
                    StringComparison.Ordinal));
            Assert.IsNotNull(selectionCheckBox);

            selectionCheckBox.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = selectionCheckBox
            });
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.AreSame(beta, rows.SelectedItem);
            Assert.AreSame(beta, viewModel.SelectedInformation);
            Assert.AreEqual(3, viewModel.OccurrenceTotal);

            Assert.IsNotNull(selectionCheckBox.Command);
            Assert.IsTrue(selectionCheckBox.Command.CanExecute(
                selectionCheckBox.CommandParameter));
            selectionCheckBox.Command.Execute(selectionCheckBox.CommandParameter);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (viewModel.SelectInformationCommand.ExecutionTask is { } selectionTask)
            {
                await selectionTask;
            }

            Assert.AreSame(beta, rows.SelectedItem);
            Assert.AreSame(beta, viewModel.SelectedInformation);
            Assert.IsTrue(beta.IsSelected, "The business selection checkbox did not remain functional.");
            Assert.AreEqual(3, viewModel.OccurrenceTotal);
            Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
            Assert.AreEqual("beta occurrence 1", viewModel.OccurrencePreviewText);
            Assert.IsTrue(viewModel.NextOccurrenceCommand.CanExecute(null));
            Assert.IsFalse(viewModel.PreviousOccurrenceCommand.CanExecute(null));
            Assert.AreEqual("beta", client.LastOccurrenceLookup?.InformationType);
            Assert.AreEqual(1, client.LastOccurrenceLookup?.GlobalOrdinal);

            var databaseTagField = FindVisualDescendant<TextBox>(
                view,
                textBox => string.Equals(
                    AutomationProperties.GetName(textBox),
                    "Database Tag Name Override",
                    StringComparison.Ordinal));
            Assert.IsNotNull(databaseTagField);
            Assert.IsTrue(databaseTagField.IsEnabled);
            Assert.AreEqual("beta", databaseTagField.Text);

            databaseTagField.Text = "Mapped beta";
            databaseTagField.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            await Dispatcher.Yield(DispatcherPriority.DataBind);

            Assert.AreEqual("Mapped beta", beta.DatabaseTag);
            Assert.IsTrue(beta.HasDatabaseTagOverride);
            Assert.AreEqual("Mapped beta", viewModel.SelectedDatabaseTag);
        }
        finally
        {
            window.Close();
        }
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject parent,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class RecordingDiscoveryClient(SourceId sourceId) : IDiscoveryClient
    {
        public DiscoveryOccurrenceLookup? LastOccurrenceLookup { get; private set; }

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            var completion = OperationCompletion.FromCompletedItems(
                correlation,
                sources.Select(source => OperationItemStatus.ProcessedSuccessfully(
                    source.SourceId.ToString())));
            return Task.FromResult(new DiscoveryClientResult(
                true,
                [
                    new DiscoveredInformation(
                        "alpha",
                        2,
                        [new DiscoveredSourceContribution(sourceId, "source.xml", 2)],
                        "alpha occurrence 1"),
                    new DiscoveredInformation(
                        "beta",
                        3,
                        [new DiscoveredSourceContribution(sourceId, "source.xml", 3)],
                        "beta occurrence 1")
                ],
                [],
                completion,
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            LastOccurrenceLookup = lookup;
            return Task.FromResult(new DiscoveryOccurrenceClientResult(
                true,
                new DiscoveredOccurrence(
                    lookup.InformationType,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    lookup.Source.SourceId,
                    $"{lookup.InformationType} occurrence {lookup.GlobalOrdinal}"),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class SameNameWpfDiscoveryClient : IDiscoveryClient
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRequestCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;
        private int _maximumConcurrentRequests;
        private int _occurrenceCallCount;

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public Task SecondRequestCompleted => _secondRequestCompleted.Task;

        public int OccurrenceCallCount => Volatile.Read(ref _occurrenceCallCount);

        public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);

        public DiscoveryOccurrenceLookup? LastLookup { get; private set; }

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            var source = sources.Single();
            var first = new DiscoveryInformationIdentity(
                source.SourceSetId,
                "/root/first/toolnbr",
                "toolnbr",
                SourceValueCandidateKind.Element,
                "/root/first/toolnbr");
            var second = new DiscoveryInformationIdentity(
                source.SourceSetId,
                "/root/second/slot",
                "toolnbr",
                SourceValueCandidateKind.Structural,
                "/root/second/slot[@key='tool']");
            var completion = OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully(source.SourceId.ToString())]);
            return Task.FromResult(new DiscoveryClientResult(
                true,
                [
                    new DiscoveredInformation(
                        first,
                        2,
                        [new DiscoveredSourceContribution(source.SourceId, "source.xml", 2)],
                        "first sample"),
                    new DiscoveredInformation(
                        second,
                        3,
                        [new DiscoveredSourceContribution(source.SourceId, "source.xml", 3)],
                        "second sample")
                ],
                [],
                completion,
                FailureCode: null,
                FailureDescription: null));
        }

        public async Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            LastLookup = lookup;
            var callNumber = Interlocked.Increment(ref _occurrenceCallCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximumConcurrentRequests(active);

            try
            {
                if (callNumber == 1)
                {
                    _firstRequestStarted.TrySetResult();
                    await _releaseFirstRequest.Task;
                }

                return new DiscoveryOccurrenceClientResult(
                    true,
                    new DiscoveredOccurrence(
                        lookup.Identity,
                        lookup.GlobalOrdinal,
                        lookup.TotalOccurrenceCount,
                        lookup.Source.SourceId,
                        lookup.Identity.StructuralPath.Contains("first", StringComparison.Ordinal)
                            ? "first identity value"
                            : "second identity value"),
                    FailureCode: null,
                    FailureDescription: null);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                if (callNumber == 2)
                {
                    _secondRequestCompleted.TrySetResult();
                }
            }
        }

        public void CompleteFirstRequest()
        {
            _releaseFirstRequest.TrySetResult();
        }

        private void UpdateMaximumConcurrentRequests(int activeRequests)
        {
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentRequests);
                if (activeRequests <= maximum
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrentRequests,
                        activeRequests,
                        maximum) == maximum)
                {
                    return;
                }
            }
        }
    }

    private sealed class SuccessfulSourceIntakeClient(LoadedSourceContract source)
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
            OperationId operationId,
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
