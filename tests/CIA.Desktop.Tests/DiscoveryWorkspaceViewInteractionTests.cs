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
