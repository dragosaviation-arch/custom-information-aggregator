using System.Collections.Specialized;
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
    public async Task OccurrencePreviewLoadsFirstValueAndNavigatesWithinBoundaries()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var values = new[] { "First value", "Second value", "Third value" };
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "Tag",
                    values.Length,
                    [new DiscoveredSourceContribution(source.SourceId, "source.xml", values.Length)],
                    values[0])]),
            (operationId, informationType, ordinal) => AcceptOccurrence(
                informationType,
                ordinal,
                values.Length,
                source.SourceId,
                values[ordinal - 1]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("First value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("1", viewModel.OccurrenceOrdinalInput);
        Assert.AreEqual("of 3", viewModel.OccurrenceTotalText);
        Assert.IsFalse(viewModel.PreviousOccurrenceCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextOccurrenceCommand.CanExecute(null));

        await viewModel.NextOccurrenceCommand.ExecuteAsync(null);
        await viewModel.NextOccurrenceCommand.ExecuteAsync(null);

        Assert.AreEqual("Third value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.IsFalse(viewModel.NextOccurrenceCommand.CanExecute(null));

        await viewModel.PreviousOccurrenceCommand.ExecuteAsync(null);

        Assert.AreEqual("Second value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(2, viewModel.CurrentOccurrenceOrdinal);
    }

    [TestMethod]
    public async Task SelectingTagImmediatelyShowsKnownTotalThenLoadsFirstOccurrence()
    {
        const int occurrenceCount = 202;
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new DeferredOccurrenceDiscoveryClient(source.SourceId, occurrenceCount);
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual("ecoef", viewModel.SelectedInformation?.InformationType);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.AreEqual("of 202", viewModel.OccurrenceTotalText);
        Assert.AreEqual("Loading occurrence...", viewModel.OccurrencePreviewText);
        Assert.IsTrue(viewModel.IsOccurrenceLoading);

        client.CompleteFirstOccurrence("Exact first value");
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("Exact first value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.IsFalse(viewModel.PreviousOccurrenceCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextOccurrenceCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task FirstOccurrenceFailureRetainsSelectedTagAndKnownTotal()
    {
        const int occurrenceCount = 202;
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "ecoef",
                    occurrenceCount,
                    [new DiscoveredSourceContribution(
                        source.SourceId,
                        "source.xml",
                        occurrenceCount)],
                    "sample")]),
            (_, _, _) => new DiscoveryOccurrenceClientResult(
                false,
                Occurrence: null,
                "discovery-preview-source-unavailable",
                "The occurrence preview could not be retrieved."));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("ecoef", viewModel.SelectedInformation?.InformationType);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.AreEqual("of 202", viewModel.OccurrenceTotalText);
        Assert.AreEqual(0, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(
            "The occurrence preview could not be retrieved.",
            viewModel.OccurrencePreviewText);
        Assert.AreNotEqual(
            "Select a discovered tag to inspect its occurrence value.",
            viewModel.OccurrencePreviewText);
    }

    [TestMethod]
    public async Task DirectOrdinalRejectsInvalidInputAndTagChangeLoadsFirstOccurrence()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Alpha"] = ["Alpha 1", "Alpha 2", "Alpha 3"],
            ["Beta"] = ["Beta 1", "Beta 2"]
        };
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                values.Select(pair => new DiscoveredInformation(
                    pair.Key,
                    pair.Value.Length,
                    [new DiscoveredSourceContribution(
                        source.SourceId,
                        "source.xml",
                        pair.Value.Length)],
                    pair.Value[0])).ToArray()),
            (operationId, informationType, ordinal) => AcceptOccurrence(
                informationType,
                ordinal,
                values[informationType].Length,
                source.SourceId,
                values[informationType][ordinal - 1]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        viewModel.OccurrenceOrdinalInput = "3";
        await AwaitOrdinalJumpAsync(viewModel);

        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        var requestsBeforeInvalidInput = client.OccurrenceCallCount;

        viewModel.OccurrenceOrdinalInput = "not-a-number";
        await AwaitOrdinalJumpAsync(viewModel);
        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("3", viewModel.OccurrenceOrdinalInput);

        viewModel.OccurrenceOrdinalInput = "4";
        await AwaitOrdinalJumpAsync(viewModel);
        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("3", viewModel.OccurrenceOrdinalInput);
        Assert.AreEqual(requestsBeforeInvalidInput, client.OccurrenceCallCount);

        viewModel.SelectedInformation = viewModel.Information.Single(
            information => information.InformationType == "Beta");
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("Beta 1", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(2, viewModel.OccurrenceTotal);
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
                        3,
                        [new DiscoveredSourceContribution(source.SourceId, "source.xml", 3)],
                        "retained value")])
                : Reject(correlation, sources, "processing-host-unavailable"));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);
        var selectedInformation = viewModel.SelectedInformation;
        var collectionChangeCount = 0;
        ((INotifyCollectionChanged)viewModel.Information).CollectionChanged +=
            (_, _) => collectionChangeCount++;

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("RetainedTag", viewModel.Information[0].InformationType);
        Assert.AreSame(selectedInformation, viewModel.SelectedInformation);
        Assert.AreEqual("retained value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(3, viewModel.OccurrenceTotal);
        Assert.AreEqual(0, collectionChangeCount);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        Assert.AreEqual("Discovery re-run failed", viewModel.StatusTitle);
        StringAssert.Contains(viewModel.StatusDetail, "Previous Discovery results are retained");
        Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);
    }

    [TestMethod]
    public async Task FailedReRunCannotTemporarilyPresentCurrentWithDeferredWorkflowNotifications()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var callCount = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => ++callCount == 1
                ? Accept(correlation, sources, CreateInformation(source.SourceId, "RetainedTag"))
                : Reject(correlation, sources, "processing-host-unavailable"));
        var queuedContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        DiscoveryWorkspaceViewModel viewModel;

        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            viewModel = new DiscoveryWorkspaceViewModel(
                client,
                new ActiveDiscoveryConfiguration(),
                sourceSet,
                workflow);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            queuedContext.Drain();
            Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);

            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

            Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
            Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
            Assert.AreEqual("Discovery re-run failed", viewModel.StatusTitle);
            Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);

            queuedContext.Drain();
            Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        }
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

    private static DiscoveryOccurrenceClientResult AcceptOccurrence(
        string informationType,
        int ordinal,
        int totalOccurrenceCount,
        SourceId sourceId,
        string value)
    {
        return new DiscoveryOccurrenceClientResult(
            true,
            new DiscoveredOccurrence(
                informationType,
                ordinal,
                totalOccurrenceCount,
                sourceId,
                value),
            FailureCode: null,
            FailureDescription: null);
    }

    private static async Task AwaitSelectedOccurrenceAsync(
        DiscoveryWorkspaceViewModel viewModel)
    {
        if (viewModel.SelectInformationCommand.ExecutionTask is { } task)
        {
            await task;
        }
    }

    private static async Task AwaitOrdinalJumpAsync(DiscoveryWorkspaceViewModel viewModel)
    {
        await viewModel.JumpToOccurrenceCommand.ExecuteAsync(null);
    }

    private sealed class StubDiscoveryClient : IDiscoveryClient
    {
        private readonly Func<
            OperationCorrelation,
            IReadOnlyList<LoadedSourceContract>,
            DiscoveryClientResult> _resultFactory;
        private readonly Func<
            OperationId,
            string,
            int,
            DiscoveryOccurrenceClientResult>? _occurrenceResultFactory;
        private IReadOnlyList<DiscoveredInformation> _lastInformation = [];

        public StubDiscoveryClient(
            Func<
                OperationCorrelation,
                IReadOnlyList<LoadedSourceContract>,
                DiscoveryClientResult> resultFactory,
            Func<
                OperationId,
                string,
                int,
                DiscoveryOccurrenceClientResult>? occurrenceResultFactory = null)
        {
            _resultFactory = resultFactory;
            _occurrenceResultFactory = occurrenceResultFactory;
        }

        public IReadOnlyList<LoadedSourceContract> LastSources { get; private set; } = [];

        public int CallCount { get; private set; }

        public int OccurrenceCallCount { get; private set; }

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSources = sources;
            var result = _resultFactory(correlation, sources);
            _lastInformation = result.Information;
            return Task.FromResult(result);
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            OperationId discoveryOperationId,
            string informationType,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            OccurrenceCallCount++;

            if (_occurrenceResultFactory is not null)
            {
                return Task.FromResult(_occurrenceResultFactory(
                    discoveryOperationId,
                    informationType,
                    ordinal));
            }

            var information = _lastInformation.Single(
                item => item.InformationType == informationType);
            return Task.FromResult(AcceptOccurrence(
                informationType,
                ordinal,
                information.TotalOccurrenceCount,
                information.ContributingSources[0].SourceId,
                ordinal == 1
                    ? information.SampleValue
                    : $"{informationType} occurrence {ordinal}"));
        }
    }

    private sealed class DeferredOccurrenceDiscoveryClient(
        SourceId sourceId,
        int occurrenceCount) : IDiscoveryClient
    {
        private readonly TaskCompletionSource<DiscoveryOccurrenceClientResult> _firstOccurrence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "ecoef",
                    occurrenceCount,
                    [new DiscoveredSourceContribution(sourceId, "source.xml", occurrenceCount)],
                    "sample")]));
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            OperationId discoveryOperationId,
            string informationType,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ecoef", informationType);
            Assert.AreEqual(1, ordinal);
            return _firstOccurrence.Task;
        }

        public void CompleteFirstOccurrence(string value)
        {
            _firstOccurrence.SetResult(AcceptOccurrence(
                "ecoef",
                1,
                occurrenceCount,
                sourceId,
                value));
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
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
