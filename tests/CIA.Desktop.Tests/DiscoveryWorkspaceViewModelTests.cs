using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DiscoveryWorkspaceViewModelTests
{
    [TestMethod]
    public async Task RunUsesIncludedReadySourcesAndPresentsReconciledResults()
    {
        var first = CreateSource("first.xml");
        var excluded = CreateSource("excluded.xml") with { IsIncluded = false };
        var unavailable = CreateSource("unavailable.xml") with
        {
            Status = LoadedSourceStatus.Unavailable
        };
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(
            workflow,
            first,
            excluded,
            unavailable);
        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                Assert.HasCount(1, sources);
                var source = sources[0];
                var information = new DiscoveredInformation(
                    "SerialNumber",
                    3,
                    [new DiscoveredSourceContribution(source.SourceId, "first.xml", 3)],
                    "SN-001");
                return Accept(correlation, sources, [information]);
            });
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.HasCount(1, client.LastSources);
        Assert.AreEqual(first.SourceId, client.LastSources[0].SourceId);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);
        Assert.AreEqual("Discovery complete", viewModel.StatusTitle);
        Assert.HasCount(1, viewModel.Information);
        var result = viewModel.Information[0];
        Assert.AreEqual("SerialNumber", result.InformationType);
        Assert.AreEqual(3, result.TotalOccurrenceCount);
        Assert.AreEqual(3, result.ContributingSources.Sum(source => source.OccurrenceCount));

        viewModel.InspectSourcesCommand.Execute(result);

        Assert.IsTrue(viewModel.IsSourceInspectionOpen);
        Assert.AreSame(result, viewModel.InspectedInformation);
        StringAssert.Contains(viewModel.SourceInspectionSummary, "matches tag aggregate");
    }

    [TestMethod]
    public async Task SourceSelectionChangeMakesRetainedDiscoveryResultOutOfDate()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, loading) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "PartNumber",
                    1,
                    [new DiscoveredSourceContribution(source.SourceId, "source.xml", 1)],
                    "PN-1")]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var inclusion = loading.SetInclusion(sourceSet.Items, isIncluded: false);

        Assert.IsTrue(inclusion.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("0 / 1 sources included", viewModel.IncludedSourceSummary);
        Assert.IsFalse(viewModel.RunDiscoveryCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task PresentationSupportsSearchSortingAndPaginationWithoutChangingResults()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = Enumerable.Range(1, 30)
            .Reverse()
            .Select(index => new DiscoveredInformation(
                $"Tag{index:00}",
                index,
                [new DiscoveredSourceContribution(source.SourceId, "source.xml", index)],
                $"Value {index:00}"))
            .ToArray();
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, information));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow)
        {
            PageSize = 25
        };

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(2, viewModel.PageCount);
        Assert.HasCount(25, viewModel.Information);
        Assert.AreEqual("Tag01", viewModel.Information[0].InformationType);

        viewModel.SortCommand.Execute("Occurrences");
        Assert.AreEqual(1, viewModel.Information[0].TotalOccurrenceCount);
        viewModel.SortCommand.Execute("Occurrences");
        Assert.AreEqual(30, viewModel.Information[0].TotalOccurrenceCount);

        viewModel.SearchText = "Tag29";
        Assert.HasCount(1, viewModel.Information);
        var filtered = viewModel.Information[0];
        Assert.AreEqual("Tag29", filtered.InformationType);
        Assert.AreEqual(29, filtered.ContributingSources.Sum(item => item.OccurrenceCount));
    }

    [TestMethod]
    public async Task FailedDiscoveryReportsRealIssueCountAndFailureStage()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => new DiscoveryClientResult(
                false,
                [],
                [new DiscoverySourceIssue(
                    source.SourceId,
                    "unsupported-xml-structure",
                    "The source structure is not supported.")],
                OperationCompletion.FromTerminalOutcome(
                    correlation,
                    OperationOutcome.Failed,
                    sources.Select(item => OperationItemStatus.Failed(
                        item.SourceId.ToString(),
                        "unsupported-xml-structure"))),
                "discovery-no-usable-sources",
                "Discovery could not interpret any source in the active source set."));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual("Discovery failed", viewModel.StatusTitle);
        StringAssert.Contains(viewModel.ResultSummary, "1 issues");
        Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);
        Assert.AreNotEqual("Stage: Complete", viewModel.ProgressStage);
    }

    [TestMethod]
    public async Task FailedReRunRetainsPriorRowsButMarksThemOutOfDate()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var callCount = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => ++callCount == 1
                ? Accept(
                    correlation,
                    sources,
                    [new DiscoveredInformation(
                        "RetainedTag",
                        1,
                        [new DiscoveredSourceContribution(source.SourceId, "source.xml", 1)],
                        "retained value")])
                : Reject(correlation, sources, "processing-host-unavailable"));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("RetainedTag", viewModel.Information[0].InformationType);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        Assert.AreEqual("Discovery re-run failed", viewModel.StatusTitle);
        StringAssert.Contains(viewModel.StatusDetail, "Previous Discovery results are retained");
        Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);
    }

    [TestMethod]
    public async Task SelectionAndBlacklistRemainDistinctInTheActiveConfiguration()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = CreateInformation(source.SourceId, "Alpha", "Beta");
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, information));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");

        viewModel.ToggleSelectionCommand.Execute(alpha);

        Assert.IsTrue(alpha.IsSelected);
        Assert.IsFalse(alpha.IsBlacklisted);
        Assert.AreEqual("1 / 2 selected", viewModel.SelectionSummary);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));

        viewModel.ToggleBlacklistCommand.Execute(alpha);

        Assert.IsFalse(alpha.IsSelected);
        Assert.IsTrue(alpha.IsBlacklisted);
        Assert.AreEqual("0 / 2 selected", viewModel.SelectionSummary);
        Assert.IsFalse(viewModel.ToggleSelectionCommand.CanExecute(alpha));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpha"));

        viewModel.ToggleBlacklistCommand.Execute(alpha);
        Assert.IsFalse(alpha.IsBlacklisted);
        Assert.AreEqual("Neutral", alpha.DispositionText);
        Assert.IsTrue(viewModel.ToggleSelectionCommand.CanExecute(alpha));

        viewModel.ToggleSelectionCommand.Execute(alpha);
        viewModel.ToggleSelectionCommand.Execute(alpha);
        Assert.IsFalse(alpha.IsSelected);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Database);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task BulkSelectionAffectsOnlyTheFilteredVisibleSubset()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                CreateInformation(source.SourceId, "Alpha", "Alpine", "Beta", "Gamma")));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpine = viewModel.Information.Single(item => item.InformationType == "Alpine");
        viewModel.ToggleBlacklistCommand.Execute(alpine);
        viewModel.SearchText = "Alp";

        viewModel.SelectVisibleCommand.Execute(null);

        Assert.AreEqual(1, configuration.Current.SelectedCount);
        Assert.AreEqual(1, configuration.Current.BlacklistedCount);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpine"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Beta"));
        Assert.AreEqual("1 / 4 selected", viewModel.SelectionSummary);

        viewModel.ShowBlacklisted = false;
        Assert.AreEqual(1, viewModel.FilteredCount);
        viewModel.DeselectVisibleCommand.Execute(null);

        Assert.AreEqual(0, configuration.Current.SelectedCount);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpine"));
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Database);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task ReRunRetainsMatchingActiveSelectionAndDropsUnavailableIdentities()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var run = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                ++run == 1
                    ? CreateInformation(source.SourceId, "Alpha", "Removed")
                    : CreateInformation(source.SourceId, "Alpha", "Added")));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");
        viewModel.ToggleSelectionCommand.Execute(alpha);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(2, client.CallCount);
        Assert.HasCount(2, configuration.Current.Items);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Added"));
        Assert.IsFalse(configuration.Current.Items.Any(item => item.InformationType == "Removed"));
        Assert.AreEqual("1 / 2 selected", viewModel.SelectionSummary);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);
        Assert.AreEqual("Stage: Complete", viewModel.ProgressStage);
    }

    private static IReadOnlyList<DiscoveredInformation> CreateInformation(
        SourceId sourceId,
        params string[] informationTypes)
    {
        return informationTypes
            .Select(informationType => new DiscoveredInformation(
                informationType,
                1,
                [new DiscoveredSourceContribution(sourceId, "source.xml", 1)],
                $"{informationType} value"))
            .ToArray();
    }

    private static DiscoveryInformationDisposition GetDisposition(
        ActiveDiscoveryConfiguration configuration,
        string informationType)
    {
        return configuration.Current.Items
            .Single(item => item.InformationType == informationType)
            .Disposition;
    }

    private static async Task<(ActiveLoadedSourceSet SourceSet, SourceLoadingCoordinator Loading)>
        LoadSourcesAsync(
            ApplicationWorkflowCoordinator workflow,
            params LoadedSourceContract[] sources)
    {
        var sourceSet = new ActiveLoadedSourceSet();
        var client = new StubSourceIntakeClient(
            new SourceIntakeClientResult(
                true,
                sources,
                FailureCode: null,
                FailureDescription: null));
        var loading = new SourceLoadingCoordinator(client, sourceSet, workflow);

        var result = await loading.AddAsync(SourceSelectionKind.XmlFile, sources[0].Path);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(sources.Length, sourceSet.Items);
        return (sourceSet, loading);
    }

    private static LoadedSourceContract CreateSource(string name)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath(name),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
    }

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator()
    {
        return new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
    }

    private static DiscoveryClientResult Accept(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<DiscoveredInformation> information)
    {
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            sources.Select(source => OperationItemStatus.ProcessedSuccessfully(
                source.SourceId.ToString())));
        return new DiscoveryClientResult(
            true,
            information,
            [],
            completion,
            FailureCode: null,
            FailureDescription: null);
    }

    private static DiscoveryClientResult Reject(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        string failureCode)
    {
        return new DiscoveryClientResult(
            false,
            [],
            [],
            OperationCompletion.FromTerminalOutcome(
                correlation,
                OperationOutcome.Failed,
                sources.Select(source => OperationItemStatus.Unprocessed(
                    source.SourceId.ToString(),
                    failureCode))),
            failureCode,
            "The Processing Host could not complete Discovery.");
    }

    private sealed class StubDiscoveryClient(
        Func<OperationCorrelation, IReadOnlyList<LoadedSourceContract>, DiscoveryClientResult>
            resultFactory) : IDiscoveryClient
    {
        public IReadOnlyList<LoadedSourceContract> LastSources { get; private set; } = [];

        public int CallCount { get; private set; }

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSources = sources;
            return Task.FromResult(resultFactory(correlation, sources));
        }
    }

    private sealed class StubSourceIntakeClient(SourceIntakeClientResult result)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(result);

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SourceRefreshClientResult(
                    true,
                    source,
                    FailureCode: null,
                    FailureDescription: null));
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
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
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
