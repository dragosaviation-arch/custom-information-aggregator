using System.Text;
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
public sealed class BlacklistProfileCoordinatorTests
{
    [TestMethod]
    public async Task MultiSetSaveLoadDefaultAndClonePreservePortableBlacklistBoundary()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = Identity(firstSet, "/catalog/item/code", "code");
        var second = Identity(secondSet, first.StructuralPath, "code");
        var selected = Identity(firstSet, "/catalog/item/name", "name");
        var absent = Identity(secondSet, "/catalog/item/description", "description");
        var sameNameDifferentPath = Identity(firstSet, "/catalog/other/code", "code");
        environment.Configuration.Synchronize(
            [first, second, selected, absent, sameNameDifferentPath]);
        environment.Configuration.SetBlacklisted([first, second], isBlacklisted: true);
        environment.Configuration.SetSelection([selected], isSelected: true);
        environment.Configuration.SetDatabaseTagOverride(first, "Mapped Code");
        environment.Configuration.SetRepeatedDataLayout(
            firstSet,
            RepeatedDataLayout.StructuralRows);
        environment.Configuration.SetRepeatedDataLayout(
            secondSet,
            RepeatedDataLayout.AllCombinations);
        var overrides = environment.Configuration.DatabaseTagOverridesByIdentity;
        var layouts = environment.Configuration.RepeatedDataLayouts;

        var saved = environment.Coordinator.SaveNew("Shared blacklist");

        Assert.IsTrue(saved.Succeeded, saved.Message);
        var sourceProfile = saved.Profile!;
        var sourceBytes = File.ReadAllBytes(sourceProfile.Path);
        var artifact = environment.Store.FindById(sourceProfile.ProfileId).Artifact!;
        Assert.AreEqual(ProfileKind.Blacklist, artifact.ProfileKind);
        Assert.AreEqual(ProfileArtifactV1.CurrentSchemaVersion, artifact.SchemaVersion);
        Assert.AreEqual(
            $"{sourceProfile.ProfileId}{ProfileArtifactStore.FileExtension}",
            Path.GetFileName(sourceProfile.Path));
        var content = (BlacklistProfileContentV1)artifact.Content;
        Assert.HasCount(1, content.Entries);
        Assert.AreEqual(PortableDiscoveryInformationIdentity.From(first), content.Entries[0]);
        var json = Encoding.UTF8.GetString(sourceBytes);
        Assert.IsFalse(json.Contains("sourceSetId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("sourceId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("Mapped Code", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("repeatedDataLayout", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("selected", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("default", StringComparison.OrdinalIgnoreCase));

        var designated = environment.Coordinator.SetDefault(sourceProfile);
        Assert.IsTrue(designated.Succeeded, designated.Message);
        CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourceProfile.Path));

        using var freshWorkflow = CreateWorkflow();
        var freshConfiguration = new ActiveDiscoveryConfiguration();
        var freshCoordinator = CreateCoordinator(
            environment.Store,
            environment.DesignationStore,
            freshConfiguration,
            freshWorkflow);
        await freshCoordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(BlacklistSessionDefaultStatus.Resolved, freshCoordinator.SessionDefault.Status);
        Assert.AreEqual(sourceProfile.ProfileId, freshCoordinator.SessionDefault.ProfileId);
        Assert.HasCount(1, freshCoordinator.SessionDefault.Content!.Entries);
        Assert.IsEmpty(freshConfiguration.Current.Items);

        var cloned = environment.Coordinator.Clone(sourceProfile, "Cloned blacklist");
        Assert.IsTrue(cloned.Succeeded, cloned.Message);
        Assert.AreNotEqual(sourceProfile.ProfileId, cloned.Profile!.ProfileId);
        Assert.AreEqual(7, cloned.Profile.ProfileId.Value.Version);
        var cloneContent = (BlacklistProfileContentV1)environment.Store
            .FindById(cloned.Profile.ProfileId).Artifact!.Content;
        CollectionAssert.AreEqual(content.Entries.ToArray(), cloneContent.Entries.ToArray());
        CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourceProfile.Path));
        Assert.AreEqual(sourceProfile.ProfileId, environment.Coordinator.Inventory.DefaultProfileId);
        Assert.IsFalse(cloned.Profile.IsDefault);

        environment.Configuration.SetBlacklisted([first, second], isBlacklisted: false);
        environment.Configuration.SetSelection([second], isSelected: true);
        environment.Configuration.SetBlacklisted(
            [absent, sameNameDifferentPath],
            isBlacklisted: true);
        var workflowEvents = 0;
        environment.Workflow.StateChanged += (_, _) => workflowEvents++;

        var loaded = environment.Coordinator.Load(sourceProfile);

