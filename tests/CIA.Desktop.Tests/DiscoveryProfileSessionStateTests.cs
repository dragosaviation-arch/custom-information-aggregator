using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Profiles;
using CIA.Core.Runtime;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Profiles;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DiscoveryProfileSessionStateTests
{
    [TestMethod]
    public async Task ActiveProfilesApplyAcrossDiscoveryRerunByPortableIdentityWithoutExtraInvalidation()
    {
        using var environment = new SessionTestEnvironment();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var sources = environment.CreateSources(firstSet, secondSet);
        var blackFirst = Identity(firstSet, "/root/black", "black");
        var blackSecond = Identity(secondSet, "/root/black", "black");
        var selectedFirst = Identity(firstSet, "/root/selected", "selected");
        var selectedSecond = Identity(secondSet, "/root/selected", "selected");
        var excluded = Identity(firstSet, "/root/excluded", "excluded");
        var cleared = Identity(firstSet, "/root/cleared", "cleared");
        var identities = new[]
        {
            blackFirst,
            blackSecond,
            selectedFirst,
            selectedSecond,
            excluded,
            cleared
        };
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize([blackFirst, selectedFirst, excluded, cleared]);
        configuration.SetSelection([excluded], isSelected: true);
        configuration.SetBlacklisted([cleared], isBlacklisted: true);
        var sessionState = new ReusableProfileSessionState();
        var informationArtifact = CreateInformationSelectionArtifact(
            [
                SelectionEntry(blackFirst, InformationSelectionMembership.Selected),
                SelectionEntry(selectedFirst, InformationSelectionMembership.Selected),
                SelectionEntry(excluded, InformationSelectionMembership.Excluded)
            ]);
        var blacklistArtifact = CreateBlacklistArtifact(blackFirst);
        var informationWrite = environment.Store.Create(
            $"{informationArtifact.ProfileId}{ProfileArtifactStore.FileExtension}",
            informationArtifact);
        var blacklistWrite = environment.Store.Create(
            $"{blacklistArtifact.ProfileId}{ProfileArtifactStore.FileExtension}",
            blacklistArtifact);
        Assert.IsTrue(informationWrite.Succeeded, informationWrite.Problem);
        Assert.IsTrue(blacklistWrite.Succeeded, blacklistWrite.Problem);
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var sourceSet = await environment.CreateActiveSourceSetAsync(workflow, sources);
        var initialDiscovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(initialDiscovery.Accepted, initialDiscovery.Rejection?.Reason);
        Assert.IsTrue(workflow.CompleteOperation(
            initialDiscovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        var informationCoordinator = new InformationSelectionProfileCoordinator(
            environment.Store,
            configuration,
            workflow,
            sessionState: sessionState);
        var blacklistCoordinator = new BlacklistProfileCoordinator(
            environment.Store,
            environment.DesignationStore,
            configuration,
            workflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            sessionState: sessionState);
        var informationProfile = informationCoordinator.RefreshInventory().Profiles.Single();
        var blacklistProfile = blacklistCoordinator.RefreshInventory().Profiles.Single();
        var informationBytes = File.ReadAllBytes(informationProfile.Path);
        var blacklistBytes = File.ReadAllBytes(blacklistProfile.Path);

        var informationLoad = informationCoordinator.Load(informationProfile);
        var blacklistLoad = blacklistCoordinator.Load(blacklistProfile);

        Assert.IsTrue(informationLoad.Succeeded, informationLoad.Message);
        Assert.IsTrue(blacklistLoad.Succeeded, blacklistLoad.Message);
        var recordingWorkflow = new RecordingWorkflowCoordinator(workflow);
        var client = new StaticDiscoveryClient(identities.Select((identity, index) =>
            Information(identity, sources[index % sources.Count])).ToArray());
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            recordingWorkflow,
            reusableProfileSessionState: sessionState);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(0, recordingWorkflow.DiscoveryConfigurationChangedCount);
        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted,
            Disposition(configuration, blackFirst));
        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted,
            Disposition(configuration, blackSecond));
        Assert.AreEqual(DiscoveryInformationDisposition.Selected,
            Disposition(configuration, selectedFirst));
        Assert.AreEqual(DiscoveryInformationDisposition.Selected,
            Disposition(configuration, selectedSecond));
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral,
            Disposition(configuration, excluded));
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral,
            Disposition(configuration, cleared));
        CollectionAssert.AreEqual(informationBytes, File.ReadAllBytes(informationProfile.Path));
        CollectionAssert.AreEqual(blacklistBytes, File.ReadAllBytes(blacklistProfile.Path));
    }

    [TestMethod]
    public void ProfileApplicationPreservesOverridesAndLayoutsWhileApplyingReplacementSemantics()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var blacklisted = Identity(sourceSetId, "/root/blacklisted", "blacklisted");
        var selected = Identity(sourceSetId, "/root/selected", "selected");
        var excluded = Identity(sourceSetId, "/root/excluded", "excluded");
        var cleared = Identity(sourceSetId, "/root/cleared", "cleared");
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize([blacklisted, selected, excluded, cleared]);
        configuration.SynchronizeSourceSets([sourceSetId]);
        configuration.SetSelection([excluded], isSelected: true);
        configuration.SetBlacklisted([cleared], isBlacklisted: true);
        configuration.SetDatabaseTagOverride(selected, "Mapped Selected");
        configuration.SetRepeatedDataLayout(sourceSetId, RepeatedDataLayout.StructuralRows);
        var sessionState = new ReusableProfileSessionState();
        sessionState.ActivateInformationSelection(
            ProfileId.CreateNew(),
            new InformationSelectionProfileContentV1(
            [
                SelectionEntry(blacklisted, InformationSelectionMembership.Selected),
                SelectionEntry(selected, InformationSelectionMembership.Selected),
                SelectionEntry(excluded, InformationSelectionMembership.Excluded)
            ]));
        sessionState.ActivateBlacklist(
            ProfileId.CreateNew(),
            new BlacklistProfileContentV1(
            [
                PortableDiscoveryInformationIdentity.From(blacklisted)
            ]),
            ReusableProfileActivationOrigin.ExplicitLoad);

        sessionState.ApplyTo(configuration);

        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted,
            Disposition(configuration, blacklisted));
        Assert.AreEqual(DiscoveryInformationDisposition.Selected,
            Disposition(configuration, selected));
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral,
            Disposition(configuration, excluded));
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral,
            Disposition(configuration, cleared));
        Assert.AreEqual(
            "Mapped Selected",
            configuration.DatabaseTagOverridesByIdentity[selected]);
        Assert.AreEqual(
            RepeatedDataLayout.StructuralRows,
            configuration.RepeatedDataLayouts[sourceSetId]);
    }

    [TestMethod]
    public async Task ExplicitCrudAndSelectionDoNotActivateUntilSuccessfulLoad()
    {
        using var environment = new SessionTestEnvironment();
        var identity = Identity(SourceSetId.CreateNew(), "/root/code", "code");
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize([identity]);
        configuration.SetSelection([identity], isSelected: true);
        using var workflow = await CreateCurrentDiscoveryWorkflowAsync();
        var sessionState = new ReusableProfileSessionState();
        var informationCoordinator = new InformationSelectionProfileCoordinator(
            environment.Store,
            configuration,
            workflow,
            sessionState: sessionState);
        var blacklistCoordinator = new BlacklistProfileCoordinator(
            environment.Store,
            environment.DesignationStore,
            configuration,
            workflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            sessionState: sessionState);

        var information = informationCoordinator.SaveNew("Selection");
        Assert.IsTrue(information.Succeeded, information.Message);
        information = informationCoordinator.Update(information.Profile!);
        Assert.IsTrue(information.Succeeded, information.Message);
        configuration.SetBlacklisted([identity], isBlacklisted: true);
        var blacklist = blacklistCoordinator.SaveNew("Blacklist");
        Assert.IsTrue(blacklist.Succeeded, blacklist.Message);
        blacklist = blacklistCoordinator.Update(blacklist.Profile!);
        Assert.IsTrue(blacklist.Succeeded, blacklist.Message);
        var clone = blacklistCoordinator.Clone(blacklist.Profile!, "Clone");
        Assert.IsTrue(clone.Succeeded, clone.Message);
        Assert.IsTrue(blacklistCoordinator.Delete(clone.Profile!).Succeeded);
        Assert.IsNull(sessionState.Current.InformationSelection);
        Assert.IsNull(sessionState.Current.Blacklist);

        var selectedInformation = informationCoordinator.RefreshInventory().Profiles.Single();
        var selectedBlacklist = blacklistCoordinator.RefreshInventory().Profiles.Single();
        Assert.IsNull(sessionState.Current.InformationSelection,
            "Merely selecting an inventory item must not activate it.");
        var dispositionBeforeDefault = Disposition(configuration, identity);
        Assert.IsTrue(blacklistCoordinator.SetDefault(selectedBlacklist).Succeeded);
        Assert.AreEqual(dispositionBeforeDefault, Disposition(configuration, identity));
        Assert.IsNull(sessionState.Current.Blacklist,
            "Set Default must not activate or apply the selected profile.");
        var informationBytes = File.ReadAllBytes(selectedInformation.Path);
        var blacklistBytes = File.ReadAllBytes(selectedBlacklist.Path);
        var markerBytes = File.ReadAllBytes(environment.DesignationStore.DesignationPath);
        configuration.SetBlacklisted([identity], isBlacklisted: false);
        configuration.SetSelection([identity], isSelected: true);

        var loadedInformation = informationCoordinator.Load(selectedInformation);
        var loadedBlacklist = blacklistCoordinator.Load(selectedBlacklist);

        Assert.IsTrue(loadedInformation.Succeeded, loadedInformation.Message);
        Assert.IsTrue(loadedBlacklist.Succeeded, loadedBlacklist.Message);
        Assert.AreEqual(selectedInformation.ProfileId,
            sessionState.Current.InformationSelection!.ProfileId);
        Assert.AreEqual(ReusableProfileActivationOrigin.ExplicitLoad,
            sessionState.Current.InformationSelection.Origin);
        Assert.AreEqual(selectedBlacklist.ProfileId,
            sessionState.Current.Blacklist!.ProfileId);
        Assert.AreEqual(ReusableProfileActivationOrigin.ExplicitLoad,
            sessionState.Current.Blacklist.Origin);
        CollectionAssert.AreEqual(informationBytes, File.ReadAllBytes(selectedInformation.Path));
        CollectionAssert.AreEqual(blacklistBytes, File.ReadAllBytes(selectedBlacklist.Path));
        CollectionAssert.AreEqual(
            markerBytes,
            File.ReadAllBytes(environment.DesignationStore.DesignationPath));
        Assert.IsNull(typeof(ActiveInformationSelectionProfileState).GetProperty("Path"));
        Assert.IsNull(typeof(ActiveBlacklistProfileState).GetProperty("Path"));
    }

    [TestMethod]
    public async Task StartupDefaultSeedsFirstSuccessfulDiscoveryButNotBeforeIt()
    {
        using var environment = new SessionTestEnvironment();
        var sourceSetId = SourceSetId.CreateNew();
        var source = environment.CreateSources(sourceSetId).Single();
        var identity = Identity(sourceSetId, "/root/code", "code");
        var artifact = CreateBlacklistArtifact(identity);
        var stored = environment.Store.Create(
            $"{artifact.ProfileId}{ProfileArtifactStore.FileExtension}",
            artifact);
        Assert.IsTrue(stored.Succeeded, stored.Problem);
        Assert.IsTrue(environment.DesignationStore.Set(artifact.ProfileId).Succeeded);
        var artifactBytes = File.ReadAllBytes(stored.Path!);
        var markerBytes = File.ReadAllBytes(environment.DesignationStore.DesignationPath);
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize([identity]);
        var sessionState = new ReusableProfileSessionState();
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var coordinator = new BlacklistProfileCoordinator(
            environment.Store,
            environment.DesignationStore,
            configuration,
            workflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            sessionState: sessionState);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.AreEqual(DiscoveryInformationDisposition.Neutral,
            Disposition(configuration, identity));
        Assert.AreEqual(artifact.ProfileId, sessionState.Current.Blacklist!.ProfileId);
        Assert.AreEqual(ReusableProfileActivationOrigin.StartupDefault,
            sessionState.Current.Blacklist.Origin);
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        var sourceSet = await environment.CreateActiveSourceSetAsync(workflow, [source]);
        using var viewModel = new DiscoveryWorkspaceViewModel(
            new StaticDiscoveryClient([Information(identity, source)]),
            configuration,
            sourceSet,
            workflow,
            reusableProfileSessionState: sessionState);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted,
            Disposition(configuration, identity));
        CollectionAssert.AreEqual(artifactBytes, File.ReadAllBytes(stored.Path!));
        CollectionAssert.AreEqual(
            markerBytes,
            File.ReadAllBytes(environment.DesignationStore.DesignationPath));
    }

    [TestMethod]
    public async Task MissingAndInvalidStartupDefaultsLeaveSessionInactive()
    {
        using var missingEnvironment = new SessionTestEnvironment();
        using var missingWorkflow = await CreateCurrentDiscoveryWorkflowAsync();
        var missingState = new ReusableProfileSessionState();
        var missingCoordinator = new BlacklistProfileCoordinator(
            missingEnvironment.Store,
            missingEnvironment.DesignationStore,
            new ActiveDiscoveryConfiguration(),
            missingWorkflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            sessionState: missingState);

        await missingCoordinator.StartAsync(CancellationToken.None);

        Assert.IsNull(missingState.Current.Blacklist);
        Assert.AreEqual(
            BlacklistSessionDefaultStatus.NotConfigured,
            missingCoordinator.SessionDefault.Status);

        using var invalidEnvironment = new SessionTestEnvironment();
        Directory.CreateDirectory(invalidEnvironment.Paths.ProfilesDirectory);
        File.WriteAllText(invalidEnvironment.DesignationStore.DesignationPath, "{invalid-json");
        using var invalidWorkflow = await CreateCurrentDiscoveryWorkflowAsync();
        var invalidState = new ReusableProfileSessionState();
        var invalidCoordinator = new BlacklistProfileCoordinator(
            invalidEnvironment.Store,
            invalidEnvironment.DesignationStore,
            new ActiveDiscoveryConfiguration(),
            invalidWorkflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            sessionState: invalidState);

        await invalidCoordinator.StartAsync(CancellationToken.None);

        Assert.IsNull(invalidState.Current.Blacklist);
        Assert.AreEqual(
            BlacklistSessionDefaultStatus.Invalid,
            invalidCoordinator.SessionDefault.Status);
    }

    [TestMethod]
    public void NoActiveProfilesLeaveDiscoveryConfigurationUnchanged()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var selected = Identity(sourceSetId, "/root/selected", "selected");
        var blacklisted = Identity(sourceSetId, "/root/blacklisted", "blacklisted");
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize([selected, blacklisted]);
        configuration.SetSelection([selected], isSelected: true);
        configuration.SetBlacklisted([blacklisted], isBlacklisted: true);
        var before = configuration.Current;

        var result = new ReusableProfileSessionState().ApplyTo(configuration);

        Assert.AreEqual(0, result.TotalChanges);
        CollectionAssert.AreEqual(
            before.Items.ToArray(),
            configuration.Current.Items.ToArray());
        CollectionAssert.AreEqual(
            before.SourceSets.ToArray(),
            configuration.Current.SourceSets.ToArray());
    }

    private static InformationSelectionProfileEntryV1 SelectionEntry(
        DiscoveryInformationIdentity identity,
        InformationSelectionMembership membership) =>
        new(PortableDiscoveryInformationIdentity.From(identity), membership);

    private static ProfileArtifactV1 CreateBlacklistArtifact(
        DiscoveryInformationIdentity identity)
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.Blacklist,
            "Startup default",
            now,
            now,
            new BlacklistProfileContentV1(
            [
                PortableDiscoveryInformationIdentity.From(identity)
            ]));
    }

    private static ProfileArtifactV1 CreateInformationSelectionArtifact(
        IReadOnlyList<InformationSelectionProfileEntryV1> entries)
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.InformationSelection,
            "Selection",
            now,
            now,
            new InformationSelectionProfileContentV1(entries));
    }

    private static DiscoveryInformationIdentity Identity(
        SourceSetId sourceSetId,
        string structuralPath,
        string informationType) =>
        new(
            sourceSetId,
            structuralPath,
            informationType,
            SourceValueCandidateKind.Element,
            structuralPath);

    private static DiscoveredInformation Information(
        DiscoveryInformationIdentity identity,
        LoadedSourceContract source) =>
        new(
            identity,
            1,
            [new DiscoveredSourceContribution(source.SourceId, Path.GetFileName(source.Path), 1)],
            "value");

    private static DiscoveryInformationDisposition Disposition(
        ActiveDiscoveryConfiguration configuration,
        DiscoveryInformationIdentity identity) =>
        configuration.Current.Items.Single(item => item.Identity == identity).Disposition;

    private static async Task<ApplicationWorkflowCoordinator> CreateCurrentDiscoveryWorkflowAsync()
    {
        var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        var begin = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(begin.Accepted, begin.Rejection?.Reason);
        Assert.IsTrue(workflow.CompleteOperation(
            begin.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        return workflow;
    }

    private sealed class SessionTestEnvironment : IDisposable
    {
        private static readonly string SafeRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR75.Tests");

        public SessionTestEnvironment()
        {
            Root = Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData"));
            Store = new ProfileArtifactStore(Paths);
            DesignationStore = new DefaultBlacklistProfileDesignationStore(Paths);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public ProfileArtifactStore Store { get; }

        public DefaultBlacklistProfileDesignationStore DesignationStore { get; }

        public IReadOnlyList<LoadedSourceContract> CreateSources(params SourceSetId[] sourceSetIds)
        {
            return sourceSetIds.Select((sourceSetId, index) =>
            {
                var path = Path.Combine(Root, $"source-{index + 1}.xml");
                File.WriteAllText(path, "<root />");
                return new LoadedSourceContract(
                    SourceId.CreateNew(),
                    sourceSetId,
                    path,
                    IsIncluded: true,
                    LoadedSourceStatus.Ready,
                    LoadedSourceKind.XmlFile);
            }).ToArray();
        }

        public async Task<ActiveLoadedSourceSet> CreateActiveSourceSetAsync(
            IApplicationWorkflowCoordinator workflow,
            IReadOnlyList<LoadedSourceContract> sources)
        {
            var sourceSet = new ActiveLoadedSourceSet();
            var loading = new SourceLoadingCoordinator(
                new StubSourceIntakeClient(sources),
                sourceSet,
                workflow);
            var loaded = await loading.AddAsync(
                SourceSelectionKind.XmlFile,
                sources[0].Path);
            Assert.IsTrue(loaded.Accepted, loaded.FailureDescription);
            return sourceSet;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var safePrefix = Path.GetFullPath(SafeRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to remove an SPR-75 test directory outside its safe root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class StaticDiscoveryClient(
        IReadOnlyList<DiscoveredInformation> information) : IDiscoveryClient
    {
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
                information,
                [],
                completion,
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryOccurrenceClientResult(
                true,
                new DiscoveredOccurrence(
                    lookup.Identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    lookup.Source.SourceId,
                    "value"),
                FailureCode: null,
                FailureDescription: null));
    }

    private sealed class StubSourceIntakeClient(
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

    private sealed class RecordingWorkflowCoordinator(
        IApplicationWorkflowCoordinator inner) : IApplicationWorkflowCoordinator
    {
        public int DiscoveryConfigurationChangedCount { get; private set; }

        public WorkflowStateSnapshot Current => inner.Current;

        public event EventHandler<WorkflowStateSnapshot>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public WorkflowCommandResult RecordSourceSelectionChanged(bool hasValidSourceSelection) =>
            inner.RecordSourceSelectionChanged(hasValidSourceSelection);

        public WorkflowCommandResult RecordDiscoveryConfigurationChanged()
        {
            DiscoveryConfigurationChangedCount++;
            return inner.RecordDiscoveryConfigurationChanged();
        }

        public WorkflowCommandResult RecordDatabaseReviewChanged() =>
            inner.RecordDatabaseReviewChanged();

        public WorkflowCommandResult RecordWorkingStateRestored(
            bool hasValidSourceSelection,
            bool hasPublishedDatabase) =>
            inner.RecordWorkingStateRestored(hasValidSourceSelection, hasPublishedDatabase);

        public Task<WorkflowCommandResult> BeginOperationAsync(
            WorkflowOperationKind operationKind,
            CancellationToken cancellationToken = default) =>
            inner.BeginOperationAsync(operationKind, cancellationToken);

        public WorkflowCommandResult EvaluateOperationPrerequisites(
            WorkflowOperationKind operationKind) =>
            inner.EvaluateOperationPrerequisites(operationKind);

        public Task<WorkflowCommandResult> RequestCancellationAsync(
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(cancellationToken);

        public WorkflowCommandResult CompleteOperation(
            OperationId operationId,
            OperationOutcome outcome) =>
            inner.CompleteOperation(operationId, outcome);

        public WorkflowCommandResult CompleteOperation(OperationCompletion completion) =>
            inner.CompleteOperation(completion);

        public void RestoreInterruptedOperationStatus(
            WorkflowOperationKind operationKind,
            OperationCorrelation correlation,
            string detail) =>
            inner.RestoreInterruptedOperationStatus(operationKind, correlation, detail);

        public void InterruptActiveOperationForShutdown() =>
            inner.InterruptActiveOperationForShutdown();
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
