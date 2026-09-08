using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DatabaseBuildCoordinatorTests
{
    [TestMethod]
    public async Task FirstSuccessfulBuildPublishesCapturedGenerationAndWorkspaceState()
    {
        var context = await BuildContext.CreateAsync();
        var client = new RecordingDatabaseClient((correlation, _, mapping) =>
            Success(correlation, mapping, valueCount: 3));
        var coordinator = context.CreateCoordinator(client);
        using var database = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator);

        var result = await coordinator.BuildAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);
        Assert.AreEqual(client.Correlation?.OperationId, coordinator.CurrentGeneration?.OperationId);
        Assert.HasCount(1, database.Columns);
        Assert.AreEqual("DatabaseName", database.Columns[0].DatabaseField);
        Assert.AreEqual("3 mapped values", database.RecordCountText);
    }

    [TestMethod]
    public async Task InFlightBuildUsesSnapshotAndIsNotAuthoritativeBeforeResponse()
    {
        var context = await BuildContext.CreateAsync();
        var response = new TaskCompletionSource<DatabaseClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseMappingSnapshot? capturedMapping = null;
        var client = new RecordingDatabaseClient(async (correlation, _, mapping) =>
        {
            capturedMapping = mapping;
            return await response.Task;
        });
        var coordinator = context.CreateCoordinator(client);

        var buildTask = coordinator.BuildAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);

        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "ChangedLater"));
        response.SetResult(Success(client.Correlation!, capturedMapping!, valueCount: 1));
        Assert.IsTrue((await buildTask).Accepted);

        Assert.AreEqual("DatabaseName", capturedMapping!.Columns.Single().DatabaseTagName);
        Assert.AreEqual(
            "DatabaseName",
            coordinator.CurrentGeneration!.Mapping.Columns.Single().DatabaseTagName);
    }

    [TestMethod]
    public async Task FailedAndCancelledAttemptsNeverReplacePublishedGeneration()
    {
        var context = await BuildContext.CreateAsync();
        var outcomes = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled
        ]);
        var client = new RecordingDatabaseClient((correlation, sources, mapping) =>
        {
            var outcome = outcomes.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? Success(correlation, mapping, valueCount: 1)
                : Failure(correlation, sources, outcome);
        });
        var coordinator = context.CreateCoordinator(client);
        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        var published = coordinator.CurrentGeneration;

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "Replacement"));
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreSame(published, coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);

        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreSame(published, coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task EscCancellationTargetsOnlyActiveDatabaseOperationCorrelation()
    {
        var supervisor = new TrackingProcessingHostSupervisor();
        var context = await BuildContext.CreateAsync(supervisor);
        var response = new TaskCompletionSource<DatabaseClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingDatabaseClient(async (_, _, _) => await response.Task);
        var coordinator = context.CreateCoordinator(client);

        var buildTask = coordinator.BuildAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = context.Workflow.Current.ActiveOperation;
        Assert.IsNotNull(active);

        var cancellation = await context.Workflow.RequestCancellationAsync();
        Assert.IsTrue(cancellation.Accepted);
        Assert.AreEqual(active.Correlation.OperationId, supervisor.CancelledOperationId);
        response.SetResult(Failure(
            active.Correlation,
            context.Sources.CreateIncludedReadySnapshot(),
            OperationOutcome.Cancelled));
        await buildTask;

        Assert.AreEqual(WorkflowOperationState.Cancelled, context.Workflow.Current.LatestOperation?.State);
        Assert.IsNull(coordinator.CurrentGeneration);
    }

    [TestMethod]
    public async Task FailedOrCancelledFirstBuildLeavesDatabaseUnavailable()
    {
        foreach (var outcome in new[] { OperationOutcome.Failed, OperationOutcome.Cancelled })
        {
            var context = await BuildContext.CreateAsync();
            var client = new RecordingDatabaseClient((correlation, sources, _) =>
                Failure(correlation, sources, outcome));
            var coordinator = context.CreateCoordinator(client);

            Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
            Assert.IsNull(coordinator.CurrentGeneration);
            Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);
        }
    }

    [TestMethod]
    public async Task PublishedReviewAlignsIndependentColumnsAndExposesPerValueProvenance()
    {
        var context = await BuildContext.CreateAsync(
            informationTypes: ["alpha", "beta", "gamma"]);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("alpha", "Combined"));
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("beta", "Combined"));
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("gamma", "Gamma override"));
        var databaseClient = new RecordingDatabaseClient((correlation, _, mapping) =>
            Success(correlation, mapping, valueCount: 7));
        var firstSourceId = SourceId.CreateNew();
        var secondSourceId = SourceId.CreateNew();
        var reviewClient = new RecordingDatabaseReviewClient((generationId, start, count) =>
            AcceptedReview(
                generationId,
                start,
                count,
                totalMappedValueCount: 7,
                new DatabaseReviewColumn(
                    "Combined",
                    totalValueCount: 4,
                    [
                        new DatabaseReviewValue(1, "A1", "alpha", firstSourceId),
                        new DatabaseReviewValue(2, "A2", "alpha", firstSourceId),
                        new DatabaseReviewValue(3, "B1", "beta", firstSourceId),
                        new DatabaseReviewValue(4, "B2", "beta", secondSourceId)
                    ]),
                new DatabaseReviewColumn(
                    "Gamma override",
                    totalValueCount: 3,
                    [
                        new DatabaseReviewValue(1, "G1", "gamma", firstSourceId),
                        new DatabaseReviewValue(2, "G2", "gamma", secondSourceId),
                        new DatabaseReviewValue(3, "G3", "gamma", secondSourceId)
                    ])));
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 4);

        CollectionAssert.AreEqual(
            new[] { "Combined", "Gamma override" },
            viewModel.VisibleColumns.Select(column => column.DatabaseField).ToArray());
        Assert.AreEqual("A1", viewModel.Records[0].Cells[0].DisplayValue);
        Assert.AreEqual("G1", viewModel.Records[0].Cells[1].DisplayValue);
        Assert.AreEqual("B2", viewModel.Records[3].Cells[0].DisplayValue);
        Assert.AreEqual(string.Empty, viewModel.Records[3].Cells[1].DisplayValue);
        StringAssert.Contains(viewModel.Records[0].Cells[0].SourceContext!, "alpha");
        StringAssert.Contains(
            viewModel.Records[0].Cells[0].SourceContext!,
            firstSourceId.ToString());
        Assert.AreEqual("7 mapped values", viewModel.RecordCountText);

        var combined = viewModel.Columns.Single(column => column.DatabaseField == "Combined");
        combined.Width = 280;
        viewModel.MoveColumnDownCommand.Execute(combined);
        viewModel.Columns.Single(column => column.DatabaseField == "Gamma override").IsVisible =
            false;
        Assert.HasCount(1, reviewClient.Requests);
        Assert.IsTrue(viewModel.Records.All(row => row.Cells.Count == 1));
        CollectionAssert.AreEqual(
            new[] { "A1", "A2", "B1", "B2" },
            viewModel.Records.Select(row => row.Cells[0].DisplayValue).ToArray());
    }

    [TestMethod]
    public async Task ReviewUsesBoundedPagesInsteadOfLoadingThePublishedDatabaseAtOnce()
    {
        var context = await BuildContext.CreateAsync();
        var databaseClient = new RecordingDatabaseClient((correlation, _, mapping) =>
            Success(correlation, mapping, valueCount: 150));
        var sourceId = SourceId.CreateNew();
        var reviewClient = new RecordingDatabaseReviewClient((generationId, start, count) =>
        {
            var last = Math.Min(150, start + count - 1);
            var values = Enumerable.Range(start, last - start + 1)
                .Select(ordinal => new DatabaseReviewValue(
                    ordinal,
                    $"Value {ordinal}",
                    "tag",
                    sourceId))
                .ToArray();
            return AcceptedReview(
                generationId,
                start,
                count,
                totalMappedValueCount: 150,
                new DatabaseReviewColumn("DatabaseName", 150, values));
        });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 100);
        Assert.AreEqual(DatabaseReviewLimits.MaximumRowsPerPage, reviewClient.Requests[0].RowCount);
        Assert.AreEqual(1, viewModel.Records[0].Ordinal);
        Assert.AreEqual("Page 1 of 2", viewModel.ReviewPageText);

        await viewModel.NextReviewPageCommand.ExecuteAsync(null);
        await WaitForAsync(() => viewModel.Records.FirstOrDefault()?.Ordinal == 101);
        Assert.HasCount(50, viewModel.Records);
        Assert.AreEqual("Value 150", viewModel.Records[^1].Cells[0].DisplayValue);
        Assert.AreEqual("Page 2 of 2", viewModel.ReviewPageText);
        Assert.HasCount(2, reviewClient.Requests);
    }

    [TestMethod]
    public async Task StaleFailedAndCancelledBuildsRetainReviewUntilSuccessfulReplacement()
    {
        var context = await BuildContext.CreateAsync();
        var outcomes = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled,
            OperationOutcome.CompletedSuccessfully
        ]);
        var databaseClient = new RecordingDatabaseClient((correlation, sources, mapping) =>
        {
            var outcome = outcomes.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? Success(correlation, mapping, valueCount: 1)
                : Failure(correlation, sources, outcome);
        });
        var sourceId = SourceId.CreateNew();
        OperationId? firstGenerationId = null;
        var reviewClient = new RecordingDatabaseReviewClient((generationId, start, count) =>
        {
            var replacement = firstGenerationId is not null
                && generationId != firstGenerationId;
            var databaseTagName = replacement ? "Replacement" : "DatabaseName";
            var value = replacement ? "replacement value" : "original value";
            return AcceptedReview(
                generationId,
                start,
                count,
                totalMappedValueCount: 1,
                new DatabaseReviewColumn(
                    databaseTagName,
                    1,
                    [new DatabaseReviewValue(1, value, "tag", sourceId)]));
        });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 1);
        firstGenerationId = coordinator.CurrentGeneration!.OperationId;
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "Replacement"));
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() =>
            viewModel.Records.FirstOrDefault()?.Cells[0].DisplayValue == "replacement value");
        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        Assert.AreEqual("Replacement", viewModel.Columns.Single().DatabaseField);
    }

    private static DatabaseReviewClientResult AcceptedReview(
        OperationId generationId,
        int startRowOrdinal,
        int requestedRowCount,
        int totalMappedValueCount,
        params DatabaseReviewColumn[] columns)
    {
        return new DatabaseReviewClientResult(
            true,
            new DatabaseReviewPage(
                generationId,
                startRowOrdinal,
                requestedRowCount,
                totalMappedValueCount,
                columns),
            FailureCode: null,
            FailureDescription: null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static DatabaseClientResult Success(
        OperationCorrelation correlation,
        DatabaseMappingSnapshot mapping,
        int valueCount)
    {
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [OperationItemStatus.ProcessedSuccessfully("source")]);
        return new DatabaseClientResult(
            true,
            completion,
            new DatabaseGenerationSummary(correlation.OperationId, mapping, valueCount),
            FailureCode: null,
            FailureDescription: null);
    }

    private static DatabaseClientResult Failure(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        OperationOutcome outcome)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            outcome,
            sources.Select(source => OperationItemStatus.Unprocessed(
                source.SourceId.ToString(),
                "not-published")).ToArray());
        return new DatabaseClientResult(
            false,
            completion,
            PublishedGeneration: null,
            "not-published",
            "The candidate was not published.");
    }

    private sealed class RecordingDatabaseClient : IDatabaseClient
    {
        private readonly Func<
            OperationCorrelation,
            IReadOnlyList<LoadedSourceContract>,
            DatabaseMappingSnapshot,
            Task<DatabaseClientResult>> _build;

        public RecordingDatabaseClient(
            Func<
                OperationCorrelation,
                IReadOnlyList<LoadedSourceContract>,
                DatabaseMappingSnapshot,
                DatabaseClientResult> build)
            : this((correlation, sources, mapping) =>
                Task.FromResult(build(correlation, sources, mapping)))
        {
        }

        public RecordingDatabaseClient(
            Func<
                OperationCorrelation,
                IReadOnlyList<LoadedSourceContract>,
                DatabaseMappingSnapshot,
                Task<DatabaseClientResult>> build)
        {
            _build = build;
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCorrelation? Correlation { get; private set; }

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping,
            CancellationToken cancellationToken = default)
        {
            Correlation = correlation;
            Started.TrySetResult();
            return _build(correlation, sources, mapping);
        }
    }

    private sealed class RecordingDatabaseReviewClient(
        Func<OperationId, int, int, DatabaseReviewClientResult> readPage)
        : IDatabaseReviewClient
    {
        public List<(OperationId GenerationId, int StartRowOrdinal, int RowCount)> Requests
        {
            get;
        } = [];

        public Task<DatabaseReviewClientResult> ReadPageAsync(
            OperationId generationId,
            int startRowOrdinal,
            int rowCount,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((generationId, startRowOrdinal, rowCount));
            return Task.FromResult(readPage(generationId, startRowOrdinal, rowCount));
        }
    }

    private sealed class BuildContext
    {
        private BuildContext(
            ActiveDiscoveryConfiguration configuration,
            ActiveLoadedSourceSet sources,
            ApplicationWorkflowCoordinator workflow)
        {
            Configuration = configuration;
            Sources = sources;
            Workflow = workflow;
        }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ActiveLoadedSourceSet Sources { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public static async Task<BuildContext> CreateAsync(
            IProcessingHostSupervisor? supervisor = null,
            IReadOnlyList<string>? informationTypes = null)
        {
            informationTypes ??= ["tag"];
            var configuration = new ActiveDiscoveryConfiguration();
            configuration.Synchronize(informationTypes);
            configuration.SetSelection(informationTypes, isSelected: true);
            if (informationTypes.SequenceEqual(["tag"], StringComparer.Ordinal))
            {
                configuration.SetDatabaseTagOverride("tag", "DatabaseName");
            }
            var sources = new ActiveLoadedSourceSet();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var workflow = new ApplicationWorkflowCoordinator(
                supervisor ?? new TrackingProcessingHostSupervisor(),
                new RecordingProcessingHistoryRecorder());
            var loading = new SourceLoadingCoordinator(
                new StaticSourceIntakeClient(source),
                sources,
                workflow);
            Assert.IsTrue((await loading.AddAsync(
                SourceSelectionKind.XmlFile,
                source.Path)).Accepted);
            var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
            Assert.IsTrue(discovery.Accepted);
            Assert.IsTrue(workflow.CompleteOperation(
                discovery.Operation!.OperationId,
                OperationOutcome.CompletedSuccessfully).Accepted);
            return new BuildContext(configuration, sources, workflow);
        }

        public DatabaseBuildCoordinator CreateCoordinator(IDatabaseClient client)
        {
            return new DatabaseBuildCoordinator(
                Configuration,
                Sources,
                Workflow,
                client,
                NullLogger<DatabaseBuildCoordinator>.Instance);
        }
    }

    private sealed class StaticSourceIntakeClient(LoadedSourceContract source)
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
            LoadedSourceContract refreshedSource,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceRefreshClientResult(
                true,
                refreshedSource,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class TrackingProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public OperationId? CancelledOperationId { get; private set; }

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
            CancelledOperationId = operationId;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