        Assert.IsTrue(loaded.Succeeded, loaded.Message);
        Assert.AreEqual(4, loaded.ChangedCount);
        Assert.AreEqual(1, loaded.MatchedCount);
        Assert.AreEqual(0, loaded.UnmatchedCount);
        Assert.AreEqual(1, workflowEvents);
        var dispositions = environment.Configuration.Current.Items.ToDictionary(
            item => item.Identity,
            item => item.Disposition);
        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted, dispositions[first]);
        Assert.AreEqual(DiscoveryInformationDisposition.Blacklisted, dispositions[second]);
        Assert.AreEqual(DiscoveryInformationDisposition.Selected, dispositions[selected]);
        Assert.AreEqual(DiscoveryInformationDisposition.Neutral, dispositions[absent]);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            dispositions[sameNameDifferentPath]);
        CollectionAssert.AreEquivalent(
            overrides.ToArray(),
            environment.Configuration.DatabaseTagOverridesByIdentity.ToArray());
        CollectionAssert.AreEquivalent(
            layouts.ToArray(),
            environment.Configuration.RepeatedDataLayouts.ToArray());
        CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourceProfile.Path));
    }

    [TestMethod]
    public async Task MixedMultiSetBlacklistRejectsSaveAndUpdateWithoutChangingArtifact()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var first = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        var second = Identity(SourceSetId.CreateNew(), first.StructuralPath, "code");
        environment.Configuration.Synchronize([first, second]);
        environment.Configuration.SetBlacklisted([first, second], isBlacklisted: true);
        var saved = environment.Coordinator.SaveNew("Consistent");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        var bytes = File.ReadAllBytes(saved.Profile!.Path);
        environment.Configuration.SetBlacklisted(second, isBlacklisted: false);

        var rejectedSave = environment.Coordinator.SaveNew("Conflicting");
        var rejectedUpdate = environment.Coordinator.Update(saved.Profile);

        Assert.IsFalse(environment.Coordinator.CanCaptureCurrentConfiguration);
        Assert.IsFalse(rejectedSave.Succeeded);
        Assert.IsFalse(rejectedUpdate.Succeeded);
        StringAssert.Contains(rejectedSave.Message, "conflicting blacklist states");
        StringAssert.Contains(rejectedUpdate.Message, "conflicting blacklist states");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(saved.Profile.Path));
        Assert.HasCount(1, environment.Coordinator.RefreshInventory().Profiles);
    }

    [TestMethod]
    public async Task EmptyBlacklistIsRepresentableAndSavesNoEntries()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var neutral = Identity(SourceSetId.CreateNew(), "/catalog/name", "name");
        var selected = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([neutral, selected]);
        environment.Configuration.SetSelection([selected], isSelected: true);

        var saved = environment.Coordinator.SaveNew("Empty blacklist");

        Assert.IsTrue(saved.Succeeded, saved.Message);
        var content = (BlacklistProfileContentV1)environment.Store
            .FindById(saved.Profile!.ProfileId).Artifact!.Content;
        Assert.IsEmpty(content.Entries);
    }

    [TestMethod]
    public async Task LoadReplacesBlacklistDimensionReportsUnmatchedAndNoOpDoesNotInvalidate()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var match = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        var selectedAbsent = Identity(SourceSetId.CreateNew(), "/catalog/name", "name");
        var neutralAbsent = Identity(SourceSetId.CreateNew(), "/catalog/description", "description");
        environment.Configuration.Synchronize([match, selectedAbsent, neutralAbsent]);
        environment.Configuration.SetBlacklisted(match, isBlacklisted: true);
        environment.Configuration.SetSelection([selectedAbsent], isSelected: true);
        var artifact = CreateBlacklistArtifact(
            "With missing",
            PortableDiscoveryInformationIdentity.From(match),
            Portable("/missing/value", "value"));
        var stored = StoreArtifact(environment.Store, artifact);
        var selectedProfile = environment.Coordinator.RefreshInventory().Profiles.Single();
        var originalBytes = File.ReadAllBytes(stored.Path!);
        var events = 0;
        environment.Workflow.StateChanged += (_, _) => events++;

        var result = environment.Coordinator.Load(selectedProfile);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(1, result.MatchedCount);
        Assert.AreEqual(1, result.UnmatchedCount);
        Assert.AreEqual(0, result.ChangedCount);
        Assert.AreEqual(0, events);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Extraction);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            environment.Configuration.Current.Items.Single(
                item => item.Identity == selectedAbsent).Disposition);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            environment.Configuration.Current.Items.Single(
                item => item.Identity == neutralAbsent).Disposition);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(stored.Path!));
    }

    [TestMethod]
    public async Task UpdateIsMonotonicPreservesIdentityDefaultAndRejectsStaleFingerprint()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        var saved = environment.Coordinator.SaveNew("Stable");
        Assert.IsTrue(saved.Succeeded, saved.Message);
        Assert.IsTrue(environment.Coordinator.SetDefault(saved.Profile!).Succeeded);
        var designationBytes = File.ReadAllBytes(environment.DesignationStore.DesignationPath);
        environment.Configuration.SetBlacklisted(identity, isBlacklisted: true);

        var updated = environment.Coordinator.Update(saved.Profile!);

        Assert.IsTrue(updated.Succeeded, updated.Message);
        Assert.AreEqual(saved.Profile!.ProfileId, updated.Profile!.ProfileId);
        Assert.AreEqual(saved.Profile.Name, updated.Profile.Name);
        Assert.AreEqual(saved.Profile.CreatedAtUtc, updated.Profile.CreatedAtUtc);
        Assert.IsGreaterThan(saved.Profile.UpdatedAtUtc, updated.Profile.UpdatedAtUtc);
        Assert.AreEqual(updated.Profile.ProfileId, environment.Coordinator.Inventory.DefaultProfileId);
        CollectionAssert.AreEqual(
            designationBytes,
            File.ReadAllBytes(environment.DesignationStore.DesignationPath));
        Assert.AreEqual(updated.Profile.UpdatedAtUtc,
            environment.Coordinator.SessionDefault.Profile!.UpdatedAtUtc);
        var bytes = File.ReadAllBytes(updated.Profile.Path);

        var stale = environment.Coordinator.Update(saved.Profile);
        var staleDefault = environment.Coordinator.SetDefault(saved.Profile);
        Assert.IsFalse(stale.Succeeded);
        Assert.IsTrue(stale.RequiresReselection);
        Assert.IsFalse(staleDefault.Succeeded);
        Assert.IsTrue(staleDefault.RequiresReselection);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(updated.Profile.Path));
        CollectionAssert.AreEqual(
            designationBytes,
            File.ReadAllBytes(environment.DesignationStore.DesignationPath));
    }

    [TestMethod]
    public async Task MaximumTimestampRejectsUpdateWithoutMutation()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        var createdAt = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        var artifact = CreateBlacklistArtifact(
            "Maximum",
            createdAt,
            DateTimeOffset.MaxValue,
            PortableDiscoveryInformationIdentity.From(identity));
        var stored = StoreArtifact(environment.Store, artifact);
        var coordinator = CreateCoordinator(
            environment.Store,
            environment.DesignationStore,
            environment.Configuration,
            environment.Workflow,
            new FixedTimeProvider(DateTimeOffset.MaxValue));
        var selected = coordinator.RefreshInventory().Profiles.Single();
        var bytes = File.ReadAllBytes(stored.Path!);

        var result = coordinator.Update(selected);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "cannot be advanced");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(stored.Path!));
        Assert.AreEqual(stored.Fingerprint, environment.Store.FindById(artifact.ProfileId).Fingerprint);
    }

    [TestMethod]
    public async Task InventoryIncludesCopiedBlacklistAndIsolatesOtherKindsAndFailures()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var first = CreateBlacklistArtifact("First", Portable("/one", "one"));
        var copied = CreateBlacklistArtifact("Copied", Portable("/two", "two"));
        var information = CreateInformationSelectionArtifact("Selection");
        StoreArtifact(environment.Store, first);
        StoreArtifact(environment.Store, information);
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        var copiedPath = Path.Combine(
            environment.Paths.ProfilesDirectory,
            "manual-copy.cia-profile.json");
        File.WriteAllBytes(copiedPath, ProfileArtifactStore.Serialize(copied));
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "bad.cia-profile.json"),
            "{bad-json");

        var inventory = environment.Coordinator.RefreshInventory();

        Assert.HasCount(2, inventory.Profiles);
        Assert.IsTrue(inventory.Profiles.Any(profile => profile.Name == "Copied"));
        Assert.IsFalse(inventory.Profiles.Any(profile => profile.Name == "Selection"));
        Assert.HasCount(1, inventory.Problems);
        Assert.IsFalse(inventory.Profiles.Any(profile => string.Equals(
            Path.GetFileName(profile.Path),
            DefaultBlacklistProfileDesignationStore.FileName,
            StringComparison.OrdinalIgnoreCase)));

        File.Copy(
            copiedPath,
            Path.Combine(environment.Paths.ProfilesDirectory, "duplicate.cia-profile.json"));
        inventory = environment.Coordinator.RefreshInventory();
        Assert.HasCount(1, inventory.Profiles);
        Assert.AreEqual("First", inventory.Profiles.Single().Name);
        Assert.HasCount(3, inventory.Problems);
    }

    [TestMethod]
    public async Task DefaultDesignationChangesOnlyMarkerAndClearDeletesNoProfile()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetBlacklisted(identity, isBlacklisted: true);
        var first = environment.Coordinator.SaveNew("First").Profile!;
        environment.Configuration.SetBlacklisted(identity, isBlacklisted: false);
        var second = environment.Coordinator.Clone(first, "Second").Profile!;
        var firstBytes = File.ReadAllBytes(first.Path);
        var secondBytes = File.ReadAllBytes(second.Path);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            environment.Configuration.Current.Items.Single().Disposition);

        Assert.IsTrue(environment.Coordinator.SetDefault(first).Succeeded);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            environment.Configuration.Current.Items.Single().Disposition);
        var firstMarker = File.ReadAllBytes(environment.DesignationStore.DesignationPath);
        var markerText = Encoding.UTF8.GetString(firstMarker);
        StringAssert.Contains(markerText, first.ProfileId.ToString());
        StringAssert.Contains(markerText, "schemaVersion");
        Assert.IsFalse(markerText.Contains(first.Name, StringComparison.Ordinal));
        Assert.IsFalse(markerText.Contains(first.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markerText.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment.Coordinator.SetDefault(second).Succeeded);
        var secondMarker = File.ReadAllBytes(environment.DesignationStore.DesignationPath);

        CollectionAssert.AreNotEqual(firstMarker, secondMarker);
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(first.Path));
        CollectionAssert.AreEqual(secondBytes, File.ReadAllBytes(second.Path));
        Assert.AreEqual(second.ProfileId, environment.Coordinator.Inventory.DefaultProfileId);
        Assert.IsTrue(environment.Coordinator.ClearDefault().Succeeded);
        Assert.IsFalse(File.Exists(environment.DesignationStore.DesignationPath));
        Assert.IsTrue(File.Exists(first.Path));
        Assert.IsTrue(File.Exists(second.Path));
        Assert.IsNull(environment.Coordinator.Inventory.DefaultProfileId);
    }

    [TestMethod]
    public async Task FailedDesignationReplacementPreservesPreviousValidMarker()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var first = ProfileId.CreateNew();
        var second = ProfileId.CreateNew();
        Assert.IsTrue(environment.DesignationStore.Set(first).Succeeded);
        var bytes = File.ReadAllBytes(environment.DesignationStore.DesignationPath);
        var failing = new DefaultBlacklistProfileDesignationStore(
            environment.Paths,
            new FailingReplaceDesignationOperations());

        var result = failing.Set(second);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(failing.DesignationPath));
        Assert.AreEqual(first, failing.Read().ProfileId);
    }

    [TestMethod]
    public async Task ConcurrentDesignationWritersPublishOneStrictValidMarker()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var first = ProfileId.CreateNew();
        var second = ProfileId.CreateNew();
        var otherInstance = new DefaultBlacklistProfileDesignationStore(environment.Paths);

        var results = await Task.WhenAll(
            Task.Run(() => environment.DesignationStore.Set(first)),
            Task.Run(() => otherInstance.Set(second)));

        Assert.IsTrue(results.All(result => result.Succeeded));
        var published = environment.DesignationStore.Read();
        Assert.AreEqual(DefaultBlacklistProfileDesignationState.Valid, published.State);
        Assert.IsTrue(published.ProfileId == first || published.ProfileId == second);
        Assert.IsFalse(Directory.EnumerateFiles(
            environment.Paths.ProfilesDirectory,
            "*.incomplete",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public async Task MalformedAndFutureDesignationFailClosedWithoutRepair()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        File.WriteAllText(environment.DesignationStore.DesignationPath, "{bad-json");
        var malformedBytes = File.ReadAllBytes(environment.DesignationStore.DesignationPath);

        var malformed = environment.Coordinator.ResolveSessionDefault();

        Assert.AreEqual(BlacklistSessionDefaultStatus.Invalid, malformed.Status);
        CollectionAssert.AreEqual(
            malformedBytes,
            File.ReadAllBytes(environment.DesignationStore.DesignationPath));
        Assert.IsFalse(environment.DesignationStore.Set(ProfileId.CreateNew()).Succeeded);

        var strictId = ProfileId.CreateNew();
        File.WriteAllText(
            environment.DesignationStore.DesignationPath,
            $$"""
            {
              "schemaVersion": 1,
              "profileId": "{{strictId}}",
              "unexpected": true
            }
            """);
        Assert.AreEqual(
            DefaultBlacklistProfileDesignationState.MalformedOrInvalid,
            environment.DesignationStore.Read().State);

        var futureId = ProfileId.CreateNew();
        File.WriteAllText(
            environment.DesignationStore.DesignationPath,
            $$"""
            {
              "schemaVersion": 2,
              "profileId": "{{futureId}}"
            }
            """);
        var future = environment.DesignationStore.Read();
        Assert.AreEqual(DefaultBlacklistProfileDesignationState.UnsupportedSchema, future.State);
        Assert.AreEqual(
            BlacklistSessionDefaultStatus.Invalid,
            environment.Coordinator.ResolveSessionDefault().Status);
    }

    [TestMethod]
    public async Task MissingWrongKindAndDuplicateDesignatedProfilesFailClosed()
    {
        using var missingEnvironment = await BlacklistEnvironment.CreateAsync();
        var missingId = ProfileId.CreateNew();
        Assert.IsTrue(missingEnvironment.DesignationStore.Set(missingId).Succeeded);
        var missing = missingEnvironment.Coordinator.ResolveSessionDefault();
        Assert.AreEqual(BlacklistSessionDefaultStatus.Invalid, missing.Status);
        Assert.AreEqual(missingId, missing.ProfileId);

        using var wrongKindEnvironment = await BlacklistEnvironment.CreateAsync();
        var information = CreateInformationSelectionArtifact("Wrong kind");
        StoreArtifact(wrongKindEnvironment.Store, information);
        Assert.IsTrue(wrongKindEnvironment.DesignationStore.Set(information.ProfileId).Succeeded);
        var wrongKind = wrongKindEnvironment.Coordinator.ResolveSessionDefault();
        Assert.AreEqual(BlacklistSessionDefaultStatus.Invalid, wrongKind.Status);
        Assert.IsNull(wrongKind.Profile);

        using var duplicateEnvironment = await BlacklistEnvironment.CreateAsync();
        var blacklist = CreateBlacklistArtifact("Duplicate", Portable("/code", "code"));
        var stored = StoreArtifact(duplicateEnvironment.Store, blacklist);
        File.Copy(
            stored.Path!,
            Path.Combine(
                duplicateEnvironment.Paths.ProfilesDirectory,
                "copy.cia-profile.json"));
        Assert.IsTrue(duplicateEnvironment.DesignationStore.Set(blacklist.ProfileId).Succeeded);
        var duplicate = duplicateEnvironment.Coordinator.ResolveSessionDefault();
        Assert.AreEqual(BlacklistSessionDefaultStatus.Invalid, duplicate.Status);
        Assert.IsNull(duplicate.Profile);
    }

    [TestMethod]
    public async Task DeleteHonorsKindFingerprintAndDefaultDesignationWithoutUndoingBlacklist()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetBlacklisted(identity, isBlacklisted: true);
        var first = environment.Coordinator.SaveNew("Default").Profile!;
        var second = environment.Coordinator.Clone(first, "Other").Profile!;
        Assert.IsTrue(environment.Coordinator.SetDefault(first).Succeeded);

        var deletedOther = environment.Coordinator.Delete(second);
        Assert.IsTrue(deletedOther.Succeeded, deletedOther.Message);
        Assert.AreEqual(first.ProfileId, environment.Coordinator.Inventory.DefaultProfileId);

        var current = environment.Store.FindById(first.ProfileId).Artifact!;
        var replacement = current with { UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1) };
        var replacementWrite = environment.Store.Update(replacement, first.Fingerprint);
        Assert.IsTrue(replacementWrite.Succeeded, replacementWrite.Problem);
        var replacementBytes = File.ReadAllBytes(replacementWrite.Path!);
        var stale = environment.Coordinator.Delete(first);
        Assert.IsFalse(stale.Succeeded);
        CollectionAssert.AreEqual(replacementBytes, File.ReadAllBytes(replacementWrite.Path!));

        var refreshed = environment.Coordinator.RefreshInventory().Profiles.Single();
        var deletedDefault = environment.Coordinator.Delete(refreshed);
        Assert.IsTrue(deletedDefault.Succeeded, deletedDefault.Message);
        Assert.IsFalse(File.Exists(environment.DesignationStore.DesignationPath));
        Assert.AreEqual(BlacklistSessionDefaultStatus.NotConfigured,
            environment.Coordinator.SessionDefault.Status);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            environment.Configuration.Current.Items.Single().Disposition);

        var information = CreateInformationSelectionArtifact("Cannot delete here");
        var storedInformation = StoreArtifact(environment.Store, information);
        var forged = new BlacklistProfileItem(
            information.ProfileId,
            information.Name,
            information.CreatedAtUtc,
            information.UpdatedAtUtc,
            storedInformation.Path!,
            storedInformation.Fingerprint!.Value,
            IsDefault: false);
        var wrongKind = environment.Coordinator.Delete(forged);
        Assert.IsFalse(wrongKind.Succeeded);
        Assert.IsTrue(File.Exists(storedInformation.Path));
    }

    [TestMethod]
    public async Task FailedDefaultClearAfterDeletionReportsPartialOutcomeAndFailsClosed()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        var selected = environment.Coordinator.SaveNew("Default").Profile!;
        Assert.IsTrue(environment.DesignationStore.Set(selected.ProfileId).Succeeded);
        var failingDesignationStore = new DefaultBlacklistProfileDesignationStore(
            environment.Paths,
            new FailingDeleteDesignationOperations());
        var coordinator = CreateCoordinator(
            environment.Store,
            failingDesignationStore,
            environment.Configuration,
            environment.Workflow);
        coordinator.RefreshInventory();

        var result = coordinator.Delete(coordinator.Inventory.Profiles.Single());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.RequiresReselection);
        StringAssert.Contains(result.Message, "Deleted blacklist profile");
        StringAssert.Contains(result.Message, "could not be cleared");
        Assert.IsFalse(File.Exists(selected.Path));
        Assert.IsTrue(File.Exists(failingDesignationStore.DesignationPath));
        Assert.AreEqual(BlacklistSessionDefaultStatus.Invalid, coordinator.SessionDefault.Status);
        Assert.IsNull(coordinator.SessionDefault.Profile);
    }

    [TestMethod]
    public async Task ProfileCrudAndDefaultCommandsDoNotInvalidateWorkflow()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        environment.Configuration.SetBlacklisted(identity, isBlacklisted: true);
        var events = 0;
        environment.Workflow.StateChanged += (_, _) => events++;

        var saved = environment.Coordinator.SaveNew("One");
        var updated = environment.Coordinator.Update(saved.Profile!);
        var clone = environment.Coordinator.Clone(updated.Profile!, "Two");
        var setDefault = environment.Coordinator.SetDefault(updated.Profile!);
        var clearDefault = environment.Coordinator.ClearDefault();
        var deleted = environment.Coordinator.Delete(clone.Profile!);

        Assert.IsTrue(saved.Succeeded && updated.Succeeded && clone.Succeeded);
        Assert.IsTrue(setDefault.Succeeded && clearDefault.Succeeded && deleted.Succeeded);
        Assert.AreEqual(0, events);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Current, environment.Workflow.Current.Extraction);
    }

    [TestMethod]
    public async Task BlacklistDeleteConfirmationDeclineAndConfirmUseSharedCiaFlow()
    {
        using var declinedEnvironment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        declinedEnvironment.Configuration.Synchronize([identity]);
        var declinedProfile = declinedEnvironment.Coordinator.SaveNew("Keep").Profile!;
        var declinedBytes = File.ReadAllBytes(declinedProfile.Path);
        var recordingDeclined = new RecordingBlacklistCoordinator(
            declinedEnvironment.Coordinator);
        var decline = new StubDeleteConfirmation(accepted: false);
        using (var viewModel = CreateViewModel(
                   declinedEnvironment,
                   recordingDeclined,
                   decline))
        {
            Assert.IsFalse(viewModel.DeleteBlacklistProfileCommand.CanExecute(null));
            viewModel.SelectedBlacklistProfile = viewModel.BlacklistProfiles.Single();
            await viewModel.DeleteBlacklistProfileCommand.ExecuteAsync(null);
            Assert.AreEqual(1, decline.RequestCount);
            Assert.AreEqual("Keep", decline.RequestedProfileName);
            Assert.AreEqual(0, recordingDeclined.DeleteCount);
            Assert.HasCount(1, viewModel.BlacklistProfiles);
            Assert.IsNotNull(viewModel.SelectedBlacklistProfile);
            Assert.AreEqual("Profile deletion cancelled.", viewModel.BlacklistProfileStatusText);
            CollectionAssert.AreEqual(declinedBytes, File.ReadAllBytes(declinedProfile.Path));
        }

        using var confirmedEnvironment = await BlacklistEnvironment.CreateAsync();
        confirmedEnvironment.Configuration.Synchronize([identity]);
        var confirmedProfile = confirmedEnvironment.Coordinator.SaveNew("Delete").Profile!;
        var recordingConfirmed = new RecordingBlacklistCoordinator(
            confirmedEnvironment.Coordinator);
        using var confirmedViewModel = CreateViewModel(
            confirmedEnvironment,
            recordingConfirmed,
            new StubDeleteConfirmation(accepted: true));
        confirmedViewModel.SelectedBlacklistProfile = confirmedViewModel.BlacklistProfiles.Single();
        await confirmedViewModel.DeleteBlacklistProfileCommand.ExecuteAsync(null);
        Assert.AreEqual(1, recordingConfirmed.DeleteCount);
        Assert.IsFalse(File.Exists(confirmedProfile.Path));
        Assert.IsEmpty(confirmedViewModel.BlacklistProfiles);
        Assert.IsNull(confirmedViewModel.SelectedBlacklistProfile);
    }

    [TestMethod]
    public async Task ConfirmedStaleBlacklistDeletePreservesReplacement()
    {
        using var environment = await BlacklistEnvironment.CreateAsync();
        var identity = Identity(SourceSetId.CreateNew(), "/catalog/code", "code");
        environment.Configuration.Synchronize([identity]);
        var selected = environment.Coordinator.SaveNew("Changed externally").Profile!;
        byte[]? replacementBytes = null;
        var confirmation = new StubDeleteConfirmation(
            accepted: true,
            onConfirm: () =>
            {
                var current = environment.Store.FindById(selected.ProfileId).Artifact!;
                var replacement = current with
                {
                    UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
                };
                var updated = environment.Store.Update(replacement, selected.Fingerprint);
                Assert.IsTrue(updated.Succeeded, updated.Problem);
                replacementBytes = File.ReadAllBytes(updated.Path!);
            });
        var recording = new RecordingBlacklistCoordinator(environment.Coordinator);
        using var viewModel = CreateViewModel(environment, recording, confirmation);
        viewModel.SelectedBlacklistProfile = viewModel.BlacklistProfiles.Single();

        await viewModel.DeleteBlacklistProfileCommand.ExecuteAsync(null);

        Assert.AreEqual(1, recording.DeleteCount);
        Assert.IsNotNull(replacementBytes);
        CollectionAssert.AreEqual(replacementBytes, File.ReadAllBytes(selected.Path));
        Assert.IsNull(viewModel.SelectedBlacklistProfile);
        StringAssert.Contains(viewModel.BlacklistProfileStatusText, "changed after it was listed");
    }

    [TestMethod]
    public void DiscoveryViewExposesCompleteBlacklistProfileManagementWithoutNewTab()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CIA.Desktop",
            "Views",
            "DiscoveryWorkspaceView.xaml"));

        StringAssert.Contains(xaml, "BlacklistProfiles");
        StringAssert.Contains(xaml, "LoadBlacklistProfileCommand");
        StringAssert.Contains(xaml, "SaveNewBlacklistProfileCommand");
        StringAssert.Contains(xaml, "UpdateBlacklistProfileCommand");
        StringAssert.Contains(xaml, "CloneBlacklistProfileCommand");
        StringAssert.Contains(xaml, "DeleteBlacklistProfileCommand");
        StringAssert.Contains(xaml, "SetDefaultBlacklistProfileCommand");
        StringAssert.Contains(xaml, "ClearDefaultBlacklistProfileCommand");
        StringAssert.Contains(xaml, "CiaDangerButtonStyle");
        Assert.IsFalse(xaml.Contains("IsEnabled=\"False\" SelectedIndex=\"0\"", StringComparison.Ordinal));
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

    private static PortableDiscoveryInformationIdentity Portable(
        string path,
        string informationType) =>
        new(
            path,
            informationType,
            SourceValueCandidateKind.Element,
            $"Element:{path}");

    private static ProfileArtifactV1 CreateBlacklistArtifact(
        string name,
        params PortableDiscoveryInformationIdentity[] entries)
    {
        var now = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        return CreateBlacklistArtifact(name, now, now, entries);
    }

    private static ProfileArtifactV1 CreateBlacklistArtifact(
        string name,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        params PortableDiscoveryInformationIdentity[] entries) =>
        new(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.Blacklist,
            name,
            createdAtUtc,
            updatedAtUtc,
            new BlacklistProfileContentV1(entries));

    private static ProfileArtifactV1 CreateInformationSelectionArtifact(string name)
    {
        var now = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            ProfileId.CreateNew(),
            ProfileKind.InformationSelection,
            name,
            now,
            now,
            new InformationSelectionProfileContentV1([]));
    }

    private static ProfileArtifactWriteResult StoreArtifact(
        ProfileArtifactStore store,
        ProfileArtifactV1 artifact)
    {
        var result = store.Create(
            $"{artifact.ProfileId}{ProfileArtifactStore.FileExtension}",
            artifact);
        Assert.IsTrue(result.Succeeded, result.Problem);
        return result;
    }

    private static BlacklistProfileCoordinator CreateCoordinator(
        ProfileArtifactStore store,
        DefaultBlacklistProfileDesignationStore designationStore,
        ActiveDiscoveryConfiguration configuration,
        IApplicationWorkflowCoordinator workflow,
        TimeProvider? timeProvider = null) =>
        new(
            store,
            designationStore,
            configuration,
            workflow,
            NullLogger<BlacklistProfileCoordinator>.Instance,
            timeProvider);

    private static ApplicationWorkflowCoordinator CreateWorkflow() =>
        new(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());

    private static DiscoveryWorkspaceViewModel CreateViewModel(
        BlacklistEnvironment environment,
        IBlacklistProfileCoordinator coordinator,
        IProfileDeleteConfirmation confirmation) =>
        new(
            new ThrowingDiscoveryClient(),
            environment.Configuration,
            new ActiveLoadedSourceSet(),
            environment.Workflow,
            profileDeleteConfirmation: confirmation,
            blacklistProfileCoordinator: coordinator);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CIA.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private sealed class BlacklistEnvironment : IDisposable
    {
        private static readonly string SafeRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR91.Tests");

        private BlacklistEnvironment(string root)
        {
            Root = root;
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(root, "LocalAppData"));
            Store = new ProfileArtifactStore(Paths);
            DesignationStore = new DefaultBlacklistProfileDesignationStore(Paths);
            Configuration = new ActiveDiscoveryConfiguration();
            Workflow = CreateWorkflow();
            Coordinator = CreateCoordinator(
                Store,
                DesignationStore,
                Configuration,
                Workflow);
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public ProfileArtifactStore Store { get; }

        public DefaultBlacklistProfileDesignationStore DesignationStore { get; }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public BlacklistProfileCoordinator Coordinator { get; }

        public static async Task<BlacklistEnvironment> CreateAsync()
        {
            var environment = new BlacklistEnvironment(
                Path.Combine(SafeRoot, Guid.NewGuid().ToString("N")));
            environment.Workflow.RecordSourceSelectionChanged(hasValidSourceSelection: true);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.Discovery);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.DatabaseBuild);
            await CompleteAsync(environment.Workflow, WorkflowOperationKind.Extraction);
            environment.Coordinator.RefreshInventory();
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
                    "Refusing to remove a blacklist-profile test directory outside its safe root.");
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

    private sealed class FailingReplaceDesignationOperations :
        IDefaultBlacklistProfileDesignationFileOperations
    {
        private readonly DefaultBlacklistProfileDesignationFileOperations _inner = new();

        public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content) =>
            _inner.WriteCandidate(candidatePath, content);

        public void PublishNew(string candidatePath, string targetPath) =>
            _inner.PublishNew(candidatePath, targetPath);

        public void Replace(string candidatePath, string targetPath) =>
            throw new IOException("Injected designation replacement failure.");

        public void DeleteCandidate(string candidatePath) =>
            _inner.DeleteCandidate(candidatePath);

        public void DeleteDesignation(string targetPath) =>
            _inner.DeleteDesignation(targetPath);
    }

    private sealed class FailingDeleteDesignationOperations :
        IDefaultBlacklistProfileDesignationFileOperations
    {
        private readonly DefaultBlacklistProfileDesignationFileOperations _inner = new();

        public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content) =>
            _inner.WriteCandidate(candidatePath, content);

        public void PublishNew(string candidatePath, string targetPath) =>
            _inner.PublishNew(candidatePath, targetPath);

        public void Replace(string candidatePath, string targetPath) =>
            _inner.Replace(candidatePath, targetPath);

        public void DeleteCandidate(string candidatePath) =>
            _inner.DeleteCandidate(candidatePath);

        public void DeleteDesignation(string targetPath) =>
            throw new IOException("Injected designation deletion failure.");
    }

    private sealed class RecordingBlacklistCoordinator(IBlacklistProfileCoordinator inner) :
        IBlacklistProfileCoordinator
    {
        public int DeleteCount { get; private set; }

        public BlacklistProfileInventory Inventory => inner.Inventory;

        public BlacklistSessionDefaultSnapshot SessionDefault => inner.SessionDefault;

        public bool CanCaptureCurrentConfiguration => inner.CanCaptureCurrentConfiguration;

        public string CaptureReadinessReason => inner.CaptureReadinessReason;

        public bool CanLoadCurrentConfiguration => inner.CanLoadCurrentConfiguration;

        public BlacklistProfileInventory RefreshInventory() => inner.RefreshInventory();

        public BlacklistSessionDefaultSnapshot ResolveSessionDefault() =>
            inner.ResolveSessionDefault();

        public BlacklistProfileOperationResult SaveNew(string name) => inner.SaveNew(name);

        public BlacklistProfileOperationResult Load(BlacklistProfileItem selectedProfile) =>
            inner.Load(selectedProfile);

        public BlacklistProfileOperationResult Update(BlacklistProfileItem selectedProfile) =>
            inner.Update(selectedProfile);

        public BlacklistProfileOperationResult Clone(
            BlacklistProfileItem selectedProfile,
            string newName) => inner.Clone(selectedProfile, newName);

        public BlacklistProfileOperationResult Delete(BlacklistProfileItem selectedProfile)
        {
            DeleteCount++;
            return inner.Delete(selectedProfile);
        }

        public BlacklistProfileOperationResult SetDefault(BlacklistProfileItem selectedProfile) =>
            inner.SetDefault(selectedProfile);

        public BlacklistProfileOperationResult ClearDefault() => inner.ClearDefault();
    }

    private sealed class StubDeleteConfirmation(
        bool accepted,
        Action? onConfirm = null) : IProfileDeleteConfirmation
    {
        public bool IsOpen => false;

        public string? ProfileName => null;

        public int RequestCount { get; private set; }

        public string? RequestedProfileName { get; private set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<bool> ConfirmAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestedProfileName = profileName;
            onConfirm?.Invoke();
            return Task.FromResult(accepted);
        }

        public void Accept()
        {
        }

        public void Decline()
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowingDiscoveryClient : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery is not used by blacklist-profile tests.");

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Occurrence preview is not used by blacklist-profile tests.");
    }
}
