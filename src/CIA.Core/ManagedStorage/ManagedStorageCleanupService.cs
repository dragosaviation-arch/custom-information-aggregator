namespace CIA.Core.ManagedStorage;

public enum ManagedStorageCleanupOutcome
{
    CompletedSuccessfully = 1,
    CompletedWithItemFailures = 2,
    Failed = 3
}

public enum ManagedStorageCleanupItemState
{
    Removed = 1,
    SkippedNoLongerEligible = 2,
    FailedToRemove = 3
}

public sealed record ManagedStorageCleanupItemResult(
    ManagedStorageArtifactId? ArtifactId,
    string CanonicalPath,
    ManagedStorageCleanupItemState State,
    IReadOnlyList<ManagedStorageProtectionReason> ProtectionReasons,
    string? Problem);

public sealed record ManagedStorageCleanupResult(
    ManagedStorageCleanupOutcome Outcome,
    IReadOnlyList<ManagedStorageCleanupItemResult> Items,
    string? FailureDescription)
{
    public int RemovedCount => Items.Count(item => item.State == ManagedStorageCleanupItemState.Removed);

    public int SkippedCount => Items.Count(
        item => item.State == ManagedStorageCleanupItemState.SkippedNoLongerEligible);

    public int FailedCount => Items.Count(
        item => item.State == ManagedStorageCleanupItemState.FailedToRemove);
}

public interface IManagedStorageCleanupService
{
    ManagedStorageCleanupResult Cleanup();
}

public interface IManagedStorageArtifactDeleter
{
    void Delete(string canonicalArtifactPath);

    bool Exists(string canonicalArtifactPath);
}

public sealed class ManagedStorageCleanupService(
    ManagedStorageInventoryService inventoryService,
    IManagedStorageDependencySnapshotProvider dependencySnapshotProvider,
    IManagedStorageArtifactDeleter artifactDeleter) : IManagedStorageCleanupService
{
    private int _isRunning;

    public ManagedStorageCleanupResult Cleanup()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return Failed("Managed-storage cleanup is already running.");
        }

        try
        {
            ManagedStorageInventory inventory;
            try
            {
                var dependencies = dependencySnapshotProvider.CreateSnapshot();
                inventory = inventoryService.CreateInventory(dependencies);
            }
            catch (Exception exception)
            {
                return Failed($"Managed-storage cleanup could not create a safe inventory ({exception.Message}).");
            }

            var results = new List<ManagedStorageCleanupItemResult>();
            foreach (var candidate in inventory.CleanupCandidates)
            {
                results.Add(CleanupCandidate(candidate));
            }

            var outcome = results.Any(item =>
                item.State == ManagedStorageCleanupItemState.FailedToRemove)
                    ? ManagedStorageCleanupOutcome.CompletedWithItemFailures
                    : ManagedStorageCleanupOutcome.CompletedSuccessfully;
            return new ManagedStorageCleanupResult(outcome, results, FailureDescription: null);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private ManagedStorageCleanupItemResult CleanupCandidate(
        ManagedStorageArtifactClassification candidate)
    {
        ManagedStorageArtifactClassification current;
        try
        {
            var currentDependencies = dependencySnapshotProvider.CreateSnapshot();
            current = inventoryService.ClassifyPath(
                candidate.CanonicalPath,
                currentDependencies);
        }
        catch (Exception exception)
        {
            return FailedItem(
                candidate,
                $"The artifact could not be safely revalidated ({exception.Message}).");
        }

        if (!current.IsCleanupEligible)
        {
            return new ManagedStorageCleanupItemResult(
                candidate.ArtifactId,
                candidate.CanonicalPath,
                ManagedStorageCleanupItemState.SkippedNoLongerEligible,
                current.ProtectionReasons,
                current.InspectionProblem
                    ?? "The artifact is no longer eligible for cleanup.");
        }

        if (candidate.ArtifactId is null
            || current.ArtifactId != candidate.ArtifactId
            || !PathsEqual(current.CanonicalPath, candidate.CanonicalPath))
        {
            return new ManagedStorageCleanupItemResult(
                candidate.ArtifactId,
                candidate.CanonicalPath,
                ManagedStorageCleanupItemState.SkippedNoLongerEligible,
                [ManagedStorageProtectionReason.OwnershipPathMismatch],
                "The owned artifact identity changed before cleanup.");
        }

        try
        {
            artifactDeleter.Delete(candidate.CanonicalPath);
            if (artifactDeleter.Exists(candidate.CanonicalPath))
            {
                return FailedItem(
                    candidate,
                    "The artifact remained present after deletion was attempted.");
            }

            return new ManagedStorageCleanupItemResult(
                candidate.ArtifactId,
                candidate.CanonicalPath,
                ManagedStorageCleanupItemState.Removed,
                ProtectionReasons: [],
                Problem: null);
        }
        catch (Exception exception)
        {
            return FailedItem(
                candidate,
                $"The artifact could not be removed ({exception.Message}).");
        }
    }

    private static ManagedStorageCleanupItemResult FailedItem(
        ManagedStorageArtifactClassification candidate,
        string problem) =>
        new(
            candidate.ArtifactId,
            candidate.CanonicalPath,
            ManagedStorageCleanupItemState.FailedToRemove,
            ProtectionReasons: [],
            problem);

    private static ManagedStorageCleanupResult Failed(string description) =>
        new(
            ManagedStorageCleanupOutcome.Failed,
            Items: [],
            description);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}

public sealed class FileSystemManagedStorageArtifactDeleter : IManagedStorageArtifactDeleter
{
    public void Delete(string canonicalArtifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalArtifactPath);
        var root = Canonicalize(canonicalArtifactPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The managed artifact directory no longer exists.");
        }

        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Cleanup does not remove a candidate root that is a reparse point.");
        }

        Directory.Delete(root, recursive: true);
    }

    public bool Exists(string canonicalArtifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalArtifactPath);
        var path = Canonicalize(canonicalArtifactPath);
        return Directory.Exists(path) || File.Exists(path);
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
