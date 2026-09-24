using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CIA.Desktop.WorkingState;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class WorkingStateCoordinatorTests
{
    [TestMethod]
    public async Task CurrentDatabaseSaveSendsCoherentSnapshotToHost()
    {
        var prepared = await CreateCurrentDatabaseContextAsync();

        Assert.IsTrue(prepared.Context.Coordinator.EvaluateSaveReadiness().NormalOperationReady);
        var result = await prepared.Context.Coordinator.SaveAsync("C:\\saved\\current.cia");

        Assert.IsTrue(result.Accepted, result.FailureDescription);
        Assert.AreEqual(1, prepared.Context.Client.SaveCallCount);
        Assert.AreEqual(
            WorkingStatePublicationMode.ReplaceExisting,
            prepared.Context.Client.LastPublicationMode);
        Assert.IsNotNull(prepared.Context.Client.LastSavedSnapshot?.DatabaseGeneration);
        Assert.AreEqual(
            WorkflowArtifactStatus.Current,
            prepared.Context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task NoPublishedDatabaseSaveSendsDatabaseLessSnapshotToHost()
    {
        var context = CreateContext();

        Assert.IsTrue(context.Coordinator.EvaluateSaveReadiness().NormalOperationReady);
        var result = await context.Coordinator.SaveAsync("C:\\saved\\empty.cia");

        Assert.IsTrue(result.Accepted, result.FailureDescription);
        Assert.AreEqual(1, context.Client.SaveCallCount);
        Assert.IsNull(context.Client.LastSavedSnapshot?.DatabaseGeneration);
    }

    [TestMethod]
    public async Task DatabaseReviewChangeLeavesCurrentDatabaseSaveable()
    {
        var prepared = await CreateCurrentDatabaseContextAsync();

        Assert.IsTrue(prepared.Context.Workflow.RecordDatabaseReviewChanged().Accepted);
        var result = await prepared.Context.Coordinator.SaveAsync("C:\\saved\\reviewed.cia");

        Assert.AreEqual(WorkflowArtifactStatus.Current, prepared.Context.Workflow.Current.Database);
        Assert.IsTrue(result.Accepted, result.FailureDescription);
        Assert.AreEqual(1, prepared.Context.Client.SaveCallCount);
    }

    [TestMethod]
    public async Task SettingsManagedLibrarySavesRestoresAndDeletesAuthoritativeWorkingBoundary()
    {
        var prepared = await CreateCurrentDatabaseContextAsync();
        using var environment = new TemporaryManagedStateDirectory();
        var library = new SavedWorkingStateLibrary(environment.Paths);
        prepared.Context.Client.PersistSaveTargets = true;
        var viewModel = new SettingsWorkspaceViewModel(
            new EmptyHistoryReader(),
            new SettingsWorkspaceRuntimePaths(environment.Paths, environment.Paths.LogsDirectory),
            savedStateLibrary: library,
            workingStateCoordinator: prepared.Context.Coordinator,
            workflowCoordinator: prepared.Context.Workflow,
            deleteConfirmation: new AcceptedDeleteConfirmation());
        viewModel.SavedStateName = "Current boundary";

        await viewModel.SaveStateCommand.ExecuteAsync(null);
        var savedPath = viewModel.SelectedSavedState?.Path;
        var savedSnapshot = prepared.Context.Client.LastSavedSnapshot;
        Assert.IsNotNull(savedSnapshot);
        Assert.IsNotNull(savedPath);
        Assert.IsTrue(File.Exists(savedPath));
        Assert.HasCount(1, viewModel.SavedStates);

        var source = prepared.Context.Sources.Items.Single();
        Assert.IsTrue(prepared.Loading.SetInclusion([source], isIncluded: false).Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, prepared.Context.Workflow.Current.Database);
        Assert.IsFalse(prepared.Context.Sources.Items.Single().IsIncluded);

        await viewModel.RestoreStateCommand.ExecuteAsync(null);

        Assert.AreEqual(1, prepared.Context.Client.RestoreCallCount);
        Assert.IsTrue(prepared.Context.Sources.Items.Single().IsIncluded);
        Assert.AreEqual(
            savedSnapshot.DatabaseGeneration,
            prepared.Context.Database.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Current, prepared.Context.Workflow.Current.Database);
        Assert.IsTrue(File.Exists(savedPath));
        Assert.HasCount(1, viewModel.SavedStates);

        await viewModel.DeleteStateCommand.ExecuteAsync(null);

        Assert.IsFalse(File.Exists(savedPath));
        Assert.IsEmpty(viewModel.SavedStates);
    }

    [TestMethod]
    public async Task SourceInclusionChangeMakesDatabaseStaleAndRejectsSaveBeforeHostRequest()
    {
        var prepared = await CreateCurrentDatabaseContextAsync();
        using var target = new TemporaryPackageFile();
        var original = await File.ReadAllBytesAsync(target.Path);

        var change = prepared.Loading.SetInclusion(
            [prepared.Context.Sources.Items.Single()],
            false);
        var readiness = prepared.Context.Coordinator.EvaluateSaveReadiness();
        var result = await prepared.Context.Coordinator.SaveAsync(target.Path);

        Assert.IsTrue(change.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, prepared.Context.Workflow.Current.Database);
        Assert.IsTrue(readiness.WorkflowPrerequisitesSatisfied);
        Assert.IsFalse(readiness.NormalOperationReady);
        StringAssert.Contains(readiness.UnavailableReason, "Update or rebuild");
        Assert.IsFalse(result.Accepted);
        StringAssert.Contains(result.FailureDescription, "Update or rebuild");
        Assert.AreEqual(0, prepared.Context.Client.SaveCallCount);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(target.Path));
    }

    [TestMethod]
    public async Task InterruptedSaveRecoveryUsesTheSameStaleDatabaseGuardAsNormalSave()
    {
        var prepared = await CreateCurrentDatabaseContextAsync();
        Assert.IsTrue(prepared.Context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        var sharedReadiness = prepared.Context.Coordinator.EvaluateSaveReadiness();
        var start = new ProcessingOperationStartRecord(
            OperationCorrelation.CreateNew(DateTimeOffset.UtcNow.AddMinutes(-2)),
            WorkflowOperationKind.WorkingStateSave.ToString(),
            DateTimeOffset.UtcNow.AddMinutes(-2));
        var history = new RecoveryHistoryStore(start);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            history,
            history,
            prepared.Context.Workflow,
            new SaveOperationReadiness(prepared.Context.Coordinator));

        recovery.ReconcileStartupHistory();

        Assert.IsFalse(sharedReadiness.NormalOperationReady);
        Assert.IsTrue(recovery.Current?.WorkflowPrerequisitesSatisfied);
        Assert.IsFalse(recovery.Current?.NormalOperationReady);
        Assert.AreEqual(sharedReadiness.UnavailableReason, recovery.Current?.UnavailableReason);
        Assert.AreEqual(0, prepared.Context.Client.SaveCallCount);
        Assert.IsNull(prepared.Context.Workflow.Current.ActiveOperation);
    }

    [TestMethod]
    [DataRow("selection")]
    [DataRow("override")]
    [DataRow("layout")]
    public async Task DiscoveryConfigurationChangeMakesDatabaseStaleAndRejectsSaveBeforeHostRequest(
        string changeKind)
    {
        var prepared = await CreateCurrentDatabaseContextAsync();

        Assert.IsTrue(prepared.Context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        switch (changeKind)
        {
            case "selection":
                Assert.AreEqual(1, prepared.Context.Configuration.SetSelection([prepared.Identity], false));
                break;
            case "override":
                Assert.IsTrue(prepared.Context.Configuration.SetDatabaseTagOverride(
                    prepared.Identity,
                    "ChangedCode"));
                break;
            case "layout":
                Assert.IsTrue(prepared.Context.Configuration.SetRepeatedDataLayout(
                    prepared.Identity.SourceSetId,
                    RepeatedDataLayout.StructuralRows));
                break;
            default:
                Assert.Fail($"Unsupported test change {changeKind}.");
                break;
        }

        var result = await prepared.Context.Coordinator.SaveAsync("C:\\saved\\stale.cia");

        Assert.AreEqual(WorkflowArtifactStatus.Stale, prepared.Context.Workflow.Current.Database);
        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(0, prepared.Context.Client.SaveCallCount);
    }

    [TestMethod]
    public async Task RestoreRehydratesStableIdentityAndTruthfulWorkflowState()
    {
        var context = CreateContext();
        var restoredSet = SourceSetId.CreateNew();
        var sourceId = SourceId.CreateNew();
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "missing.xml");
        var identity = new DiscoveryInformationIdentity(restoredSet, "/records/code", "code");
        var generation = CreateGeneration(restoredSet, identity);
        var snapshot = new WorkingStateSnapshot(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            generation,
            [new WorkingStateSourceSet(restoredSet, "Restored set", 1)],
            restoredSet,
            [new LoadedSourceContract(
                sourceId,
                restoredSet,
                missingPath,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile)],
            new DiscoveryConfigurationSnapshot(
                [new DiscoveryConfigurationItem(identity, DiscoveryInformationDisposition.Selected)],
                [new SourceSetDiscoveryConfiguration(restoredSet, RepeatedDataLayout.StructuralRows)]),
            [new WorkingStateDatabaseTagOverride(identity, "RestoredCode")]);
        context.Client.RestoreManifest = CreateManifest(snapshot);

        var result = await context.Coordinator.RestoreAsync("C:\\saved\\state.cia");

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(restoredSet, context.Sources.ActiveSourceSet?.SourceSetId);
        Assert.AreEqual("Restored set", context.Sources.ActiveSourceSet?.Name);
        Assert.HasCount(1, context.Sources.Items);
        Assert.AreEqual(sourceId, context.Sources.Items[0].SourceId);
        Assert.AreEqual(LoadedSourceStatus.Unavailable, context.Sources.Items[0].Status);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            context.Configuration.Current.Items.Single().Disposition);
        Assert.AreEqual("RestoredCode", context.Configuration.DatabaseTagOverridesByIdentity[identity]);
        Assert.AreEqual(
            RepeatedDataLayout.StructuralRows,
            context.Configuration.RepeatedDataLayouts[restoredSet]);
        Assert.AreSame(generation, context.Database.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Extraction);
        Assert.IsFalse(context.Workflow.Current.HasValidSourceSelection);
    }

    [TestMethod]
    public async Task ConflictingOperationRejectsRestoreBeforeHostRequest()
    {
        var context = CreateContext();
        var active = await context.Workflow.BeginOperationAsync(WorkflowOperationKind.WorkingStateSave);
        Assert.IsTrue(active.Accepted);

        var result = await context.Coordinator.RestoreAsync("C:\\saved\\state.cia");

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(0, context.Client.RestoreCallCount);
        Assert.AreEqual(WorkflowOperationKind.WorkingStateSave, context.Workflow.Current.ActiveOperation?.Kind);
    }

    [TestMethod]
    public void RestoreReadinessRequiresPackageSelectionWithoutClaimingExecutableReadiness()
    {
        var context = CreateContext();

        var withoutSelection = context.Coordinator.EvaluateRestoreReadiness(packagePath: null);
        var withSelection = context.Coordinator.EvaluateRestoreReadiness("C:\\saved\\state.cia");

        Assert.IsTrue(withoutSelection.WorkflowPrerequisitesSatisfied);
        Assert.IsFalse(withoutSelection.NormalOperationReady);
        Assert.IsTrue(withoutSelection.AdditionalUserInputRequired);
        StringAssert.Contains(withoutSelection.UnavailableReason, "Select a .cia");
        Assert.IsTrue(withSelection.NormalOperationReady);
        Assert.IsFalse(withSelection.AdditionalUserInputRequired);
    }

    [TestMethod]
    public async Task CapturedSaveSnapshotDoesNotTrackLaterDesktopChanges()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "source.xml");
        var loading = new SourceLoadingCoordinator(
            new StaticSourceClient(sourcePath),
            context.Sources,
            context.Workflow);
        Assert.IsTrue((await loading.AddAsync(SourceSelectionKind.XmlFile, sourcePath)).Accepted);
        var captured = context.Coordinator.CaptureSnapshot();

        Assert.IsTrue(loading.SetInclusion(context.Sources.Items, false).Accepted);

        Assert.IsTrue(captured.Sources.Single().IsIncluded);
        Assert.IsFalse(context.Sources.Items.Single().IsIncluded);
    }

    private static TestContext CreateContext()
    {
        var sources = new ActiveLoadedSourceSet();
        var configuration = new ActiveDiscoveryConfiguration();
        var workflow = new ApplicationWorkflowCoordinator(
            new ReadySupervisor(),
            new RecordingProcessingHistoryRecorder());
        var database = new DatabaseBuildCoordinator(
            configuration,
            sources,
            workflow,
            new UnusedDatabaseClient(),
            NullLogger<DatabaseBuildCoordinator>.Instance);
        var client = new StubWorkingStateClient();
        var coordinator = new WorkingStateCoordinator(
            sources,
            configuration,
            database,
            workflow,
            client,
            NullLogger<WorkingStateCoordinator>.Instance);
        return new TestContext(sources, configuration, workflow, database, client, coordinator);
    }

    private static async Task<PreparedContext> CreateCurrentDatabaseContextAsync()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "source.xml");
        var loading = new SourceLoadingCoordinator(
            new StaticSourceClient(sourcePath),
            context.Sources,
            context.Workflow);
        Assert.IsTrue((await loading.AddAsync(SourceSelectionKind.XmlFile, sourcePath)).Accepted);
        var sourceSet = context.Sources.ActiveSourceSet!;
        var identity = new DiscoveryInformationIdentity(
            sourceSet.SourceSetId,
            "/records/code",
            "code");
        context.Configuration.Synchronize([identity]);
        Assert.AreEqual(1, context.Configuration.SetSelection([identity], true));

        var discovery = await context.Workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(discovery.Accepted);
        Assert.IsTrue(context.Workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);

        var mapping = DatabaseTagMapper.CreateFieldMappings(
            sourceSet.SourceSetId,
            context.Configuration.Current.Items,
            context.Configuration.DatabaseTagOverridesByIdentity).Single();
        var generation = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            [new DatabaseDatasetSummary(
                sourceSet.SourceSetId,
                sourceSet.Name,
                1,
                RepeatedDataLayout.AlignRepeatedGroupsByPosition,
                1,
                1,
                [new DatabaseColumnDefinition(
                    new DatabaseColumnIdentity(
                        sourceSet.SourceSetId,
                        mapping.FieldKey,
                        DatabaseRepeatCoordinatePath.Empty),
                    mapping.EffectiveName,
                    1)],
                [mapping])]);
        var build = await context.Workflow.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);
        Assert.IsTrue(build.Accepted);
        Assert.IsTrue(context.Workflow.CompleteOperation(
            build.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        context.Database.AdoptRestoredGeneration(generation);
        return new PreparedContext(context, loading, identity);
    }

    private static DatabaseGenerationSummary CreateGeneration(
        SourceSetId sourceSetId,
        DiscoveryInformationIdentity identity)
    {
        var mapping = new DatabaseFieldMapping(
            DatabaseLogicalFieldIdentity.Create(identity),
            "RestoredCode",
            true,
            [identity]);
        var column = new DatabaseColumnDefinition(
            new DatabaseColumnIdentity(
                sourceSetId,
                mapping.FieldKey,
                DatabaseRepeatCoordinatePath.Empty),
            "RestoredCode",
            1);
        return new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            [new DatabaseDatasetSummary(
                sourceSetId,
                "Restored set",
                1,
                RepeatedDataLayout.StructuralRows,
                1,
                1,
                [column],
                [mapping])]);
    }

    private static WorkingStateManifest CreateManifest(WorkingStateSnapshot snapshot) =>
        new(
            WorkingStatePackageFormat.CurrentSchemaVersion,
            5,
            new string('0', 64),
            snapshot);

    private sealed record TestContext(
        ActiveLoadedSourceSet Sources,
        ActiveDiscoveryConfiguration Configuration,
        ApplicationWorkflowCoordinator Workflow,
        DatabaseBuildCoordinator Database,
        StubWorkingStateClient Client,
        WorkingStateCoordinator Coordinator);

    private sealed record PreparedContext(
        TestContext Context,
        SourceLoadingCoordinator Loading,
        DiscoveryInformationIdentity Identity);

    private sealed class StubWorkingStateClient : IWorkingStateClient
    {
        public bool PersistSaveTargets { get; set; }

        public WorkingStateManifest? RestoreManifest { get; set; }

        public int SaveCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public WorkingStateSnapshot? LastSavedSnapshot { get; private set; }

        public WorkingStatePublicationMode? LastPublicationMode { get; private set; }

        public Task<WorkingStateClientResult> SaveAsync(
            OperationCorrelation correlation,
            string targetPath,
            WorkingStateSnapshot snapshot,
            WorkingStatePublicationMode publicationMode,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            LastSavedSnapshot = snapshot;
            LastPublicationMode = publicationMode;
            RestoreManifest = CreateManifest(snapshot);
            if (PersistSaveTargets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllBytes(targetPath, [1, 2, 3, 4]);
            }

            return Task.FromResult(new WorkingStateClientResult(
                true,
                new OperationCompletion(correlation, OperationOutcome.CompletedSuccessfully, []),
                CreateManifest(snapshot),
                null,
                null));
        }

        public Task<WorkingStateClientResult> RestoreAsync(
            OperationCorrelation correlation,
            string packagePath,
            CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            return Task.FromResult(new WorkingStateClientResult(
                true,
                new OperationCompletion(correlation, OperationOutcome.CompletedSuccessfully, []),
                RestoreManifest,
                null,
                null));
        }
    }

    private sealed class SaveOperationReadiness(WorkingStateCoordinator coordinator) :
        IWorkflowOperationReadiness
    {
        public event EventHandler? ReadinessChanged
        {
            add { }
            remove { }
        }

        public WorkflowOperationReadiness Evaluate(WorkflowOperationKind operationKind)
        {
            Assert.AreEqual(WorkflowOperationKind.WorkingStateSave, operationKind);
            return coordinator.EvaluateSaveReadiness();
        }
    }

    private sealed class RecoveryHistoryStore(ProcessingOperationStartRecord start) :
        IProcessingHistoryReader,
        IProcessingHistoryRecorder
    {
        private readonly List<ProcessingAttemptRecord> _attempts = [];
        private readonly List<ProcessingDiagnosticRecord> _diagnostics = [];

        public ProcessingHistorySnapshot Read() => new(
            _attempts.ToArray(),
            _diagnostics.ToArray(),
            [start],
            ReadProblem: null)
        {
            RecoveryEvidenceComplete = true
        };

        public void RecordStart(ProcessingOperationStartRecord record)
        {
        }

        public void RecordAttempt(ProcessingAttemptRecord record) => _attempts.Add(record);

        public void RecordDiagnostic(ProcessingDiagnosticRecord record) =>
            _diagnostics.Add(record);
    }

    private sealed class EmptyHistoryReader : IProcessingHistoryReader
    {
        public ProcessingHistorySnapshot Read() => new([], [], ReadProblem: null);
    }

    private sealed class AcceptedDeleteConfirmation : ISavedWorkingStateDeleteConfirmation
    {
        public bool IsOpen => false;

        public string? StateName => null;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<bool> ConfirmAsync(
            string stateName,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void Accept()
        {
        }

        public void Decline()
        {
        }
    }

    private sealed class ReadySupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            true,
            123,
            null);

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

    private sealed class UnusedDatabaseClient : IDatabaseClient
    {
        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Working-state restore must not rebuild the Database.");
    }

    private sealed class StaticSourceClient(string sourcePath) : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceIntakeClientResult(
                true,
                [new LoadedSourceContract(
                    SourceId.CreateNew(),
                    sourcePath,
                    true,
                    LoadedSourceStatus.Ready,
                    LoadedSourceKind.XmlFile)],
                null,
                null));

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Snapshot capture must not refresh sources.");
    }

    private sealed class TemporaryPackageFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR166.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryPackageFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "existing.cia");
            File.WriteAllBytes(Path, [1, 2, 3, 4]);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class TemporaryManagedStateDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR93.Integration.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryManagedStateDirectory()
        {
            Paths = ApplicationPaths.FromLocalApplicationData(_root);
        }

        public ApplicationPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
