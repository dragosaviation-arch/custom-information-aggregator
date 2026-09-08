using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Extraction;
using CIA.Desktop.Hosting;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ExtractionCoordinatorTests
{
    [TestMethod]
    public async Task SuccessfulExtractionUsesCapturedDatabaseAndBecomesCurrent()
    {
        var context = await ExtractionContext.CreateAsync();
        var database = await context.BuildCurrentDatabaseAsync(valueCount: 3);
        var client = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var coordinator = context.CreateExtractionCoordinator(client);

        var result = await coordinator.ExtractAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.AreEqual(database, client.DatabaseGeneration);
        Assert.AreEqual(database, coordinator.CurrentResult?.DatabaseGeneration);
        Assert.AreEqual(client.Correlation?.OperationId, coordinator.CurrentResult?.OperationId);
        Assert.IsFalse(typeof(ExtractionResultSummary).GetProperties().Any(property =>
            property.Name.Contains("Visible", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Width", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Order", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Row", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task InFlightExtractionKeepsItsCapturedGenerationAndCorrelation()
    {
        var context = await ExtractionContext.CreateAsync();
        var database = await context.BuildCurrentDatabaseAsync(valueCount: 2);
        var response = new TaskCompletionSource<ExtractionClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingExtractionClient(async (_, _) => await response.Task);
        var coordinator = context.CreateExtractionCoordinator(client);

        var extractionTask = coordinator.ExtractAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(database, client.DatabaseGeneration);
        Assert.IsFalse((await context.DatabaseCoordinator.BuildAsync()).Accepted);
        Assert.AreEqual(database, client.DatabaseGeneration);

        response.SetResult(Success(client.Correlation!, database));
        Assert.IsTrue((await extractionTask).Accepted);
        Assert.AreEqual(database.OperationId, coordinator.CurrentResult?.DatabaseGeneration.OperationId);
    }

    [TestMethod]
    public async Task StaleDatabaseIsRejectedAndFailedReplacementsPreservePriorResult()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var results = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled
        ]);
        var client = new RecordingExtractionClient((correlation, basis) =>
        {
            var outcome = results.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? Success(correlation, basis)
                : Failure(correlation, outcome);
        });
        var coordinator = context.CreateExtractionCoordinator(client);
        Assert.IsTrue((await coordinator.ExtractAsync()).Accepted);
        var retained = coordinator.CurrentResult;

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        var staleRejection = await coordinator.ExtractAsync();
        Assert.IsFalse(staleRejection.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.DatabaseNotCurrent, staleRejection.Rejection?.Code);
        Assert.AreSame(retained, coordinator.CurrentResult);

        await context.BuildCurrentDatabaseAsync();
        Assert.IsFalse((await coordinator.ExtractAsync()).Accepted);
        Assert.AreSame(retained, coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Extraction);

        Assert.IsFalse((await coordinator.ExtractAsync()).Accepted);
        Assert.AreSame(retained, coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Extraction);
    }

    [TestMethod]
    public async Task EscCancellationTargetsOnlyTheActiveExtractionOperation()
    {
        var supervisor = new TrackingProcessingHostSupervisor();
        var context = await ExtractionContext.CreateAsync(supervisor);
        await context.BuildCurrentDatabaseAsync();
        var response = new TaskCompletionSource<ExtractionClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingExtractionClient(async (_, _) => await response.Task);
        var coordinator = context.CreateExtractionCoordinator(client);

        var extractionTask = coordinator.ExtractAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activeOperationId = context.Workflow.Current.ActiveOperation!.Correlation.OperationId;
        Assert.IsTrue((await context.Workflow.RequestCancellationAsync()).Accepted);
        Assert.AreEqual(activeOperationId, supervisor.CancelledOperationId);

        response.SetResult(Failure(client.Correlation!, OperationOutcome.Cancelled));
        Assert.IsFalse((await extractionTask).Accepted);
        Assert.IsNull(coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowOperationState.Cancelled, context.Workflow.Current.LatestOperation?.State);
    }

    private static ExtractionClientResult Success(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration)
    {
        return new ExtractionClientResult(
            true,
            OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully("extraction-publication")]),
            new ExtractionResultSummary(correlation.OperationId, databaseGeneration),
            FailureCode: null,
            FailureDescription: null);
    }

    private static ExtractionClientResult Failure(
        OperationCorrelation correlation,
        OperationOutcome outcome)
    {
        return new ExtractionClientResult(
            false,
            OperationCompletion.FromTerminalOutcome(
                correlation,
                outcome,
                [OperationItemStatus.Unprocessed("extraction-publication", "not-published")]),
            PublishedResult: null,
            "not-published",
            "No Extraction Result was published.");
    }

    private sealed class RecordingExtractionClient : IExtractionClient
    {
        private readonly Func<
            OperationCorrelation,
            DatabaseGenerationSummary,
            Task<ExtractionClientResult>> _extract;

        public RecordingExtractionClient(
            Func<OperationCorrelation, DatabaseGenerationSummary, ExtractionClientResult> extract)
            : this((correlation, database) => Task.FromResult(extract(correlation, database)))
        {
        }

        public RecordingExtractionClient(
            Func<OperationCorrelation, DatabaseGenerationSummary, Task<ExtractionClientResult>> extract)
        {
            _extract = extract;
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCorrelation? Correlation { get; private set; }

        public DatabaseGenerationSummary? DatabaseGeneration { get; private set; }

        public Task<ExtractionClientResult> ExtractAsync(
            OperationCorrelation correlation,
            DatabaseGenerationSummary databaseGeneration,
            CancellationToken cancellationToken = default)
        {
            Correlation = correlation;
            DatabaseGeneration = databaseGeneration;
            Started.TrySetResult();
            return _extract(correlation, databaseGeneration);
        }
    }

    private sealed class ExtractionContext
    {
        private ExtractionContext(
            ActiveDiscoveryConfiguration configuration,
            ActiveLoadedSourceSet sources,
            ApplicationWorkflowCoordinator workflow,
            DatabaseBuildCoordinator databaseCoordinator,
            ConfigurableDatabaseClient databaseClient)
        {
            Configuration = configuration;
            Sources = sources;
            Workflow = workflow;
            DatabaseCoordinator = databaseCoordinator;
            DatabaseClient = databaseClient;
        }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ActiveLoadedSourceSet Sources { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public DatabaseBuildCoordinator DatabaseCoordinator { get; }

        private ConfigurableDatabaseClient DatabaseClient { get; }

        public static async Task<ExtractionContext> CreateAsync(
            IProcessingHostSupervisor? supervisor = null)
        {
            var configuration = new ActiveDiscoveryConfiguration();
            configuration.Synchronize(["tag"]);
            configuration.SetSelection(["tag"], isSelected: true);
            configuration.SetDatabaseTagOverride("tag", "Database Field");
            var sources = new ActiveLoadedSourceSet();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var workflow = new ApplicationWorkflowCoordinator(
                supervisor ?? new TrackingProcessingHostSupervisor(),
                new RecordingHistory());
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

            var databaseClient = new ConfigurableDatabaseClient();
            var databaseCoordinator = new DatabaseBuildCoordinator(
                configuration,
                sources,
                workflow,
                databaseClient,
                NullLogger<DatabaseBuildCoordinator>.Instance);
            return new ExtractionContext(
                configuration,
                sources,
                workflow,
                databaseCoordinator,
                databaseClient);
        }

        public async Task<DatabaseGenerationSummary> BuildCurrentDatabaseAsync(int valueCount = 1)
        {
            DatabaseClient.ValueCount = valueCount;
            Assert.IsTrue((await DatabaseCoordinator.BuildAsync()).Accepted);
            return DatabaseCoordinator.CurrentGeneration!;
        }

        public ExtractionCoordinator CreateExtractionCoordinator(IExtractionClient client)
        {
            return new ExtractionCoordinator(
                DatabaseCoordinator,
                Workflow,
                client,
                NullLogger<ExtractionCoordinator>.Instance);
        }
    }

    private sealed class ConfigurableDatabaseClient : IDatabaseClient
    {
        public int ValueCount { get; set; } = 1;

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            DatabaseMappingSnapshot mapping,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DatabaseClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully("source")]),
                new DatabaseGenerationSummary(
                    correlation.OperationId,
                    mapping,
                    ValueCount),
                FailureCode: null,
                FailureDescription: null));
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

    private sealed class RecordingHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }
}
