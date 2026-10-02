using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class WorkflowNavigationPreferenceTests
{
    [TestMethod]
    public async Task DiscoveryNavigationOccursOnlyAfterSuccessfulGenerationWhenEnabled()
    {
        Assert.AreEqual(
            WorkspaceArea.Discovery,
            await RunDiscoveryScenarioAsync(DiscoveryScenario.Success, enabled: true));
        Assert.AreEqual(
            WorkspaceArea.Load,
            await RunDiscoveryScenarioAsync(DiscoveryScenario.Success, enabled: false));
        Assert.AreEqual(
            WorkspaceArea.Load,
            await RunDiscoveryScenarioAsync(DiscoveryScenario.Failure, enabled: true));
        Assert.AreEqual(
            WorkspaceArea.Load,
            await RunDiscoveryScenarioAsync(DiscoveryScenario.Cancelled, enabled: true));
    }

    [TestMethod]
    public async Task DatabaseNavigationOccursOnlyAfterSuccessfulCreationWhenEnabled()
    {
        Assert.AreEqual(
            WorkspaceArea.Database,
            await RunDatabaseScenarioAsync(databaseSucceeds: true, enabled: true));
        Assert.AreEqual(
            WorkspaceArea.Discovery,
            await RunDatabaseScenarioAsync(databaseSucceeds: true, enabled: false));
        Assert.AreEqual(
            WorkspaceArea.Discovery,
            await RunDatabaseScenarioAsync(databaseSucceeds: false, enabled: true));
    }

    [TestMethod]
    public async Task DiscoveryReportsRealSourceProgressFromZeroThroughIntermediateToComplete()
    {
        using var workflow = CreateWorkflowCoordinator();
        var sources = new[] { CreateSource("first.xml"), CreateSource("second.xml") };
        var activeSources = await LoadSourcesAsync(workflow, sources);
        var client = new ControlledProgressDiscoveryClient();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            activeSources,
            workflow);

        var run = viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await client.IntermediateReported.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsFalse(viewModel.IsProgressIndeterminate);
        Assert.AreEqual(2, viewModel.ProgressMaximum);
        Assert.AreEqual(1, viewModel.ProgressValue);
        Assert.AreEqual("50%", viewModel.ProgressPercentText);

        client.Complete();
        await run;

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual(2, viewModel.ProgressValue);
        Assert.AreEqual("100%", viewModel.ProgressPercentText);
    }

    private static async Task<WorkspaceArea> RunDiscoveryScenarioAsync(
        DiscoveryScenario scenario,
        bool enabled)
    {
        var root = Directory.CreateTempSubdirectory("CIA.SPR191.DiscoveryNav.");
        try
        {
            var settings = new ApplicationSettingsService(new ApplicationSettingsStore(root.FullName));
            Assert.IsTrue(settings.Save(settings.Current with
            {
                OpenDiscoveryWhenGenerationCompletes = enabled
            }).Succeeded);
            using var workflow = CreateWorkflowCoordinator();
            var source = CreateSource("navigation.xml");
            var activeSources = await LoadSourcesAsync(workflow, [source]);
            var shell = new MainWindowViewModel(new ApplicationSession());
            using var viewModel = new DiscoveryWorkspaceViewModel(
                new NavigationDiscoveryClient(scenario),
                new ActiveDiscoveryConfiguration(),
                activeSources,
                workflow,
                shell: shell,
                settingsService: settings);

            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            return shell.SelectedWorkspace.Area;
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<WorkspaceArea> RunDatabaseScenarioAsync(
        bool databaseSucceeds,
        bool enabled)
    {
        var root = Directory.CreateTempSubdirectory("CIA.SPR191.DatabaseNav.");
        try
        {
            var settings = new ApplicationSettingsService(new ApplicationSettingsStore(root.FullName));
            Assert.IsTrue(settings.Save(settings.Current with
            {
                OpenDatabaseWhenCreationCompletes = enabled
            }).Succeeded);
            using var workflow = CreateWorkflowCoordinator();
            var source = CreateSource("database-navigation.xml");
            var activeSources = await LoadSourcesAsync(workflow, [source]);
            var configuration = new ActiveDiscoveryConfiguration();
            var database = new DatabaseBuildCoordinator(
                configuration,
                activeSources,
                workflow,
                new NavigationDatabaseClient(databaseSucceeds),
                NullLogger<DatabaseBuildCoordinator>.Instance);
            var shell = new MainWindowViewModel(new ApplicationSession());
            using var viewModel = new DiscoveryWorkspaceViewModel(
                new NavigationDiscoveryClient(DiscoveryScenario.Success),
                configuration,
                activeSources,
                workflow,
                database,
                shell: shell,
                settingsService: settings);
            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            viewModel.ToggleSelectionCommand.Execute(viewModel.Information.Single());
            shell.SelectedWorkspace = shell.Workspaces.Single(workspace =>
                workspace.Area == WorkspaceArea.Discovery);

            await viewModel.BuildDatabaseCommand.ExecuteAsync(null);
            return shell.SelectedWorkspace.Area;
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<ActiveLoadedSourceSet> LoadSourcesAsync(
        ApplicationWorkflowCoordinator workflow,
        IReadOnlyList<LoadedSourceContract> sources)
    {
        var activeSources = new ActiveLoadedSourceSet();
        var loading = new SourceLoadingCoordinator(
            new StaticSourceIntakeClient(sources),
            activeSources,
            workflow);
        Assert.IsTrue((await loading.AddAsync(
            SourceSelectionKind.Folder,
            Path.GetFullPath("navigation-input"))).Accepted);
        return activeSources;
    }

    private static LoadedSourceContract CreateSource(string name) =>
        new(
            SourceId.CreateNew(),
            Path.GetFullPath(name),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator() =>
        new(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());

    private enum DiscoveryScenario
    {
        Success,
        Failure,
        Cancelled
    }

    private sealed class NavigationDiscoveryClient(DiscoveryScenario scenario) : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            if (scenario == DiscoveryScenario.Cancelled)
            {
                throw new OperationCanceledException();
            }

            var source = sources.Single();
            if (scenario == DiscoveryScenario.Failure)
            {
                return Task.FromResult(new DiscoveryClientResult(
                    false,
                    [],
                    [],
                    OperationCompletion.FromTerminalOutcome(
                        correlation,
                        OperationOutcome.Failed,
                        [OperationItemStatus.Failed(source.SourceId.ToString(), "test-failure")]),
                    "test-failure",
                    "Discovery failed for the navigation regression."));
            }

            var identity = new DiscoveryInformationIdentity(
                source.SourceSetId,
                "/root/value",
                "value");
            return Task.FromResult(new DiscoveryClientResult(
                true,
                [new DiscoveredInformation(
                    identity,
                    1,
                    [new DiscoveredSourceContribution(source.SourceId, "navigation.xml", 1)],
                    "sample")],
                [],
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully(source.SourceId.ToString())]),
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DiscoveryOccurrenceClientResult(
                true,
                new DiscoveredOccurrence(
                    lookup.Identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    lookup.Source.SourceId,
                    "sample"),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class ControlledProgressDiscoveryClient : IDiscoveryClient
    {
        private readonly TaskCompletionSource _intermediateReported =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task IntermediateReported => _intermediateReported.Task;

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default) =>
            RunAsync(correlation, sources, progress: null, cancellationToken);

        public async Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            IProgress<DiscoveryProgressSnapshot>? progress,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new DiscoveryProgressSnapshot(1, sources.Count));
            _intermediateReported.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            var information = sources.Select(source => new DiscoveredInformation(
                new DiscoveryInformationIdentity(source.SourceSetId, "/root/value", "value"),
                1,
                [new DiscoveredSourceContribution(source.SourceId, Path.GetFileName(source.Path), 1)],
                "sample")).ToArray();
            return new DiscoveryClientResult(
                true,
                information,
                [],
                OperationCompletion.FromCompletedItems(
                    correlation,
                    sources.Select(source => OperationItemStatus.ProcessedSuccessfully(
                        source.SourceId.ToString())).ToArray()),
                FailureCode: null,
                FailureDescription: null);
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DiscoveryOccurrenceClientResult(
                true,
                new DiscoveredOccurrence(
                    lookup.Identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    lookup.Source.SourceId,
                    "sample"),
                FailureCode: null,
                FailureDescription: null));
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class NavigationDatabaseClient(bool succeeds) : IDatabaseClient
    {
        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var dataset = specification.Datasets.Single();
            if (!succeeds)
            {
                return Task.FromResult(new DatabaseClientResult(
                    false,
                    OperationCompletion.FromTerminalOutcome(
                        correlation,
                        OperationOutcome.Failed,
                        [OperationItemStatus.Failed(
                            dataset.Sources[0].SourceId.ToString(),
                            "test-failure")]),
                    PublishedGeneration: null,
                    "test-failure",
                    "Database creation failed for the navigation regression."));
            }

            var columns = dataset.Fields.Select((field, index) => new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    dataset.SourceSetId,
                    field.FieldKey,
                    DatabaseRepeatCoordinatePath.Empty),
                field.EffectiveName,
                index + 1)).ToArray();
            var generation = new DatabaseGenerationSummary(
                correlation.OperationId,
                [new DatabaseDatasetSummary(
                    dataset.SourceSetId,
                    dataset.DisplayName,
                    dataset.Ordinal,
                    dataset.RepeatedDataLayout,
                    1,
                    1,
                    columns,
                    dataset.Fields)]);
            return Task.FromResult(new DatabaseClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully(
                        dataset.Sources[0].SourceId.ToString())]),
                generation,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class StaticSourceIntakeClient(
        IReadOnlyList<LoadedSourceContract> sources) : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceIntakeClientResult(
                true,
                sources,
                FailureCode: null,
                FailureDescription: null));

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceRefreshClientResult(
                true,
                source,
                FailureCode: null,
                FailureDescription: null));
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
