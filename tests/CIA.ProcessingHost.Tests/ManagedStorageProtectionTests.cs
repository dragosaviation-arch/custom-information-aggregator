using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.ManagedStorage;
using CIA.Core.Runtime;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CIA.ProcessingHost.SourceIntake;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ManagedStorageProtectionTests
{
    [TestMethod]
    public void OwnershipMetadataRoundTripsTypedIdentityAndUtcContext()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var operationId = OperationId.CreateNew();
        var sourceId = SourceId.CreateNew();
        var sourceSetId = SourceSetId.CreateNew();
        var created = DateTimeOffset.UtcNow;
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate,
            operationId,
            sourceId,
            sourceSetId,
            created);

        var read = environment.Metadata.Read(artifact.Path);

        Assert.AreEqual(ManagedStorageMetadataReadState.Valid, read.State);
        Assert.AreEqual(artifact.Metadata.ArtifactId, read.Metadata?.ArtifactId);
        Assert.AreEqual(operationId, read.Metadata?.OperationId);
        Assert.AreEqual(sourceId, read.Metadata?.SourceId);
        Assert.AreEqual(sourceSetId, read.Metadata?.SourceSetId);
        Assert.AreEqual(created, read.Metadata?.CreatedAtUtc);
        Assert.AreEqual(Path.GetFullPath(artifact.Path), read.Metadata?.CanonicalArtifactPath);
    }

    [TestMethod]
    public void ExplicitlyOwnedTerminalTemporaryArtifactsAreEligibleButActiveOnesAreProtected()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var operationId = OperationId.CreateNew();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.TemporaryOperationArtifact,
            ManagedStorageLifecycle.OperationTemporary,
            operationId);
        var active = Snapshot(activeOperations: [operationId]);

        var whileActive = environment.Inventory.CreateInventory(active).Items.Single();
        var afterTerminal = environment.Inventory.CreateInventory().Items.Single();

        Assert.IsFalse(whileActive.IsCleanupEligible);
        CollectionAssert.Contains(
            whileActive.ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.ActiveOperation);
        Assert.IsTrue(afterTerminal.IsCleanupEligible);
        Assert.AreEqual(artifact.Metadata.ArtifactId, afterTerminal.ArtifactId);
    }

    [TestMethod]
    public void RepeatedActiveOrCancellingSnapshotDoesNotCreateAnotherArtifactIdentity()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var operationId = OperationId.CreateNew();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.TemporaryOperationArtifact,
            ManagedStorageLifecycle.OperationTemporary,
            operationId);
        var dependencies = Snapshot(activeOperations: [operationId]);

        var first = environment.Inventory.CreateInventory(dependencies).Items.Single();
        var second = environment.Inventory.CreateInventory(dependencies).Items.Single();

        Assert.AreEqual(artifact.Metadata.ArtifactId, first.ArtifactId);
        Assert.AreEqual(first.ArtifactId, second.ArtifactId);
        Assert.AreEqual(first.CanonicalPath, second.CanonicalPath);
        CollectionAssert.AreEqual(
            first.ProtectionReasons.ToArray(),
            second.ProtectionReasons.ToArray());
        Assert.IsFalse(first.IsCleanupEligible);
    }

    [TestMethod]
    public void ManagedArchiveExtractionIsProtectedByActiveSourcePathAndOriginalArchiveIdentity()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var originalArchiveSourceId = SourceId.CreateNew();
        var sourceId = SourceId.CreateNew();
        var sourceSetId = SourceSetId.CreateNew();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: originalArchiveSourceId);
        var byPath = Snapshot(activeSources:
        [
            new ManagedStorageSourceDependency(
                sourceId,
                sourceSetId,
                OriginalArchiveSourceId: null,
                Path.Combine(artifact.Path, "source.xml"),
                artifact.Path,
                ArchiveExtractionRetention.ManagedTemporary)
        ]);
        var byIdentity = Snapshot(activeSources:
        [
            new ManagedStorageSourceDependency(
                sourceId,
                sourceSetId,
                originalArchiveSourceId,
                Path.Combine(artifact.Path, "source.xml"),
                ExtractionRoot: null,
                ArchiveExtractionRetention.ManagedTemporary)
        ]);

        Assert.IsFalse(environment.Inventory.CreateInventory(byPath).Items.Single().IsCleanupEligible);
        var identityResult = environment.Inventory.CreateInventory(byIdentity).Items.Single();
        Assert.IsFalse(identityResult.IsCleanupEligible);
        CollectionAssert.Contains(
            identityResult.ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.ActiveSessionSource);
    }

    [TestMethod]
    public void RemovedSessionSourceAllowsManagedTemporaryExtractionToBecomeCandidate()
    {
        using var environment = new ManagedStorageTestEnvironment();
        environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew());

        var classification = environment.Inventory.CreateInventory().Items.Single();

        Assert.IsTrue(classification.IsCleanupEligible);
    }

    [TestMethod]
    public void RetainedDatabaseExtractionOrPartialResultDependencyProtectsByIdOrPath()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var byIdArtifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var byPathArtifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var dependencies = Snapshot(retained:
        [
            new ManagedStorageRetainedDependency(
                byIdArtifact.Metadata.ArtifactId,
                CanonicalPath: null,
                "Retained Database generation"),
            new ManagedStorageRetainedDependency(
                ArtifactId: null,
                byPathArtifact.Path,
                "Retained partial or cancelled result")
        ]);

        var items = environment.Inventory.CreateInventory(dependencies).Items;

        Assert.HasCount(2, items);
        Assert.IsTrue(items.All(item => !item.IsCleanupEligible));
        Assert.IsTrue(items.All(item =>
            item.ProtectionReasons.Contains(ManagedStorageProtectionReason.RetainedResultDependency)));
    }

    [TestMethod]
    public void CandidateKindsAreRestrictedToTheirApprovedRoots()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var temp = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ExportStagingCandidate,
            ManagedStorageLifecycle.Staging,
            OperationId.CreateNew());
        var working = environment.CreateArtifact(
            environment.Paths.WorkingDirectory,
            ManagedStorageArtifactKind.WorkingStateStagingCandidate,
            ManagedStorageLifecycle.Staging,
            OperationId.CreateNew());
        var invalidWorking = environment.CreateArtifact(
            environment.Paths.WorkingDirectory,
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew());

        var items = environment.Inventory.CreateInventory().Items;

        Assert.IsTrue(items.Single(item => item.ArtifactId == temp.Metadata.ArtifactId).IsCleanupEligible);
        Assert.IsTrue(items.Single(item => item.ArtifactId == working.Metadata.ArtifactId).IsCleanupEligible);
        var invalid = items.Single(item => item.ArtifactId == invalidWorking.Metadata.ArtifactId);
        Assert.IsFalse(invalid.IsCleanupEligible);
        CollectionAssert.Contains(
            invalid.ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.ArtifactKindNotEligibleInRoot);
    }

    [TestMethod]
    public void PersistentLifecycleCanNeverBecomeACleanupCandidate()
    {
        using var environment = new ManagedStorageTestEnvironment();
        environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Persistent);

        var classification = environment.Inventory.CreateInventory().Items.Single();

        Assert.IsFalse(classification.IsCleanupEligible);
        CollectionAssert.Contains(
            classification.ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.PersistentData);
    }

    [TestMethod]
    public void InProgressPackageStagingIsProtectedAndAbandonedTerminalStagingCanBecomeEligible()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var operationId = OperationId.CreateNew();
        var staging = environment.CreateArtifact(
            environment.Paths.WorkingDirectory,
            ManagedStorageArtifactKind.WorkingStateStagingCandidate,
            ManagedStorageLifecycle.Staging,
            operationId);

        var active = environment.Inventory.CreateInventory(
            Snapshot(activeOperations: [operationId])).Items.Single();
        var abandoned = environment.Inventory.CreateInventory().Items.Single();

        Assert.AreEqual(staging.Metadata.ArtifactId, active.ArtifactId);
        Assert.IsFalse(active.IsCleanupEligible);
        Assert.IsTrue(abandoned.IsCleanupEligible);
    }

    [TestMethod]
    public void MissingMalformedFutureAndPathMismatchedMetadataAllFailClosedIndependently()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var missing = environment.CreateUnownedDirectory(environment.Paths.TempDirectory, "missing");
        var malformed = environment.CreateUnownedDirectory(environment.Paths.TempDirectory, "malformed");
        File.WriteAllText(
            Path.Combine(malformed, ManagedStorageOwnershipMetadataStore.FileName),
            "{ definitely-not-json");
        var future = environment.CreateUnownedDirectory(environment.Paths.TempDirectory, "future");
        File.WriteAllText(
            Path.Combine(future, ManagedStorageOwnershipMetadataStore.FileName),
            "{\"schemaVersion\":999}");
        var mismatched = environment.CreateUnownedDirectory(environment.Paths.TempDirectory, "mismatch");
        environment.WriteRawMetadata(
            mismatched,
            environment.CreateMetadata(
                Path.Combine(environment.Paths.TempDirectory, "different"),
                ManagedStorageArtifactKind.ManagedIntermediateArtifact,
                ManagedStorageLifecycle.Intermediate));
        var valid = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);

        var items = environment.Inventory.CreateInventory().Items;

        Assert.IsFalse(items.Single(item => item.CanonicalPath == missing).IsCleanupEligible);
        Assert.AreEqual(
            ManagedStorageProtectionReason.MalformedOwnershipMetadata,
            items.Single(item => item.CanonicalPath == malformed).ProtectionReasons.Single());
        Assert.AreEqual(
            ManagedStorageProtectionReason.UnsupportedOwnershipMetadataVersion,
            items.Single(item => item.CanonicalPath == future).ProtectionReasons.Single());
        Assert.AreEqual(
            ManagedStorageProtectionReason.OwnershipPathMismatch,
            items.Single(item => item.CanonicalPath == mismatched).ProtectionReasons.Single());
        Assert.IsTrue(items.Single(item => item.ArtifactId == valid.Metadata.ArtifactId).IsCleanupEligible);
    }

    [TestMethod]
    [DataRow(ProtectedPathKind.Settings, ManagedStorageArtifactKind.SettingsOrBootstrap)]
    [DataRow(ProtectedPathKind.SettingsBootstrap, ManagedStorageArtifactKind.SettingsOrBootstrap)]
    [DataRow(ProtectedPathKind.Profiles, ManagedStorageArtifactKind.ProfileData)]
    [DataRow(ProtectedPathKind.Database, ManagedStorageArtifactKind.DatabaseStorage)]
    [DataRow(ProtectedPathKind.DatabaseWal, ManagedStorageArtifactKind.DatabaseStorage)]
    [DataRow(ProtectedPathKind.DatabaseShm, ManagedStorageArtifactKind.DatabaseStorage)]
    [DataRow(ProtectedPathKind.Logs, ManagedStorageArtifactKind.DiagnosticOrHistoryStorage)]
    [DataRow(ProtectedPathKind.Clef, ManagedStorageArtifactKind.DiagnosticOrHistoryStorage)]
    [DataRow(ProtectedPathKind.InstalledBinary, ManagedStorageArtifactKind.InstalledApplicationBinary)]
    public void BuiltInManagedAndInstalledLocationsAreHardProtected(
        ProtectedPathKind pathKind,
        ManagedStorageArtifactKind expectedKind)
    {
        using var environment = new ManagedStorageTestEnvironment();
        var path = environment.GetProtectedPath(pathKind);

        var classification = environment.Inventory.ClassifyPath(path);

        Assert.IsFalse(classification.IsCleanupEligible);
        Assert.AreEqual(expectedKind, classification.ArtifactKind);
    }

    [TestMethod]
    public void OriginalSourcesCompletedExportsSavedPackagesAndPersistentExtractionsAreProtected()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var source = Path.Combine(environment.TestRoot, "external", "source.xml");
        var export = Path.Combine(environment.TestRoot, "external", "result.xlsx");
        var package = Path.Combine(environment.TestRoot, "external", "session.cia");
        var persistent = Path.Combine(environment.TestRoot, "PersistentExtraction");
        var dependencies = Snapshot(protectedLocations:
        [
            new ManagedStorageProtectedLocation(
                source,
                ManagedStorageArtifactKind.OriginalOrExternalSource,
                IncludeDescendants: false),
            new ManagedStorageProtectedLocation(
                export,
                ManagedStorageArtifactKind.CompletedExport,
                IncludeDescendants: false),
            new ManagedStorageProtectedLocation(
                package,
                ManagedStorageArtifactKind.SavedWorkingState,
                IncludeDescendants: false),
            new ManagedStorageProtectedLocation(
                persistent,
                ManagedStorageArtifactKind.PersistentArchiveExtraction)
        ]);

        Assert.IsTrue(new[] { source, export, package, Path.Combine(persistent, "source.xml") }
            .Select(path => environment.Inventory.ClassifyPath(path, dependencies))
            .All(classification => !classification.IsCleanupEligible));
    }

    [TestMethod]
    public void InvalidDependencyEvidenceMakesOtherwiseEligibleInventoryFailClosed()
    {
        using var environment = new ManagedStorageTestEnvironment();
        environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var dependencies = Snapshot(retained:
        [
            new ManagedStorageRetainedDependency(
                ArtifactId: null,
                CanonicalPath: "relative/not-safe",
                "Invalid retained dependency")
        ]);

        var classification = environment.Inventory.CreateInventory(dependencies).Items.Single();

        Assert.IsFalse(classification.IsCleanupEligible);
        CollectionAssert.Contains(
            classification.ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.InspectionFailed);
    }

    [TestMethod]
    public void PersistentExtractionInsideTempStopsAtItsConfiguredBoundary()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var persistent = Path.Combine(environment.Paths.TempDirectory, "PersistentExtraction");
        Directory.CreateDirectory(persistent);
        File.WriteAllText(Path.Combine(persistent, "source.xml"), "<source />");
        var dependencies = Snapshot(protectedLocations:
        [
            new ManagedStorageProtectedLocation(
                persistent,
                ManagedStorageArtifactKind.PersistentArchiveExtraction)
        ]);

        var items = environment.Inventory.CreateInventory(dependencies).Items;

        Assert.HasCount(1, items);
        Assert.AreEqual(Path.GetFullPath(persistent), items[0].CanonicalPath);
        Assert.AreEqual(ManagedStorageArtifactKind.PersistentArchiveExtraction, items[0].ArtifactKind);
        Assert.IsFalse(items[0].IsCleanupEligible);
    }

    [TestMethod]
    public void SelfContainedSavedPackageDoesNotByItselfProtectUnreferencedTemporaryExtraction()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew());
        var package = Path.Combine(environment.TestRoot, "saved.cia");
        var dependencies = Snapshot(protectedLocations:
        [
            new ManagedStorageProtectedLocation(
                package,
                ManagedStorageArtifactKind.SavedWorkingState,
                IncludeDescendants: false)
        ]);

        var classification = environment.Inventory.CreateInventory(dependencies).Items.Single();

        Assert.AreEqual(artifact.Metadata.ArtifactId, classification.ArtifactId);
        Assert.IsTrue(classification.IsCleanupEligible);
        Assert.IsFalse(environment.Inventory.ClassifyPath(package, dependencies).IsCleanupEligible);
    }

    [TestMethod]
    public void InventoryEnumeratesOnlyTempAndWorkingAndNeverProtectedManagedRoots()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var temp = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var working = environment.CreateArtifact(
            environment.Paths.WorkingDirectory,
            ManagedStorageArtifactKind.WorkingStateStagingCandidate,
            ManagedStorageLifecycle.Staging,
            OperationId.CreateNew());
        environment.WriteProtectedRootSentinels();

        var inventory = environment.Inventory.CreateInventory();

        CollectionAssert.AreEquivalent(
            new[] { temp.Metadata.ArtifactId, working.Metadata.ArtifactId },
            inventory.Items.Where(item => item.ArtifactId is not null)
                .Select(item => item.ArtifactId!.Value)
                .ToArray());
        Assert.IsFalse(inventory.Items.Any(item =>
            IsWithin(item.CanonicalPath, environment.Paths.SettingsDirectory)
            || IsWithin(item.CanonicalPath, environment.Paths.ProfilesDirectory)
            || IsWithin(item.CanonicalPath, environment.Paths.DatabaseDirectory)
            || IsWithin(item.CanonicalPath, environment.Paths.LogsDirectory)
            || IsWithin(item.CanonicalPath, environment.Paths.InstalledBinaryDirectory)));
    }

    [TestMethod]
    public void InventoryIsReadOnlyAndDoesNotDeleteMoveTruncateOverwriteOrMigrate()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var candidate = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate,
            payload: "retain exact bytes");
        var payloadPath = Path.Combine(candidate.Path, "payload.bin");
        var markerPath = Path.Combine(candidate.Path, ManagedStorageOwnershipMetadataStore.FileName);
        var payload = File.ReadAllBytes(payloadPath);
        var marker = File.ReadAllBytes(markerPath);
        var payloadWrite = File.GetLastWriteTimeUtc(payloadPath);
        var markerWrite = File.GetLastWriteTimeUtc(markerPath);

        var inventory = environment.Inventory.CreateInventory();

        Assert.IsTrue(inventory.Items.Single().IsCleanupEligible);
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(payloadPath));
        CollectionAssert.AreEqual(marker, File.ReadAllBytes(markerPath));
        Assert.AreEqual(payloadWrite, File.GetLastWriteTimeUtc(payloadPath));
        Assert.AreEqual(markerWrite, File.GetLastWriteTimeUtc(markerPath));
        Assert.IsTrue(Directory.Exists(candidate.Path));
    }

    [TestMethod]
    public void OverlappingCleanupAndProtectedRootsFailClosedWithoutTraversal()
    {
        using var environment = new ManagedStorageTestEnvironment(
            temporaryDirectorySelector: defaults => defaults.DatabaseDirectory);
        Directory.CreateDirectory(environment.Paths.TempDirectory);
        File.WriteAllText(Path.Combine(environment.Paths.TempDirectory, "database.sqlite"), "database");

        var inventory = environment.Inventory.CreateInventory();

        Assert.IsTrue(inventory.Items.Any(item =>
            item.CanonicalPath == Path.GetFullPath(environment.Paths.TempDirectory)
            && item.ProtectionReasons.Contains(ManagedStorageProtectionReason.ProtectedManagedRoot)));
        Assert.IsFalse(inventory.CleanupCandidates.Any());
    }

    [TestMethod]
    public void ReparsePointInsideCleanupRootIsProtectedAndNeverTraversed()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var externalTarget = Path.Combine(environment.TestRoot, "ExternalTarget");
        Directory.CreateDirectory(externalTarget);
        File.WriteAllText(Path.Combine(externalTarget, "source.xml"), "<source />");
        var link = Path.Combine(environment.Paths.TempDirectory, "linked-target");
        CreateDirectoryLink(link, externalTarget);

        try
        {
            var items = environment.Inventory.CreateInventory().Items;

            Assert.HasCount(1, items);
            Assert.AreEqual(Path.GetFullPath(link), items[0].CanonicalPath);
            Assert.AreEqual(
                ManagedStorageProtectionReason.ReparsePoint,
                items[0].ProtectionReasons.Single());
            Assert.IsFalse(items[0].IsCleanupEligible);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task RealArchiveExtractionPublishesOwnershipAndActiveSourceProtectsIt()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var archivePath = environment.CreateArchive("source.zip", "source.xml", "<root><value>1</value></root>");
        var intake = new SourceIntakeService(new ArchiveExtractionService(environment.Paths));
        var sourceSetId = SourceSetId.CreateNew();

        var result = await intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default,
            progress: null,
            cancellationToken: default,
            sourceSetId);

        Assert.IsTrue(result.Accepted);
        var source = result.Sources.Single();
        var provenance = source.ArchiveProvenance!;
        var metadata = environment.Metadata.Read(provenance.ExtractionRoot);
        Assert.AreEqual(ManagedStorageMetadataReadState.Valid, metadata.State);
        Assert.AreEqual(
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            metadata.Metadata?.ArtifactKind);
        Assert.AreEqual(provenance.OriginalArchiveSourceId, metadata.Metadata?.SourceId);
        Assert.AreEqual(sourceSetId, metadata.Metadata?.SourceSetId);

        var active = Snapshot(activeSources:
        [
            new ManagedStorageSourceDependency(
                source.SourceId,
                source.SourceSetId,
                provenance.OriginalArchiveSourceId,
                source.Path,
                provenance.ExtractionRoot,
                provenance.Retention)
        ]);
        Assert.IsFalse(environment.Inventory.CreateInventory(active).Items.Single().IsCleanupEligible);
        Assert.IsTrue(environment.Inventory.CreateInventory().Items.Single().IsCleanupEligible);
        Assert.IsTrue(File.Exists(source.Path));
    }

    [TestMethod]
    public void DependencyProviderCarriesActiveOperationAndPersistentExtractionBoundary()
    {
        using var environment = new ManagedStorageTestEnvironment();
        var correlation = OperationCorrelation.CreateNew();
        var workflow = new SnapshotWorkflowCoordinator(new WorkflowStateSnapshot(
            HasValidSourceSelection: true,
            WorkflowArtifactStatus.Current,
            WorkflowArtifactStatus.Current,
            WorkflowArtifactStatus.Current,
            new ActiveWorkflowOperation(WorkflowOperationKind.Export, correlation),
            new WorkflowOperationStatus(
                WorkflowOperationKind.Export,
                correlation,
                WorkflowOperationState.Cancelling,
                "Cancelling")));
        var persistent = Path.Combine(environment.TestRoot, "Persistent");
        var settings = ApplicationSettings.CreateDefault(environment.LocalApplicationDataDirectory)
            with
        {
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = persistent
        };
        var settingsService = new ApplicationSettingsService(
            new ApplicationSettingsStore(environment.LocalApplicationDataDirectory));
        Assert.IsTrue(settingsService.Save(settings).Succeeded);
        var provider = new ManagedStorageDependencySnapshotProvider(
            workflow,
            new ActiveLoadedSourceSet(),
            settingsService);

        var snapshot = provider.CreateSnapshot();

        CollectionAssert.Contains(snapshot.ActiveOperationIds.ToArray(), correlation.OperationId);
        Assert.IsTrue(snapshot.KnownProtectedLocations.Any(location =>
            Path.GetFullPath(location.Path) == Path.GetFullPath(persistent)
            && location.Kind == ManagedStorageArtifactKind.PersistentArchiveExtraction));
    }

    private static ManagedStorageDependencySnapshot Snapshot(
        IReadOnlyList<OperationId>? activeOperations = null,
        IReadOnlyList<ManagedStorageSourceDependency>? activeSources = null,
        IReadOnlyList<ManagedStorageRetainedDependency>? retained = null,
        IReadOnlyList<ManagedStorageProtectedLocation>? protectedLocations = null) =>
        new(
            activeOperations ?? [],
            activeSources ?? [],
            retained ?? [],
            protectedLocations ?? []);

    private static bool IsWithin(string path, string root)
    {
        var canonicalPath = Path.GetFullPath(path);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return canonicalPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || canonicalPath.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException exception)
            when (exception.Message.Contains(
                "required privilege",
                StringComparison.OrdinalIgnoreCase))
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("The junction helper could not be started.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The test junction could not be created: {process.StandardError.ReadToEnd()}");
            }
        }
    }

    public enum ProtectedPathKind
    {
        Settings,
        SettingsBootstrap,
        Profiles,
        Database,
        DatabaseWal,
        DatabaseShm,
        Logs,
        Clef,
        InstalledBinary
    }

    private sealed class ManagedStorageTestEnvironment : IDisposable
    {
        private readonly string _safeRoot;
        private readonly JsonSerializerOptions _metadataOptions;

        public ManagedStorageTestEnvironment(
            Func<ApplicationPaths, string>? temporaryDirectorySelector = null)
        {
            _safeRoot = Path.Combine(Path.GetTempPath(), "CIA.SPR106.Tests");
            TestRoot = Path.Combine(_safeRoot, Guid.NewGuid().ToString("N"));
            LocalApplicationDataDirectory = Path.Combine(TestRoot, "LocalAppData");
            var defaults = ApplicationPaths.FromLocalApplicationData(LocalApplicationDataDirectory);
            var settings = ApplicationSettings.CreateDefault(LocalApplicationDataDirectory) with
            {
                TemporaryDirectory = temporaryDirectorySelector?.Invoke(defaults)
                    ?? Path.Combine(TestRoot, "Runtime", "Temp"),
                WorkingDirectory = Path.Combine(TestRoot, "Runtime", "Working")
            };
            Paths = ApplicationPaths.FromSettings(LocalApplicationDataDirectory, settings);
            Paths.EnsureWritableDirectoriesExist();
            Metadata = new ManagedStorageOwnershipMetadataStore();
            Inventory = new ManagedStorageInventoryService(Paths, Metadata);
            _metadataOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            _metadataOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }

        public string TestRoot { get; }

        public string LocalApplicationDataDirectory { get; }

        public ApplicationPaths Paths { get; }

        public ManagedStorageOwnershipMetadataStore Metadata { get; }

        public ManagedStorageInventoryService Inventory { get; }

        public Artifact CreateArtifact(
            string root,
            ManagedStorageArtifactKind kind,
            ManagedStorageLifecycle lifecycle,
            OperationId? operationId = null,
            SourceId? sourceId = null,
            SourceSetId? sourceSetId = null,
            DateTimeOffset? createdAtUtc = null,
            string payload = "payload")
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "payload.bin"), payload);
            var metadata = CreateMetadata(
                path,
                kind,
                lifecycle,
                operationId,
                sourceId,
                sourceSetId,
                createdAtUtc);
            Metadata.Write(path, metadata);
            return new Artifact(Path.GetFullPath(path), metadata);
        }

        public ManagedStorageOwnershipMetadata CreateMetadata(
            string path,
            ManagedStorageArtifactKind kind,
            ManagedStorageLifecycle lifecycle,
            OperationId? operationId = null,
            SourceId? sourceId = null,
            SourceSetId? sourceSetId = null,
            DateTimeOffset? createdAtUtc = null) =>
            new(
                ManagedStorageOwnershipMetadata.CurrentSchemaVersion,
                ManagedStorageArtifactId.CreateNew(),
                kind,
                lifecycle,
                Path.GetFullPath(path),
                operationId,
                sourceId,
                sourceSetId,
                createdAtUtc ?? DateTimeOffset.UtcNow);

        public string CreateUnownedDirectory(string root, string name)
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteRawMetadata(string directory, ManagedStorageOwnershipMetadata metadata)
        {
            File.WriteAllText(
                Path.Combine(directory, ManagedStorageOwnershipMetadataStore.FileName),
                JsonSerializer.Serialize(metadata, _metadataOptions));
        }

        public string GetProtectedPath(ProtectedPathKind kind) =>
            kind switch
            {
                ProtectedPathKind.Settings => Path.Combine(Paths.SettingsDirectory, "application-settings.json"),
                ProtectedPathKind.SettingsBootstrap => Path.Combine(
                    Paths.ApplicationDataDirectory,
                    ApplicationSettingsStore.BootstrapFileName),
                ProtectedPathKind.Profiles => Path.Combine(Paths.ProfilesDirectory, "profile.json"),
                ProtectedPathKind.Database => Path.Combine(Paths.DatabaseDirectory, "cia.sqlite"),
                ProtectedPathKind.DatabaseWal => Path.Combine(Paths.DatabaseDirectory, "cia.sqlite-wal"),
                ProtectedPathKind.DatabaseShm => Path.Combine(Paths.DatabaseDirectory, "cia.sqlite-shm"),
                ProtectedPathKind.Logs => Path.Combine(Paths.LogsDirectory, "processing.log"),
                ProtectedPathKind.Clef => Path.Combine(Paths.LogsDirectory, "processing.clef"),
                ProtectedPathKind.InstalledBinary => Path.Combine(Paths.InstalledBinaryDirectory, "CIA.exe"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };

        public void WriteProtectedRootSentinels()
        {
            foreach (var path in new[]
                     {
                         GetProtectedPath(ProtectedPathKind.Settings),
                         GetProtectedPath(ProtectedPathKind.Profiles),
                         GetProtectedPath(ProtectedPathKind.Database),
                         GetProtectedPath(ProtectedPathKind.Clef),
                         GetProtectedPath(ProtectedPathKind.InstalledBinary)
                     })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "protected");
            }
        }

        public string CreateArchive(string fileName, string entryName, string content)
        {
            var sourceDirectory = Path.Combine(TestRoot, "Sources");
            Directory.CreateDirectory(sourceDirectory);
            var archivePath = Path.Combine(sourceDirectory, fileName);
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
            return archivePath;
        }

        public void Dispose()
        {
            if (!Directory.Exists(TestRoot))
            {
                return;
            }

            var safePrefix = Path.GetFullPath(_safeRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(TestRoot);
            if (!target.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to remove a managed-storage test directory outside its safe root.");
            }

            Directory.Delete(target, recursive: true);
        }

        public sealed record Artifact(
            string Path,
            ManagedStorageOwnershipMetadata Metadata);
    }

    private sealed class SnapshotWorkflowCoordinator(WorkflowStateSnapshot current)
        : IApplicationWorkflowCoordinator
    {
        public WorkflowStateSnapshot Current { get; } = current;

        public event EventHandler<WorkflowStateSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public WorkflowCommandResult RecordSourceSelectionChanged(bool hasValidSourceSelection) =>
            throw new NotSupportedException();

        public WorkflowCommandResult RecordDiscoveryConfigurationChanged() =>
            throw new NotSupportedException();

        public WorkflowCommandResult RecordDatabaseReviewChanged() =>
            throw new NotSupportedException();

        public WorkflowCommandResult RecordWorkingStateRestored(
            bool hasValidSourceSelection,
            bool hasPublishedDatabase) => throw new NotSupportedException();

        public Task<WorkflowCommandResult> BeginOperationAsync(
            WorkflowOperationKind operationKind,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WorkflowCommandResult EvaluateOperationPrerequisites(
            WorkflowOperationKind operationKind) => throw new NotSupportedException();

        public Task<WorkflowCommandResult> RequestCancellationAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public WorkflowCommandResult CompleteOperation(OperationId operationId, OperationOutcome outcome) =>
            throw new NotSupportedException();

        public WorkflowCommandResult CompleteOperation(OperationCompletion completion) =>
            throw new NotSupportedException();

        public void RestoreInterruptedOperationStatus(
            WorkflowOperationKind operationKind,
            OperationCorrelation correlation,
            string detail) => throw new NotSupportedException();

        public void InterruptActiveOperationForShutdown() => throw new NotSupportedException();
    }
}
