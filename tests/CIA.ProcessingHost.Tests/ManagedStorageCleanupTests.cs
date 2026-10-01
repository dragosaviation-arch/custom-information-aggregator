using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.ManagedStorage;
using CIA.Core.Runtime;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ManagedStorageCleanupTests
{
    [TestMethod]
    public void EligibleOwnedTemporaryAndUnneededSessionArtifactsAreActuallyRemoved()
    {
        using var environment = new CleanupTestEnvironment();
        var operationArtifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "operation",
            ManagedStorageArtifactKind.TemporaryOperationArtifact,
            ManagedStorageLifecycle.OperationTemporary,
            operationId: OperationId.CreateNew());
        var sessionArtifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "session-archive",
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew());
        var service = environment.CreateService(ManagedStorageDependencySnapshot.Empty);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedSuccessfully, result.Outcome);
        Assert.AreEqual(2, result.RemovedCount);
        Assert.IsTrue(result.Items.All(item => item.State == ManagedStorageCleanupItemState.Removed));
        Assert.IsFalse(Directory.Exists(operationArtifact.Path));
        Assert.IsFalse(Directory.Exists(sessionArtifact.Path));
        Assert.IsTrue(Directory.Exists(environment.Paths.TempDirectory));
        Assert.IsTrue(Directory.Exists(environment.Paths.WorkingDirectory));
    }

    [TestMethod]
    public void ActiveAndPersistentDependenciesRemainUntouched()
    {
        using var environment = new CleanupTestEnvironment();
        var activeOperationId = OperationId.CreateNew();
        var activeOperation = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "active-operation",
            ManagedStorageArtifactKind.TemporaryOperationArtifact,
            ManagedStorageLifecycle.OperationTemporary,
            operationId: activeOperationId);
        var activeIntake = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "active-intake",
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew(),
            keepIntakeActive: true);
        var activeSource = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "active-source",
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: SourceId.CreateNew());
        var persistent = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "persistent",
            ManagedStorageArtifactKind.PersistentArchiveExtraction,
            ManagedStorageLifecycle.Persistent);
        var dependencies = new ManagedStorageDependencySnapshot(
            [activeOperationId],
            [activeIntake.IntakeActivityId!.Value],
            [CreateSourceDependency(activeSource)],
            RetainedResultDependencies: [],
            KnownProtectedLocations: []);
        var service = environment.CreateService(dependencies);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedSuccessfully, result.Outcome);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.IsTrue(Directory.Exists(activeOperation.Path));
        Assert.IsTrue(Directory.Exists(activeIntake.Path));
        Assert.IsTrue(Directory.Exists(activeSource.Path));
        Assert.IsTrue(Directory.Exists(persistent.Path));
    }

    [TestMethod]
    public void SupersededArchiveExtractionIsRemovedWhileCurrentExtractionStaysProtected()
    {
        using var environment = new CleanupTestEnvironment();
        var archiveIdentity = SourceId.CreateNew();
        var superseded = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "archive-a",
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: archiveIdentity);
        var current = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "archive-b",
            ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction,
            ManagedStorageLifecycle.SessionTemporary,
            sourceId: archiveIdentity);
        var dependencies = new ManagedStorageDependencySnapshot(
            ActiveOperationIds: [],
            ActiveIntakeActivityIds: [],
            ActiveSources: [CreateSourceDependency(current, archiveIdentity)],
            RetainedResultDependencies: [],
            KnownProtectedLocations: []);
        var service = environment.CreateService(dependencies);

        var result = service.Cleanup();

        Assert.AreEqual(1, result.RemovedCount);
        Assert.IsFalse(Directory.Exists(superseded.Path));
        Assert.IsTrue(Directory.Exists(current.Path));
    }

    [TestMethod]
    public void ExternalAndRepresentativeProtectedOrUnownedContentRemainsUntouched()
    {
        using var environment = new CleanupTestEnvironment();
        var completedExport = environment.WriteFile(
            environment.Paths.TempDirectory,
            "completed.xlsx");
        var savedState = environment.WriteFile(
            environment.Paths.WorkingDirectory,
            "saved.cia");
        var unowned = Path.Combine(environment.Paths.TempDirectory, "unowned");
        Directory.CreateDirectory(unowned);
        var malformed = Path.Combine(environment.Paths.WorkingDirectory, "malformed");
        Directory.CreateDirectory(malformed);
        File.WriteAllText(
            Path.Combine(malformed, ManagedStorageOwnershipMetadataStore.FileName),
            "{not-valid-json");
        var externalSource = environment.WriteFile(environment.ExternalDirectory, "source.xml");
        var profile = environment.WriteFile(environment.Paths.ProfilesDirectory, "profile.cia-profile");
        var diagnostic = environment.WriteFile(environment.Paths.LogsDirectory, "processing.clef");
        var service = environment.CreateService(ManagedStorageDependencySnapshot.Empty);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedSuccessfully, result.Outcome);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.IsTrue(File.Exists(completedExport));
        Assert.IsTrue(File.Exists(savedState));
        Assert.IsTrue(Directory.Exists(unowned));
        Assert.IsTrue(Directory.Exists(malformed));
        Assert.IsTrue(File.Exists(externalSource));
        Assert.IsTrue(File.Exists(profile));
        Assert.IsTrue(File.Exists(diagnostic));
    }

    [TestMethod]
    public void CandidateThatBecomesProtectedIsSkippedDuringFreshRevalidation()
    {
        using var environment = new CleanupTestEnvironment();
        var operationId = OperationId.CreateNew();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "race-protected",
            ManagedStorageArtifactKind.TemporaryOperationArtifact,
            ManagedStorageLifecycle.OperationTemporary,
            operationId: operationId);
        var provider = new CallbackSnapshotProvider(call => call == 1
            ? ManagedStorageDependencySnapshot.Empty
            : new ManagedStorageDependencySnapshot(
                [operationId], [], [], [], []));
        var service = environment.CreateService(provider);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedSuccessfully, result.Outcome);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(
            ManagedStorageCleanupItemState.SkippedNoLongerEligible,
            result.Items.Single().State);
        CollectionAssert.Contains(
            result.Items.Single().ProtectionReasons.ToArray(),
            ManagedStorageProtectionReason.ActiveOperation);
        Assert.IsTrue(Directory.Exists(artifact.Path));
    }

    [TestMethod]
    public void CandidateWhoseArtifactIdentityChangesIsSkipped()
    {
        using var environment = new CleanupTestEnvironment();
        var artifact = environment.CreateArtifact(
            environment.Paths.WorkingDirectory,
            "identity-change",
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var replacementId = ManagedStorageArtifactId.CreateNew();
        var provider = new CallbackSnapshotProvider(call =>
        {
            if (call == 2)
            {
                File.Delete(Path.Combine(
                    artifact.Path,
                    ManagedStorageOwnershipMetadataStore.FileName));
                environment.Metadata.Write(
                    artifact.Path,
                    artifact.Metadata with { ArtifactId = replacementId });
            }

            return ManagedStorageDependencySnapshot.Empty;
        });
        var service = environment.CreateService(provider);

        var result = service.Cleanup();

        Assert.AreEqual(1, result.SkippedCount);
        Assert.IsTrue(Directory.Exists(artifact.Path));
        Assert.AreEqual(
            replacementId,
            environment.Metadata.Read(artifact.Path).Metadata?.ArtifactId);
    }

    [TestMethod]
    public void UndeletedArtifactIsReportedAsFailureRatherThanRemoved()
    {
        using var environment = new CleanupTestEnvironment();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "not-deleted",
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var service = environment.CreateService(
            new CallbackSnapshotProvider(_ => ManagedStorageDependencySnapshot.Empty),
            new NoOpArtifactDeleter());

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedWithItemFailures, result.Outcome);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(
            ManagedStorageCleanupItemState.FailedToRemove,
            result.Items.Single().State);
        Assert.IsTrue(Directory.Exists(artifact.Path));
    }

    [TestMethod]
    public void MixedDeletionSuccessAndFailureReturnsCompletedWithItemFailures()
    {
        using var environment = new CleanupTestEnvironment();
        var removable = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "remove",
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var failing = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "fail",
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var deleter = new SelectiveFailureArtifactDeleter(failing.Path);
        var service = environment.CreateService(
            new CallbackSnapshotProvider(_ => ManagedStorageDependencySnapshot.Empty),
            deleter);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedWithItemFailures, result.Outcome);
        Assert.AreEqual(1, result.RemovedCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.IsFalse(Directory.Exists(removable.Path));
        Assert.IsTrue(Directory.Exists(failing.Path));
    }

    [TestMethod]
    public void FatalInventoryFailureReturnsFailedWithoutDeletingAnything()
    {
        using var environment = new CleanupTestEnvironment();
        var artifact = environment.CreateArtifact(
            environment.Paths.TempDirectory,
            "fatal",
            ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            ManagedStorageLifecycle.Intermediate);
        var provider = new ThrowingSnapshotProvider();
        var service = environment.CreateService(provider);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, result.RemovedCount);
        StringAssert.Contains(result.FailureDescription, "safe inventory");
        Assert.IsTrue(Directory.Exists(artifact.Path));
    }

    [TestMethod]
    public void NoCandidatesIsSuccessfulAndCleanupRootsRemainPresent()
    {
        using var environment = new CleanupTestEnvironment();
        var service = environment.CreateService(ManagedStorageDependencySnapshot.Empty);

        var result = service.Cleanup();

        Assert.AreEqual(ManagedStorageCleanupOutcome.CompletedSuccessfully, result.Outcome);
        Assert.AreEqual(0, result.RemovedCount);
        Assert.HasCount(0, result.Items);
        Assert.IsTrue(Directory.Exists(environment.Paths.TempDirectory));
        Assert.IsTrue(Directory.Exists(environment.Paths.WorkingDirectory));
    }

    private static ManagedStorageSourceDependency CreateSourceDependency(
        Artifact artifact,
        SourceId? originalArchiveSourceId = null) =>
        new(
            SourceId.CreateNew(),
            artifact.Metadata.SourceSetId ?? SourceSetId.CreateNew(),
            originalArchiveSourceId ?? artifact.Metadata.SourceId,
            Path.Combine(Path.GetDirectoryName(artifact.Path)!, "external-source.zip"),
            artifact.Path,
            ArchiveExtractionRetention.ManagedTemporary);

    private sealed class CallbackSnapshotProvider(
        Func<int, ManagedStorageDependencySnapshot> createSnapshot)
        : IManagedStorageDependencySnapshotProvider
    {
        private int _callCount;

        public ManagedStorageDependencySnapshot CreateSnapshot() =>
            createSnapshot(Interlocked.Increment(ref _callCount));
    }

    private sealed class ThrowingSnapshotProvider : IManagedStorageDependencySnapshotProvider
    {
        public ManagedStorageDependencySnapshot CreateSnapshot() =>
            throw new IOException("Simulated dependency-snapshot failure.");
    }

    private sealed class NoOpArtifactDeleter : IManagedStorageArtifactDeleter
    {
        public void Delete(string canonicalArtifactPath)
        {
        }

        public bool Exists(string canonicalArtifactPath) => true;
    }

    private sealed class SelectiveFailureArtifactDeleter(string failingPath)
        : IManagedStorageArtifactDeleter
    {
        private readonly FileSystemManagedStorageArtifactDeleter _inner = new();

        public void Delete(string canonicalArtifactPath)
        {
            if (string.Equals(
                Path.GetFullPath(canonicalArtifactPath),
                Path.GetFullPath(failingPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated deletion failure.");
            }

            _inner.Delete(canonicalArtifactPath);
        }

        public bool Exists(string canonicalArtifactPath) => _inner.Exists(canonicalArtifactPath);
    }

    private sealed class CleanupTestEnvironment : IDisposable
    {
        private readonly string _safeRoot;
        private readonly List<ManagedStorageIntakeActivityLease> _activeLeases = [];

        public CleanupTestEnvironment()
        {
            _safeRoot = Path.Combine(Path.GetTempPath(), "CIA.SPR105.Tests");
            Root = Path.Combine(_safeRoot, Guid.NewGuid().ToString("N"));
            var localAppData = Path.Combine(Root, "LocalAppData");
            var defaults = ApplicationPaths.FromLocalApplicationData(localAppData);
            var settings = ApplicationSettings.CreateDefault(localAppData) with
            {
                TemporaryDirectory = Path.Combine(Root, "Runtime", "Temp"),
                WorkingDirectory = Path.Combine(Root, "Runtime", "Working")
            };
            Paths = ApplicationPaths.FromSettings(localAppData, settings);
            Paths.EnsureWritableDirectoriesExist();
            ExternalDirectory = Path.Combine(Root, "External");
            Directory.CreateDirectory(ExternalDirectory);
            Metadata = new ManagedStorageOwnershipMetadataStore();
            Inventory = new ManagedStorageInventoryService(Paths, Metadata);
        }

        public string Root { get; }

        public string ExternalDirectory { get; }

        public ApplicationPaths Paths { get; }

        public ManagedStorageOwnershipMetadataStore Metadata { get; }

        public ManagedStorageInventoryService Inventory { get; }

        public Artifact CreateArtifact(
            string root,
            string name,
            ManagedStorageArtifactKind kind,
            ManagedStorageLifecycle lifecycle,
            OperationId? operationId = null,
            SourceId? sourceId = null,
            bool keepIntakeActive = false)
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "payload.bin"), "payload");
            var activityId = kind == ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction
                ? SourceIntakeActivityId.CreateNew()
                : (SourceIntakeActivityId?)null;
            ManagedStorageIntakeActivityLease? lease = activityId is { } id
                ? Metadata.BeginIntakeActivity(path, id)
                : null;
            var metadata = new ManagedStorageOwnershipMetadata(
                ManagedStorageOwnershipMetadata.CurrentSchemaVersion,
                ManagedStorageArtifactId.CreateNew(),
                kind,
                lifecycle,
                path,
                operationId,
                sourceId,
                SourceSetId.CreateNew(),
                DateTimeOffset.UtcNow) with
            {
                IntakeActivityId = activityId
            };
            Metadata.Write(path, metadata);
            if (lease is not null)
            {
                if (keepIntakeActive)
                {
                    _activeLeases.Add(lease);
                }
                else
                {
                    lease.Dispose();
                }
            }

            return new Artifact(path, metadata, activityId);
        }

        public string WriteFile(string directory, string name)
        {
            Directory.CreateDirectory(directory);
            var path = Path.GetFullPath(Path.Combine(directory, name));
            File.WriteAllText(path, "protected");
            return path;
        }

        public ManagedStorageCleanupService CreateService(
            ManagedStorageDependencySnapshot snapshot) =>
            CreateService(new CallbackSnapshotProvider(_ => snapshot));

        public ManagedStorageCleanupService CreateService(
            IManagedStorageDependencySnapshotProvider provider,
            IManagedStorageArtifactDeleter? deleter = null) =>
            new(Inventory, provider, deleter ?? new FileSystemManagedStorageArtifactDeleter());

        public void Dispose()
        {
            foreach (var lease in _activeLeases)
            {
                lease.Dispose();
            }

            if (!Directory.Exists(Root))
            {
                return;
            }

            var safePrefix = Path.GetFullPath(_safeRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete outside the cleanup test root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }

    private sealed record Artifact(
        string Path,
        ManagedStorageOwnershipMetadata Metadata,
        SourceIntakeActivityId? IntakeActivityId);
}
