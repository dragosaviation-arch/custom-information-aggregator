using System.Text;
using System.Text.Json.Nodes;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;
using CIA.Core.Profiles;
using CIA.Core.Runtime;
using CIA.ProcessingHost.SourceIntake;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ProfileArtifactStoreTests
{
    [TestMethod]
    public void InformationSelectionProfileV1RoundTripsAsOneCopyableFile()
    {
        using var environment = new ProfileTestEnvironment();
        var artifact = CreateInformationSelection();

        var write = environment.Store.Create("selection.cia-profile.json", artifact);
        var inventory = environment.Store.CreateInventory();

        Assert.IsTrue(write.Succeeded, write.Problem);
        Assert.HasCount(1, GetPublishedProfilePaths(environment.Paths.ProfilesDirectory));
        Assert.HasCount(1, inventory.ValidProfiles);
        var loaded = inventory.ValidProfiles.Single();
        Assert.AreEqual(artifact.ProfileId, loaded.ProfileId);
        Assert.AreEqual(ProfileKind.InformationSelection, loaded.ProfileKind);
        Assert.AreEqual(artifact.Name, loaded.Name);
        var content = (InformationSelectionProfileContentV1)loaded.Content;
        Assert.HasCount(2, content.Entries);
        Assert.AreEqual(InformationSelectionMembership.Selected, content.Entries[0].Membership);
        Assert.AreEqual(InformationSelectionMembership.Excluded, content.Entries[1].Membership);
    }

    [TestMethod]
    public void BlacklistProfileV1RoundTripsWithSeparateTypedContent()
    {
        using var environment = new ProfileTestEnvironment();
        var artifact = CreateBlacklist();

        var write = environment.Store.Create("blacklist.cia-profile.json", artifact);
        var loaded = environment.Store.Inspect(write.Path!).Artifact!;

        Assert.IsTrue(write.Succeeded, write.Problem);
        Assert.AreEqual(ProfileKind.Blacklist, loaded.ProfileKind);
        var content = (BlacklistProfileContentV1)loaded.Content;
        Assert.HasCount(2, content.Entries);
        Assert.AreEqual("code", content.Entries[0].InformationType);
    }

    [TestMethod]
    public void CopiedProfileIsDiscoveredInFreshApplicationContextWithoutRegistration()
    {
        using var source = new ProfileTestEnvironment();
        using var destination = new ProfileTestEnvironment();
        var artifact = CreateInformationSelection();
        var created = source.Store.Create("renamable.cia-profile.json", artifact);
        Directory.CreateDirectory(destination.Paths.ProfilesDirectory);
        var copiedPath = Path.Combine(
            destination.Paths.ProfilesDirectory,
            "manually-renamed.cia-profile.json");
        File.Copy(created.Path!, copiedPath);

        var freshStore = new ProfileArtifactStore(destination.Paths);
        var copied = freshStore.CreateInventory().ValidProfiles.Single();

        Assert.AreEqual(artifact.ProfileId, copied.ProfileId);
        Assert.AreEqual(artifact.ProfileKind, copied.ProfileKind);
        Assert.AreEqual(artifact.Name, copied.Name);
        CollectionAssert.AreEqual(
            ((InformationSelectionProfileContentV1)artifact.Content).Entries.ToArray(),
            ((InformationSelectionProfileContentV1)copied.Content).Entries.ToArray());
    }

    [TestMethod]
    public void PortableIdentityRemovesSourceSetButPreservesStructuralDistinctions()
    {
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = new DiscoveryInformationIdentity(
            firstSet,
            "/catalog/buyer/name",
            "name",
            SourceValueCandidateKind.Element,
            "/catalog/buyer/name");
        var sameLogicalIdentity = new DiscoveryInformationIdentity(
            secondSet,
            first.StructuralPath,
            first.InformationType,
            first.CandidateKind,
            first.StructuralIdentity);
        var structurallyDifferent = new DiscoveryInformationIdentity(
            firstSet,
            "/catalog/seller/name",
            "name",
            SourceValueCandidateKind.Element,
            "/catalog/seller/name");

        var portable = PortableDiscoveryInformationIdentity.From(first);

        Assert.AreEqual(portable, PortableDiscoveryInformationIdentity.From(sameLogicalIdentity));
        Assert.AreNotEqual(portable, PortableDiscoveryInformationIdentity.From(structurallyDifferent));
        Assert.IsNull(typeof(PortableDiscoveryInformationIdentity).GetProperty("SourceSetId"));
        Assert.IsNull(typeof(PortableDiscoveryInformationIdentity).GetProperty("SourceId"));
    }

    [TestMethod]
    public void TypedContractsStructurallyExcludeCrossProfileAndSessionState()
    {
        var selectionProperties = typeof(InformationSelectionProfileContentV1)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var blacklistProperties = typeof(BlacklistProfileContentV1)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var serialized = Encoding.UTF8.GetString(
            ProfileArtifactStore.Serialize(CreateInformationSelection()));

        CollectionAssert.AreEquivalent(new[] { "Entries" }, selectionProperties);
        CollectionAssert.AreEquivalent(new[] { "Entries" }, blacklistProperties);
        Assert.IsNull(typeof(BlacklistProfileContentV1).GetProperty("Membership"));
        Assert.IsNull(typeof(InformationSelectionProfileEntryV1).GetProperty("Blacklist"));
        Assert.IsFalse(serialized.Contains("sourceSet", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("sourceId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("databaseTag", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("repeatedData", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("sourcePath", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MalformedAndUnknownCurrentSchemaFilesDoNotHideValidProfiles()
    {
        using var environment = new ProfileTestEnvironment();
        var valid = environment.Store.Create(
            "valid.cia-profile.json",
            CreateInformationSelection());
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "malformed.cia-profile.json"),
            "{not-json");
        var node = JsonNode.Parse(File.ReadAllText(valid.Path!))!.AsObject();
        node["unexpectedMeaning"] = true;
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "unknown.cia-profile.json"),
            node.ToJsonString());
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "duplicate-property.cia-profile.json"),
            "{\"schemaVersion\":1,\"schemaVersion\":1}");

        var inventory = environment.Store.CreateInventory();

        Assert.HasCount(1, inventory.ValidProfiles);
        Assert.AreEqual(3, inventory.Items.Count(item =>
            item.State == ProfileArtifactReadState.MalformedOrInvalid));
        Assert.IsTrue(File.Exists(valid.Path));
    }

    [TestMethod]
    public void FutureSchemaIsRejectedExplicitlyWithoutMutation()
    {
        using var environment = new ProfileTestEnvironment();
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        var path = Path.Combine(environment.Paths.ProfilesDirectory, "future.cia-profile.json");
        var json = Encoding.UTF8.GetString(ProfileArtifactStore.Serialize(CreateBlacklist()))
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        var before = File.ReadAllBytes(path);

        var inspection = environment.Store.Inspect(path);

        Assert.AreEqual(ProfileArtifactReadState.UnsupportedSchema, inspection.State);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void InvalidIdentityTimestampKindAndContentCombinationsAreRejected()
    {
        using var environment = new ProfileTestEnvironment();
        var baseline = CreateInformationSelection();
        var invalidId = baseline with { ProfileId = default };
        var invalidTimestamp = baseline with
        {
            UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))
        };
        var invalidKind = baseline with { ProfileKind = (ProfileKind)999 };
        var mismatchedContent = baseline with
        {
            Content = new BlacklistProfileContentV1([Identity("/catalog/code", "code")])
        };

        Assert.IsFalse(environment.Store.Create("invalid-id.cia-profile.json", invalidId).Succeeded);
        Assert.IsFalse(environment.Store.Create("invalid-time.cia-profile.json", invalidTimestamp).Succeeded);
        Assert.IsFalse(environment.Store.Create("invalid-kind.cia-profile.json", invalidKind).Succeeded);
        Assert.IsFalse(environment.Store.Create("invalid-content.cia-profile.json", mismatchedContent).Succeeded);
        Assert.IsFalse(Directory.Exists(environment.Paths.ProfilesDirectory)
                       && Directory.EnumerateFiles(
                           environment.Paths.ProfilesDirectory,
                           $"*{ProfileArtifactStore.FileExtension}").Any());
    }

    [TestMethod]
    public void ProfileIdNameTimestampAndReplacementInvariantsAreExplicitlyValidated()
    {
        var current = CreateBlacklist();
        var createdAfterUpdated = current with
        {
            CreatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
        };
        var emptyName = current with { Name = " " };
        var changedCreation = current with
        {
            CreatedAtUtc = current.CreatedAtUtc.AddMinutes(-1),
            UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
        };
        var unchangedUpdate = current with { Name = "Changed without timestamp" };

        Assert.ThrowsExactly<ArgumentException>(() => ProfileId.From(Guid.NewGuid()));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileArtifactValidator.Validate(createdAfterUpdated));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileArtifactValidator.Validate(emptyName));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileArtifactValidator.ValidateReplacement(current, changedCreation));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileArtifactValidator.ValidateReplacement(current, unchangedUpdate));
        CollectionAssert.AreEqual(
            ProfileArtifactStore.Serialize(current),
            ProfileArtifactStore.Serialize(current));
    }

    [TestMethod]
    public void DuplicatePortableIdentitiesAndInvalidEnumsAreRejected()
    {
        using var environment = new ProfileTestEnvironment();
        var identity = Identity("/catalog/code", "code");
        var duplicate = CreateInformationSelection() with
        {
            Content = new InformationSelectionProfileContentV1(
            [
                new(identity, InformationSelectionMembership.Selected),
                new(identity, InformationSelectionMembership.Excluded)
            ])
        };
        var invalidMembership = CreateInformationSelection() with
        {
            Content = new InformationSelectionProfileContentV1(
            [
                new(identity, (InformationSelectionMembership)999)
            ])
        };
        var invalidCandidate = CreateBlacklist() with
        {
            Content = new BlacklistProfileContentV1(
            [
                identity with { CandidateKind = (SourceValueCandidateKind)999 }
            ])
        };

        Assert.IsFalse(environment.Store.Create("duplicate.cia-profile.json", duplicate).Succeeded);
        Assert.IsFalse(environment.Store.Create("membership.cia-profile.json", invalidMembership).Succeeded);
        Assert.IsFalse(environment.Store.Create("candidate.cia-profile.json", invalidCandidate).Succeeded);
    }

    [TestMethod]
    public void UnknownProfileKindAndNonV7JsonIdentityAreRejected()
    {
        using var environment = new ProfileTestEnvironment();
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        var artifact = CreateBlacklist();
        var json = Encoding.UTF8.GetString(ProfileArtifactStore.Serialize(artifact));
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "unknown-kind.cia-profile.json"),
            json.Replace(
                "\"profileKind\": \"blacklist\"",
                "\"profileKind\": \"futureKind\"",
                StringComparison.Ordinal));
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "non-v7.cia-profile.json"),
            json.Replace(
                artifact.ProfileId.ToString(),
                Guid.NewGuid().ToString("D"),
                StringComparison.Ordinal));

        var inventory = environment.Store.CreateInventory();

        Assert.HasCount(2, inventory.Items);
        Assert.IsTrue(inventory.Items.All(item =>
            item.State == ProfileArtifactReadState.MalformedOrInvalid));
    }

    [TestMethod]
    public void DuplicateProfileIdsAreAmbiguousAndNeverChosenSilently()
    {
        using var environment = new ProfileTestEnvironment();
        var artifact = CreateBlacklist();
        var created = environment.Store.Create("first.cia-profile.json", artifact);
        var duplicatePath = Path.Combine(
            environment.Paths.ProfilesDirectory,
            "second.cia-profile.json");
        File.Copy(created.Path!, duplicatePath);

        var inventory = environment.Store.CreateInventory();
        var found = environment.Store.FindById(artifact.ProfileId);

        Assert.IsEmpty(inventory.ValidProfiles);
        Assert.IsTrue(inventory.Items.All(item =>
            item.State == ProfileArtifactReadState.AmbiguousProfileId));
        Assert.AreEqual(ProfileArtifactReadState.AmbiguousProfileId, found.State);
        Assert.IsFalse(environment.Store.Update(artifact with
        {
            UpdatedAtUtc = artifact.UpdatedAtUtc.AddMinutes(1)
        }, created.Fingerprint!.Value).Succeeded);
    }

    [TestMethod]
    public async Task ConcurrentSameIdCreatesAcrossStoresPublishExactlyOneArtifact()
    {
        using var environment = new ProfileTestEnvironment();
        using var blockingOperations = new BlockingFileOperations();
        var firstStore = new ProfileArtifactStore(environment.Paths, blockingOperations);
        var secondStore = new ProfileArtifactStore(environment.Paths);
        var artifact = CreateInformationSelection();
        var firstTask = Task.Run(() => firstStore.Create(
            "first.cia-profile.json",
            artifact));
        Assert.IsTrue(blockingOperations.WaitUntilEntered(TimeSpan.FromSeconds(10)));
        var secondTask = Task.Run(() => secondStore.Create(
            "second.cia-profile.json",
            artifact));
        try
        {
            Assert.AreNotSame(
                secondTask,
                await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromMilliseconds(150))));
        }
        finally
        {
            blockingOperations.Release();
        }

        var results = await Task.WhenAll(firstTask, secondTask);
        var inventory = secondStore.CreateInventory();

        Assert.AreEqual(1, results.Count(result => result.Succeeded));
        Assert.AreEqual(1, results.Count(result => !result.Succeeded));
        Assert.HasCount(1, inventory.ValidProfiles);
        Assert.AreEqual(artifact.ProfileId, inventory.ValidProfiles.Single().ProfileId);
        Assert.IsFalse(inventory.Items.Any(item =>
            item.State == ProfileArtifactReadState.AmbiguousProfileId));
        Assert.IsEmpty(Directory.GetFiles(environment.Paths.ProfilesDirectory, "*.incomplete"));
    }

    [TestMethod]
    public async Task ConcurrentUpdatesAcrossStoresRejectStaleAcceptedGeneration()
    {
        using var environment = new ProfileTestEnvironment();
        var current = CreateInformationSelection();
        var created = environment.Store.Create("profile.cia-profile.json", current);
        using var blockingOperations = new BlockingFileOperations();
        var firstStore = new ProfileArtifactStore(environment.Paths, blockingOperations);
        var secondStore = new ProfileArtifactStore(environment.Paths);
        var firstReplacement = current with
        {
            Name = "First accepted update",
            UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
        };
        var staleReplacement = current with
        {
            Name = "Stale conflicting update",
            UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(2)
        };
        var firstTask = Task.Run(() => firstStore.Update(
            firstReplacement,
            created.Fingerprint!.Value));
        Assert.IsTrue(blockingOperations.WaitUntilEntered(TimeSpan.FromSeconds(10)));
        var staleTask = Task.Run(() => secondStore.Update(
            staleReplacement,
            created.Fingerprint!.Value));
        try
        {
            Assert.AreNotSame(
                staleTask,
                await Task.WhenAny(staleTask, Task.Delay(TimeSpan.FromMilliseconds(150))));
        }
        finally
        {
            blockingOperations.Release();
        }

        var firstResult = await firstTask;
        var staleResult = await staleTask;
        var inventory = secondStore.CreateInventory();
        var final = inventory.ValidProfiles.Single();

        Assert.IsTrue(firstResult.Succeeded, firstResult.Problem);
        Assert.IsFalse(staleResult.Succeeded);
        StringAssert.Contains(staleResult.Problem, "changed after it was read");
        Assert.AreEqual(current.ProfileId, final.ProfileId);
        Assert.AreEqual(current.CreatedAtUtc, final.CreatedAtUtc);
        Assert.AreEqual(firstReplacement.UpdatedAtUtc, final.UpdatedAtUtc);
        Assert.AreEqual(firstReplacement.Name, final.Name);
        Assert.IsFalse(inventory.Items.Any(item =>
            item.State == ProfileArtifactReadState.AmbiguousProfileId));
        Assert.IsEmpty(Directory.GetFiles(environment.Paths.ProfilesDirectory, "*.incomplete"));
    }

    [TestMethod]
    public async Task PublicationOwnershipReleasesAfterFailureAndLockShellIsIgnored()
    {
        using var environment = new ProfileTestEnvironment();
        using var blockingOperations = new BlockingFileOperations
        {
            FailAfterRelease = true
        };
        var failingStore = new ProfileArtifactStore(environment.Paths, blockingOperations);
        var waitingStore = new ProfileArtifactStore(environment.Paths);
        var failedTask = Task.Run(() => failingStore.Create(
            "failed.cia-profile.json",
            CreateBlacklist()));
        Assert.IsTrue(blockingOperations.WaitUntilEntered(TimeSpan.FromSeconds(10)));
        var successfulArtifact = CreateInformationSelection();
        var waitingTask = Task.Run(() => waitingStore.Create(
            "successful.cia-profile.json",
            successfulArtifact));
        try
        {
            Assert.AreNotSame(
                waitingTask,
                await Task.WhenAny(waitingTask, Task.Delay(TimeSpan.FromMilliseconds(150))));
        }
        finally
        {
            blockingOperations.Release();
        }

        var failed = await failedTask;
        var succeeded = await waitingTask;
        var later = waitingStore.Create("later.cia-profile.json", CreateBlacklist());
        var inventory = waitingStore.CreateInventory();

        Assert.IsFalse(failed.Succeeded);
        Assert.IsTrue(succeeded.Succeeded, succeeded.Problem);
        Assert.IsTrue(later.Succeeded, later.Problem);
        Assert.IsTrue(File.Exists(Path.Combine(
            environment.Paths.ProfilesDirectory,
            ProfileArtifactStore.PublicationLockFileName)));
        Assert.HasCount(2, inventory.ValidProfiles);
        Assert.HasCount(2, inventory.Items);
        Assert.IsEmpty(Directory.GetFiles(environment.Paths.ProfilesDirectory, "*.incomplete"));
    }

    [TestMethod]
    public void FailedNewWritePublishesNoProfileAndCleansCandidate()
    {
        using var environment = new ProfileTestEnvironment(new FaultingFileOperations
        {
            FailCandidateWrite = true
        });

        var result = environment.Store.Create(
            "failed.cia-profile.json",
            CreateInformationSelection());

        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(GetPublishedProfilePaths(environment.Paths.ProfilesDirectory));
    }

    [TestMethod]
    public void FailedNewPublicationPublishesNoProfileAndCleansValidatedCandidate()
    {
        using var environment = new ProfileTestEnvironment(new FaultingFileOperations
        {
            FailNewPublication = true
        });

        var result = environment.Store.Create(
            "failed-publication.cia-profile.json",
            CreateInformationSelection());

        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(GetPublishedProfilePaths(environment.Paths.ProfilesDirectory));
    }

    [TestMethod]
    public void FailedReplacementPreservesPreviousBytesExactly()
    {
        using var environment = new ProfileTestEnvironment();
        var current = CreateInformationSelection();
        var created = environment.Store.Create("selection.cia-profile.json", current);
        var before = File.ReadAllBytes(created.Path!);
        environment.ReplaceStore(new FaultingFileOperations { FailReplacement = true });
        var replacement = current with
        {
            Name = "Updated profile",
            UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
        };

        var result = environment.Store.Update(replacement, created.Fingerprint!.Value);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(created.Path!));
        Assert.IsEmpty(Directory.GetFiles(environment.Paths.ProfilesDirectory, "*.incomplete"));
    }

    [TestMethod]
    public void SuccessfulReplacementPreservesIdentityAndCreationTimeAndAdvancesUpdate()
    {
        using var environment = new ProfileTestEnvironment();
        var current = CreateBlacklist();
        var created = environment.Store.Create("renamed-by-user.cia-profile.json", current);
        var replacement = current with
        {
            Name = "Updated blacklist",
            UpdatedAtUtc = current.UpdatedAtUtc.AddHours(1)
        };

        var result = environment.Store.Update(replacement, created.Fingerprint!.Value);
        var loaded = environment.Store.Inspect(created.Path!).Artifact!;

        Assert.IsTrue(result.Succeeded, result.Problem);
        Assert.AreEqual(current.ProfileId, loaded.ProfileId);
        Assert.AreEqual(current.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.AreEqual(replacement.UpdatedAtUtc, loaded.UpdatedAtUtc);
        Assert.AreEqual(replacement.Name, loaded.Name);
    }

    [TestMethod]
    public void CandidateIsRereadAndValidatedBeforePublication()
    {
        using var environment = new ProfileTestEnvironment(new FaultingFileOperations
        {
            CorruptCandidate = true
        });

        var result = environment.Store.Create(
            "corrupted-candidate.cia-profile.json",
            CreateBlacklist());

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(
            environment.Paths.ProfilesDirectory,
            "corrupted-candidate.cia-profile.json")));
        Assert.IsEmpty(GetPublishedProfilePaths(environment.Paths.ProfilesDirectory));
    }

    [TestMethod]
    public void IncompleteNestedAndUnrelatedFilesAreNotProfiles()
    {
        using var environment = new ProfileTestEnvironment();
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        File.WriteAllBytes(
            Path.Combine(environment.Paths.ProfilesDirectory, "abandoned.incomplete"),
            ProfileArtifactStore.Serialize(CreateBlacklist()));
        File.WriteAllText(
            Path.Combine(environment.Paths.ProfilesDirectory, "notes.json"),
            "{}");
        var nested = Path.Combine(environment.Paths.ProfilesDirectory, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(
            Path.Combine(nested, "nested.cia-profile.json"),
            ProfileArtifactStore.Serialize(CreateBlacklist()));

        var inventory = environment.Store.CreateInventory();

        Assert.IsEmpty(inventory.Items);
        Assert.HasCount(3, Directory.GetFiles(
            environment.Paths.ProfilesDirectory,
            "*",
            SearchOption.AllDirectories));
    }

    [TestMethod]
    public void TraversalExternalAndWrongExtensionTargetsAreRejected()
    {
        using var environment = new ProfileTestEnvironment();
        var artifact = CreateBlacklist();

        var traversal = environment.Store.Create("..\\escaped.cia-profile.json", artifact);
        var nested = environment.Store.Create("nested/profile.cia-profile.json", artifact);
        var wrongExtension = environment.Store.Create("profile.json", artifact);

        Assert.IsFalse(traversal.Succeeded);
        Assert.IsFalse(nested.Succeeded);
        Assert.IsFalse(wrongExtension.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(
            Path.GetDirectoryName(environment.Paths.ProfilesDirectory)!,
            "escaped.cia-profile.json")));
    }

    [TestMethod]
    public void ReparseProfileFileIsRejectedWithoutTouchingExternalTarget()
    {
        using var environment = new ProfileTestEnvironment();
        Directory.CreateDirectory(environment.Paths.ProfilesDirectory);
        var externalPath = Path.Combine(environment.Root, "external.cia-profile.json");
        var bytes = ProfileArtifactStore.Serialize(CreateBlacklist());
        File.WriteAllBytes(externalPath, bytes);
        var linkPath = Path.Combine(
            environment.Paths.ProfilesDirectory,
            "linked.cia-profile.json");
        try
        {
            File.CreateSymbolicLink(linkPath, externalPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
        {
            return;
        }

        var inspection = environment.Store.Inspect(linkPath);

        Assert.AreEqual(ProfileArtifactReadState.UnsafePath, inspection.State);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(externalPath));
    }

    [TestMethod]
    public void InventoryReadIsNonDestructive()
    {
        using var environment = new ProfileTestEnvironment();
        var created = environment.Store.Create("profile.cia-profile.json", CreateBlacklist());
        var malformedPath = Path.Combine(
            environment.Paths.ProfilesDirectory,
            "malformed.cia-profile.json");
        File.WriteAllText(malformedPath, "{");
        var before = Directory.GetFiles(environment.Paths.ProfilesDirectory)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        _ = environment.Store.CreateInventory();

        var after = Directory.GetFiles(environment.Paths.ProfilesDirectory);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after);
        foreach (var path in after)
        {
            CollectionAssert.AreEqual(before[path], File.ReadAllBytes(path));
        }

        Assert.IsTrue(File.Exists(created.Path));
    }

    [TestMethod]
    public async Task UnrelatedProcessingFailureDoesNotModifyProfileArtifact()
    {
        using var environment = new ProfileTestEnvironment();
        var created = environment.Store.Create("protected.cia-profile.json", CreateBlacklist());
        var before = File.ReadAllBytes(created.Path!);
        var intake = new SourceIntakeService(new ArchiveExtractionService(environment.Paths));

        var failure = await intake.LoadAsync(
            SourceSelectionKind.XmlFile,
            Path.Combine(environment.Root, "missing.xml"),
            SourceLoadSettings.Default);

        Assert.IsFalse(failure.Accepted);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(created.Path!));
        Assert.HasCount(1, environment.Store.CreateInventory().ValidProfiles);
    }

    private static string[] GetPublishedProfilePaths(string profilesDirectory) =>
        Directory.GetFiles(
            profilesDirectory,
            $"*{ProfileArtifactStore.FileExtension}",
            SearchOption.TopDirectoryOnly);

    private static ProfileArtifactV1 CreateInformationSelection(
        ProfileId? profileId = null)
    {
        var created = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            profileId ?? ProfileId.CreateNew(),
            ProfileKind.InformationSelection,
            "Selected information",
            created,
            created,
            new InformationSelectionProfileContentV1(
            [
                new(
                    Identity("/catalog/code", "code"),
                    InformationSelectionMembership.Selected),
                new(
                    Identity("/catalog/description", "description"),
                    InformationSelectionMembership.Excluded)
            ]));
    }

    private static ProfileArtifactV1 CreateBlacklist(ProfileId? profileId = null)
    {
        var created = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        return new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            profileId ?? ProfileId.CreateNew(),
            ProfileKind.Blacklist,
            "Reusable blacklist",
            created,
            created,
            new BlacklistProfileContentV1(
            [
                Identity("/catalog/code", "code"),
                Identity("/catalog/category/@name", "name", SourceValueCandidateKind.Attribute)
            ]));
    }

    private static PortableDiscoveryInformationIdentity Identity(
        string path,
        string informationType,
        SourceValueCandidateKind kind = SourceValueCandidateKind.Element) =>
        new(path, informationType, kind, $"{kind}:{path}");

    private sealed class FaultingFileOperations : IProfileArtifactFileOperations
    {
        private readonly ProfileArtifactFileOperations _inner = new();

        public bool FailCandidateWrite { get; init; }

        public bool CorruptCandidate { get; init; }

        public bool FailNewPublication { get; init; }

        public bool FailReplacement { get; init; }

        public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content)
        {
            if (FailCandidateWrite)
            {
                File.WriteAllText(candidatePath, "partial");
                throw new IOException("Simulated candidate write failure.");
            }

            _inner.WriteCandidate(
                candidatePath,
                CorruptCandidate ? Encoding.UTF8.GetBytes("{}") : content);
        }

        public void PublishNew(string candidatePath, string targetPath)
        {
            if (FailNewPublication)
            {
                throw new IOException("Simulated new-profile publication failure.");
            }

            _inner.PublishNew(candidatePath, targetPath);
        }

        public void Replace(string candidatePath, string targetPath)
        {
            if (FailReplacement)
            {
                throw new IOException("Simulated replacement failure.");
            }

            _inner.Replace(candidatePath, targetPath);
        }

        public void DeleteCandidate(string candidatePath) =>
            _inner.DeleteCandidate(candidatePath);
    }

    private sealed class BlockingFileOperations : IProfileArtifactFileOperations, IDisposable
    {
        private readonly ProfileArtifactFileOperations _inner = new();
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public bool FailAfterRelease { get; init; }

        public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The coordinated profile writer was not released.");
            }

            if (FailAfterRelease)
            {
                throw new IOException("Simulated candidate failure while publication is owned.");
            }

            _inner.WriteCandidate(candidatePath, content);
        }

        public void PublishNew(string candidatePath, string targetPath) =>
            _inner.PublishNew(candidatePath, targetPath);

        public void Replace(string candidatePath, string targetPath) =>
            _inner.Replace(candidatePath, targetPath);

        public void DeleteCandidate(string candidatePath) =>
            _inner.DeleteCandidate(candidatePath);

        public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class ProfileTestEnvironment : IDisposable
    {
        private static readonly string SafeRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR92.Tests");

        public ProfileTestEnvironment(IProfileArtifactFileOperations? fileOperations = null)
        {
            Root = Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData"));
            Store = new ProfileArtifactStore(Paths, fileOperations);
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public ProfileArtifactStore Store { get; private set; }

        public void ReplaceStore(IProfileArtifactFileOperations fileOperations)
        {
            Store = new ProfileArtifactStore(Paths, fileOperations);
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
                    "Refusing to remove a profile test directory outside its safe root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }
}
