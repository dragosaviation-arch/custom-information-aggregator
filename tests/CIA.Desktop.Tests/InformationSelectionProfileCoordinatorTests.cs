using System.Text;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Profiles;
using CIA.Core.Runtime;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Profiles;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class InformationSelectionProfileCoordinatorTests
{
    [TestMethod]
    public async Task SaveMapsOnlyPortableNonBlacklistSelectionState()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var sourceSet = SourceSetId.CreateNew();
        var selected = Identity(sourceSet, "/catalog/code", "code");
        var neutral = Identity(sourceSet, "/catalog/description", "description");
        var blacklisted = Identity(sourceSet, "/catalog/secret", "secret");
        environment.Configuration.Synchronize([selected, neutral, blacklisted]);
        environment.Configuration.SetSelection([selected], isSelected: true);
        environment.Configuration.SetBlacklisted(blacklisted, isBlacklisted: true);
        environment.Configuration.SetDatabaseTagOverride(selected, "Mapped Code");
        environment.Configuration.SetRepeatedDataLayout(
            sourceSet,
            RepeatedDataLayout.AllCombinations);

        var saved = environment.Coordinator.SaveNew("Reusable selection");

        Assert.IsTrue(saved.Succeeded, saved.Message);
        var inspection = environment.Store.FindById(saved.Profile!.ProfileId);
        var artifact = inspection.Artifact!;
        Assert.AreEqual(ProfileKind.InformationSelection, artifact.ProfileKind);
        Assert.AreEqual(7, artifact.ProfileId.Value.Version);
        Assert.AreEqual(
            $"{artifact.ProfileId}{ProfileArtifactStore.FileExtension}",
            Path.GetFileName(inspection.Path));
        var content = (InformationSelectionProfileContentV1)artifact.Content;
        Assert.HasCount(2, content.Entries);
        Assert.AreEqual(
            InformationSelectionMembership.Selected,
            content.Entries.Single(entry => entry.Identity.InformationType == "code").Membership);
        Assert.AreEqual(
            InformationSelectionMembership.Excluded,
            content.Entries.Single(entry => entry.Identity.InformationType == "description").Membership);
        Assert.IsFalse(content.Entries.Any(entry => entry.Identity.InformationType == "secret"));
        var json = Encoding.UTF8.GetString(File.ReadAllBytes(inspection.Path));
        Assert.IsFalse(json.Contains("Mapped Code", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("sourceSetId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("repeatedDataLayout", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("blacklisted", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task MultiSetPortableIdentityRoundTripAppliesAllMatchesOnceAndPreservesOverrides()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = Identity(firstSet, "/catalog/item/code", "code");
        var second = Identity(secondSet, first.StructuralPath, "code");
        environment.Configuration.Synchronize([first, second]);
        environment.Configuration.SetSelection([first, second], isSelected: true);
        environment.Configuration.SetDatabaseTagOverride(first, "First Code");
        environment.Configuration.SetDatabaseTagOverride(second, "Second Code");
        environment.Configuration.SetRepeatedDataLayout(
            firstSet,
            RepeatedDataLayout.StructuralRows);

        var saved = environment.Coordinator.SaveNew("Across sets");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        var stored = environment.Store.FindById(saved.Profile!.ProfileId);
        var content = (InformationSelectionProfileContentV1)stored.Artifact!.Content;
        Assert.HasCount(1, content.Entries);
        var originalBytes = File.ReadAllBytes(stored.Path);

        environment.Configuration.SetSelection([first, second], isSelected: false);
        environment.Configuration.SetDatabaseTagOverride(first, "Changed First");
        environment.Configuration.SetDatabaseTagOverride(second, "Changed Second");
        var firstLayout = environment.Configuration.RepeatedDataLayouts[firstSet];
        var secondLayout = environment.Configuration.RepeatedDataLayouts[secondSet];
        var workflowEvents = 0;
        environment.Workflow.StateChanged += (_, _) => workflowEvents++;

        var loaded = environment.Coordinator.Load(saved.Profile);

        Assert.IsTrue(loaded.Succeeded, loaded.Message);
        Assert.AreEqual(2, loaded.ChangedCount);
        Assert.AreEqual(1, loaded.MatchedCount);
        Assert.AreEqual(1, workflowEvents);
        Assert.IsTrue(environment.Configuration.Current.Items.All(item =>
            item.Disposition == DiscoveryInformationDisposition.Selected));
        Assert.AreEqual(
            "Changed First",
            environment.Configuration.DatabaseTagOverridesByIdentity[first]);
        Assert.AreEqual(
            "Changed Second",
            environment.Configuration.DatabaseTagOverridesByIdentity[second]);
        Assert.AreEqual(firstLayout, environment.Configuration.RepeatedDataLayouts[firstSet]);
        Assert.AreEqual(secondLayout, environment.Configuration.RepeatedDataLayouts[secondSet]);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(stored.Path));
        Assert.AreEqual(WorkflowArtifactStatus.Stale, environment.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, environment.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Discovery);
    }

    [TestMethod]
    public async Task ConflictingMultiSetMembershipRejectsSaveAndUpdateWithoutPublication()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var first = Identity(SourceSetId.CreateNew(), "/catalog/item/code", "code");
        var second = Identity(SourceSetId.CreateNew(), first.StructuralPath, "code");
        environment.Configuration.Synchronize([first, second]);
        environment.Configuration.SetSelection([first, second], isSelected: true);
        var saved = environment.Coordinator.SaveNew("Consistent");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        var path = saved.Profile!.Path;
        var previousBytes = File.ReadAllBytes(path);

        environment.Configuration.SetSelection([second], isSelected: false);
        Assert.IsFalse(environment.Coordinator.CanCaptureCurrentConfiguration);
        StringAssert.Contains(
            environment.Coordinator.CaptureReadinessReason,
            "conflicting selection states");
        var rejectedSave = environment.Coordinator.SaveNew("Conflicting");
        var rejectedUpdate = environment.Coordinator.Update(saved.Profile);

        Assert.IsFalse(rejectedSave.Succeeded);
        Assert.IsFalse(rejectedUpdate.Succeeded);
        StringAssert.Contains(rejectedSave.Message, "conflicting selection states");
        StringAssert.Contains(rejectedUpdate.Message, "conflicting selection states");
        Assert.HasCount(1, environment.Store.CreateInventory().ValidProfiles);
        CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task LoadUsesFullPortableIdentityAndReportsUnmatchedAndBlacklistedMatches()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var buyer = Identity(firstSet, "/catalog/buyer/name", "name");
        var sameBuyerOtherSet = Identity(secondSet, buyer.StructuralPath, "name");
        var seller = Identity(firstSet, "/catalog/seller/name", "name");
        var absentFromProfile = Identity(firstSet, "/catalog/untouched", "untouched");
        var blockedBuyer = Identity(
            SourceSetId.CreateNew(),
            buyer.StructuralPath,
            "name");
        environment.Configuration.Synchronize(
            [buyer, sameBuyerOtherSet, seller, absentFromProfile, blockedBuyer]);
        environment.Configuration.SetSelection([seller, absentFromProfile], isSelected: true);
        environment.Configuration.SetBlacklisted(blockedBuyer, isBlacklisted: true);
        environment.Configuration.SetDatabaseTagOverride(seller, "Seller Name");
        var missing = new PortableDiscoveryInformationIdentity(
            "/missing/value",
            "value",
            SourceValueCandidateKind.Element,
            "Element:/missing/value");
        var profile = CreateArtifact(
            "Portable",
            new(PortableDiscoveryInformationIdentity.From(buyer), InformationSelectionMembership.Selected),
            new(PortableDiscoveryInformationIdentity.From(seller), InformationSelectionMembership.Excluded),
            new(missing, InformationSelectionMembership.Selected));
        var created = environment.Store.Create(
            $"{profile.ProfileId}{ProfileArtifactStore.FileExtension}",
            profile);
        Assert.IsTrue(created.Succeeded, created.Problem);
        var selectedProfile = environment.Coordinator.RefreshInventory().Profiles.Single();

        var result = environment.Coordinator.Load(selectedProfile);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(2, result.MatchedCount);
        Assert.AreEqual(1, result.UnmatchedCount);
        Assert.AreEqual(1, result.SkippedBlacklistedCount);
        var dispositions = environment.Configuration.Current.Items.ToDictionary(
            item => item.Identity,
            item => item.Disposition);
        Assert.AreEqual(DiscoveryInformationDisposition.Selected, dispositions[buyer]);
        Assert.AreEqual(DiscoveryInformationDisposition.Selected, dispositions[sameBuyerOtherSet]);
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral, dispositions[seller]);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            dispositions[absentFromProfile]);
        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted, dispositions[blockedBuyer]);
        Assert.AreEqual(
            "Seller Name",
            environment.Configuration.DatabaseTagOverridesByIdentity[seller]);
        StringAssert.Contains(result.Message, "unmatched");
        StringAssert.Contains(result.Message, "blacklisted");
    }

    [TestMethod]
    public async Task NoOpLoadAndProfilePersistenceOperationsDoNotInvalidateWorkflow()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetSelection([identity], isSelected: true);
        var events = 0;
        environment.Workflow.StateChanged += (_, _) => events++;

        var saved = environment.Coordinator.SaveNew("No-op");
        var loaded = environment.Coordinator.Load(saved.Profile!);
        var updated = environment.Coordinator.Update(saved.Profile!);
        var deleted = environment.Coordinator.Delete(updated.Profile!);

        Assert.IsTrue(saved.Succeeded, saved.Message);
        Assert.IsTrue(loaded.Succeeded, loaded.Message);
        Assert.AreEqual(0, loaded.ChangedCount);
        Assert.IsTrue(updated.Succeeded, updated.Message);
        Assert.IsTrue(deleted.Succeeded, deleted.Message);
        Assert.AreEqual(0, events);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Extraction);
        Assert.AreEqual(DiscoveryInformationDisposition.Selected,
            environment.Configuration.Current.Items.Single().Disposition);
    }

    [TestMethod]
    public async Task InventoryFiltersKindsAndContainsFailuresWithoutHidingValidProfiles()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var first = CreateArtifact("First", Entry("/one", "one"));
        var second = CreateArtifact("Copied", Entry("/two", "two"));
        var blacklist = CreateBlacklistArtifact("Blacklist");
        Assert.IsTrue(environment.Store.Create(
            $"{first.ProfileId}{ProfileArtifactStore.FileExtension}", first).Succeeded);
        Assert.IsTrue(environment.Store.Create(
            $"{blacklist.ProfileId}{ProfileArtifactStore.FileExtension}", blacklist).Succeeded);
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        var copiedPath = Path.Combine(
            environment.Paths.ProfilesDirectory,
            "manually-copied.cia-profile.json");
        File.WriteAllBytes(copiedPath, ProfileArtifactStore.Serialize(second));
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "malformed.cia-profile.json"),
            "{not-json");

        var inventory = environment.Coordinator.RefreshInventory();

        Assert.HasCount(2, inventory.Profiles);
        Assert.IsTrue(inventory.Profiles.Any(item => item.Name == "Copied"));
        Assert.IsFalse(inventory.Profiles.Any(item => item.Name == "Blacklist"));
        Assert.HasCount(1, inventory.Problems);

        File.Copy(
            copiedPath,
            Path.Combine(environment.Paths.ProfilesDirectory, "duplicate-id.cia-profile.json"));
        inventory = environment.Coordinator.RefreshInventory();
        Assert.HasCount(1, inventory.Profiles);
        Assert.AreEqual("First", inventory.Profiles.Single().Name);
        Assert.HasCount(3, inventory.Problems);
    }

    [TestMethod]
    public async Task UpdatePreservesIdentityCreationAndNameAndRejectsStaleFingerprint()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        var saved = environment.Coordinator.SaveNew("Stable name");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        var createdAt = saved.Profile!.CreatedAtUtc;
        environment.Configuration.SetSelection([identity], isSelected: true);

        var updated = environment.Coordinator.Update(saved.Profile!);

        Assert.IsTrue(updated.Succeeded, updated.Message);
        Assert.AreEqual(saved.Profile.ProfileId, updated.Profile!.ProfileId);
        Assert.AreEqual(createdAt, updated.Profile.CreatedAtUtc);
        Assert.AreEqual("Stable name", updated.Profile.Name);
        Assert.IsGreaterThan(saved.Profile.UpdatedAtUtc, updated.Profile.UpdatedAtUtc);
        var updatedBytes = File.ReadAllBytes(updated.Profile.Path);

        var stale = environment.Coordinator.Update(saved.Profile);
        Assert.IsFalse(stale.Succeeded);
        Assert.IsTrue(stale.RequiresReselection);
        CollectionAssert.AreEqual(updatedBytes, File.ReadAllBytes(updated.Profile.Path));
    }

    [TestMethod]
    public async Task DeleteRequiresCurrentFingerprintCannotDeleteBlacklistAndDoesNotUndoAppliedState()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetSelection([identity], isSelected: true);
        var saved = environment.Coordinator.SaveNew("Delete me");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        var savedProfile = saved.Profile!;
        environment.Configuration.SetSelection([identity], isSelected: false);
        Assert.IsTrue(environment.Coordinator.Load(savedProfile).Succeeded);

        var updated = environment.Coordinator.Update(savedProfile);
        Assert.IsTrue(updated.Succeeded, updated.Message);
        var staleDelete = environment.Coordinator.Delete(savedProfile);
        Assert.IsFalse(staleDelete.Succeeded);
        Assert.IsTrue(staleDelete.RequiresReselection);
        Assert.IsTrue(File.Exists(updated.Profile!.Path));

        var deleted = environment.Coordinator.Delete(updated.Profile);
        Assert.IsTrue(deleted.Succeeded, deleted.Message);
        Assert.IsFalse(File.Exists(updated.Profile.Path));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            environment.Configuration.Current.Items.Single().Disposition);

        var blacklist = CreateBlacklistArtifact("Protected blacklist");
        var created = environment.Store.Create(
            $"{blacklist.ProfileId}{ProfileArtifactStore.FileExtension}",
            blacklist);
        var inspection = environment.Store.FindById(blacklist.ProfileId);
        var forgedSelection = new InformationSelectionProfileItem(
            blacklist.ProfileId,
            blacklist.Name,
            blacklist.CreatedAtUtc,
            blacklist.UpdatedAtUtc,
            created.Path!,
            inspection.Fingerprint!.Value);
        var rejected = environment.Coordinator.Delete(forgedSelection);
        Assert.IsFalse(rejected.Succeeded);
        Assert.IsTrue(File.Exists(created.Path));
    }

    [TestMethod]
    public async Task ChoosingAndLoadingProfileRemainExplicitAndNeverAutoRewriteContent()
    {
        using var environment = await ProfileCoordinatorEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetSelection([identity], isSelected: true);
        var saved = environment.Coordinator.SaveNew("Explicit actions only");
        var bytes = File.ReadAllBytes(saved.Profile!.Path);
        environment.Configuration.SetSelection([identity], isSelected: false);
        var selectedProfile = environment.Coordinator.RefreshInventory().Profiles.Single();

        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            environment.Configuration.Current.Items.Single().Disposition);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(saved.Profile.Path));

        var loaded = environment.Coordinator.Load(selectedProfile);
        Assert.IsTrue(loaded.Succeeded, loaded.Message);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            environment.Configuration.Current.Items.Single().Disposition);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(saved.Profile.Path));

        environment.Configuration.SetSelection([identity], isSelected: false);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(saved.Profile.Path));
    }

    private static DiscoveryInformationIdentity Identity(
        SourceSetId sourceSetId,
        string path,
        string informationType,
        SourceValueCandidateKind candidateKind = SourceValueCandidateKind.Element,
        string? structuralIdentity = null) =>
        new(
            sourceSetId,
            path,
            informationType,
            candidateKind,
            structuralIdentity ?? $"{candidateKind}:{path}");

    private static InformationSelectionProfileEntryV1 Entry(
        string path,
        string informationType,
        InformationSelectionMembership membership = InformationSelectionMembership.Selected) =>
        new(
            new PortableDiscoveryInformationIdentity(
                path,
                informationType,
                SourceValueCandidateKind.Element,
                $"Element:{path}"),
            membership);

    private static ProfileArtifactV1 CreateArtifact(
        string name,
        params InformationSelectionProfileEntryV1[] entries)
    {
        var now = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.InformationSelection,
            name,
            now,
            now,
            new InformationSelectionProfileContentV1(entries));
    }

    private static ProfileArtifactV1 CreateBlacklistArtifact(string name)
    {
        var now = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.Blacklist,
            name,
            now,
            now,
            new BlacklistProfileContentV1(
            [
                Entry("/blocked", "blocked").Identity
            ]));
    }

    private sealed class ProfileCoordinatorEnvironment : IDisposable
    {
        private static readonly string SafeRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR90.Tests");

        private ProfileCoordinatorEnvironment(string root)
        {
            Root = root;
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(root, "LocalAppData"));
            Store = new ProfileArtifactStore(Paths);
            Configuration = new ActiveDiscoveryConfiguration();
            Workflow = new ApplicationWorkflowCoordinator(
                new ReadyProcessingHostSupervisor(),
                new RecordingProcessingHistoryRecorder());
            Coordinator = new InformationSelectionProfileCoordinator(
                Store,
                Configuration,
                Workflow);
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public ProfileArtifactStore Store { get; }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public InformationSelectionProfileCoordinator Coordinator { get; }

        public static async Task<ProfileCoordinatorEnvironment> CreateAsync()
        {
            var environment = new ProfileCoordinatorEnvironment(
                Path.Combine(SafeRoot, Guid.NewGuid().ToString("N")));
            environment.Workflow.RecordSourceSelectionChanged(hasValidSourceSelection: true);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.Discovery);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.DatabaseBuild);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.Extraction);
            return environment;
        }

        public void Dispose()
        {
            Workflow.Dispose();
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
                    "Refusing to remove a profile test directory outside its safe root.");
            }

            Directory.Delete(target, recursive: true);
        }

        private static async Task CompleteAsync(
            ApplicationWorkflowCoordinator workflow,
            WorkflowOperationKind operationKind)
        {
            var begun = await workflow.BeginOperationAsync(operationKind);
            Assert.IsTrue(begun.Accepted, begun.Rejection?.Reason);
            var completed = workflow.CompleteOperation(
                begun.Operation!.OperationId,
                OperationOutcome.CompletedSuccessfully);
            Assert.IsTrue(completed.Accepted, completed.Rejection?.Reason);
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
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
