using CIA.Core.Runtime;
using CIA.Contracts.Sources;

namespace CIA.Core.ManagedStorage;

public sealed class ManagedStorageInventoryService
{
    private readonly ApplicationPaths _paths;
    private readonly ManagedStorageOwnershipMetadataStore _metadataStore;

    public ManagedStorageInventoryService(
        ApplicationPaths paths,
        ManagedStorageOwnershipMetadataStore? metadataStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _metadataStore = metadataStore ?? new ManagedStorageOwnershipMetadataStore();
    }

    public ManagedStorageInventory CreateInventory(
        ManagedStorageDependencySnapshot? dependencies = null)
    {
        var snapshot = dependencies ?? ManagedStorageDependencySnapshot.Empty;
        var items = new List<ManagedStorageArtifactClassification>();
        var dependencyEvidenceIsInvalid = HasInvalidDependencyEvidence(snapshot);

        foreach (var root in GetApprovedRoots())
        {
            InspectApprovedRoot(root, snapshot, items);
        }

        return new ManagedStorageInventory(
            items
                .Select(item => dependencyEvidenceIsInvalid
                    ? AddProtection(item, ManagedStorageProtectionReason.InspectionFailed)
                    : item)
                .OrderBy(item => item.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public ManagedStorageArtifactClassification ClassifyPath(
        string path,
        ManagedStorageDependencySnapshot? dependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var snapshot = dependencies ?? ManagedStorageDependencySnapshot.Empty;
        string canonicalPath;
        try
        {
            canonicalPath = Canonicalize(path);
        }
        catch (Exception exception) when (IsControlledInspectionFailure(exception))
        {
            return Protected(
                Path.GetFullPath(_paths.TempDirectory),
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.InvalidOrEscapingPath,
                exception.Message);
        }

        if (HasInvalidDependencyEvidence(snapshot))
        {
            return Protected(
                canonicalPath,
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.InspectionFailed,
                "Managed-storage dependency evidence contains an invalid path.");
        }

        if (TryClassifyKnownProtectedPath(canonicalPath, snapshot, out var classification))
        {
            return classification;
        }

        if (File.Exists(canonicalPath))
        {
            return Protected(
                canonicalPath,
                InferProtectedFileKind(canonicalPath),
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.MissingOwnershipMetadata);
        }

        var approvedRoot = GetApprovedRoots().FirstOrDefault(root =>
            IsWithin(canonicalPath, root.Path));
        if (approvedRoot is null)
        {
            return Protected(
                canonicalPath,
                ManagedStorageArtifactKind.OriginalOrExternalSource,
                ManagedStorageLifecycle.External,
                ManagedStorageProtectionReason.OutsideApprovedCleanupRoot);
        }

        return ClassifyOwnedArtifact(canonicalPath, approvedRoot, snapshot);
    }

    private void InspectApprovedRoot(
        ApprovedCleanupRoot root,
        ManagedStorageDependencySnapshot snapshot,
        List<ManagedStorageArtifactClassification> items)
    {
        try
        {
            if (!Directory.Exists(root.Path))
            {
                return;
            }

            if (TryClassifyKnownProtectedPath(root.Path, snapshot, out var protectedRoot))
            {
                items.Add(protectedRoot);
                return;
            }

            if (OverlapsProtectedManagedRoot(root.Path)
                || OverlapsAnotherCleanupRoot(root)
                || HasReparsePoint(root.Path, stopAt: null))
            {
                items.Add(Protected(
                    root.Path,
                    ManagedStorageArtifactKind.UnknownUnsafe,
                    ManagedStorageLifecycle.Unknown,
                    OverlapsProtectedManagedRoot(root.Path)
                    || OverlapsAnotherCleanupRoot(root)
                        ? ManagedStorageProtectionReason.ProtectedManagedRoot
                        : ManagedStorageProtectionReason.ReparsePoint));
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(root.Path))
            {
                InspectEntry(entry, root, snapshot, items);
            }
        }
        catch (Exception exception) when (IsControlledInspectionFailure(exception))
        {
            items.Add(Protected(
                root.Path,
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.InspectionFailed,
                exception.Message));
        }
    }

    private bool InspectEntry(
        string entry,
        ApprovedCleanupRoot root,
        ManagedStorageDependencySnapshot snapshot,
        List<ManagedStorageArtifactClassification> items)
    {
        string canonicalEntry;
        try
        {
            canonicalEntry = Canonicalize(entry);
            if (!IsStrictlyWithin(canonicalEntry, root.Path))
            {
                items.Add(Protected(
                    canonicalEntry,
                    ManagedStorageArtifactKind.UnknownUnsafe,
                    ManagedStorageLifecycle.Unknown,
                    ManagedStorageProtectionReason.InvalidOrEscapingPath));
                return true;
            }

            if (TryClassifyKnownProtectedPath(canonicalEntry, snapshot, out var protectedEntry))
            {
                items.Add(protectedEntry);
                return true;
            }

            var attributes = File.GetAttributes(canonicalEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                items.Add(Protected(
                    canonicalEntry,
                    ManagedStorageArtifactKind.UnknownUnsafe,
                    ManagedStorageLifecycle.Unknown,
                    ManagedStorageProtectionReason.ReparsePoint));
                return true;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                items.Add(Protected(
                    canonicalEntry,
                    InferProtectedFileKind(canonicalEntry),
                    ManagedStorageLifecycle.Unknown,
                    ManagedStorageProtectionReason.MissingOwnershipMetadata));
                return true;
            }

            var metadata = _metadataStore.Read(canonicalEntry);
            if (metadata.State != ManagedStorageMetadataReadState.Missing)
            {
                items.Add(metadata.State == ManagedStorageMetadataReadState.Valid
                    ? ClassifyOwnedArtifact(canonicalEntry, root, snapshot, metadata.Metadata!)
                    : Protected(
                        canonicalEntry,
                        ManagedStorageArtifactKind.UnknownUnsafe,
                        ManagedStorageLifecycle.Unknown,
                        MapMetadataFailure(metadata.State),
                        metadata.Problem));
                return true;
            }

            var foundChild = false;
            foreach (var child in Directory.EnumerateFileSystemEntries(canonicalEntry))
            {
                foundChild |= InspectEntry(child, root, snapshot, items);
            }

            if (!foundChild)
            {
                items.Add(Protected(
                    canonicalEntry,
                    ManagedStorageArtifactKind.UnknownUnsafe,
                    ManagedStorageLifecycle.Unknown,
                    ManagedStorageProtectionReason.MissingOwnershipMetadata));
                return true;
            }

            return true;
        }
        catch (Exception exception) when (IsControlledInspectionFailure(exception))
        {
            items.Add(Protected(
                TryCanonicalize(entry),
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.InspectionFailed,
                exception.Message));
            return true;
        }
    }

    private ManagedStorageArtifactClassification ClassifyOwnedArtifact(
        string canonicalPath,
        ApprovedCleanupRoot root,
        ManagedStorageDependencySnapshot snapshot,
        ManagedStorageOwnershipMetadata? knownMetadata = null)
    {
        if (HasReparsePoint(canonicalPath, root.Path))
        {
            return Protected(
                canonicalPath,
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                ManagedStorageProtectionReason.ReparsePoint);
        }

        var read = knownMetadata is null
            ? _metadataStore.Read(canonicalPath)
            : new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Valid,
                knownMetadata,
                null);
        if (read.State != ManagedStorageMetadataReadState.Valid)
        {
            return Protected(
                canonicalPath,
                ManagedStorageArtifactKind.UnknownUnsafe,
                ManagedStorageLifecycle.Unknown,
                MapMetadataFailure(read.State),
                read.Problem);
        }

        var metadata = read.Metadata!;
        var reasons = new List<ManagedStorageProtectionReason>();
        if (!IsKindEligibleInRoot(metadata.ArtifactKind, root.Kind))
        {
            reasons.Add(ManagedStorageProtectionReason.ArtifactKindNotEligibleInRoot);
        }

        if (metadata.Lifecycle is ManagedStorageLifecycle.Persistent
            or ManagedStorageLifecycle.External)
        {
            reasons.Add(ManagedStorageProtectionReason.PersistentData);
        }

        if (metadata.OperationId is { } operationId
            && snapshot.ActiveOperationIds.Contains(operationId))
        {
            reasons.Add(ManagedStorageProtectionReason.ActiveOperation);
        }

        if (IsNeededByActiveSource(metadata, canonicalPath, snapshot.ActiveSources))
        {
            reasons.Add(ManagedStorageProtectionReason.ActiveSessionSource);
        }

        if (IsRetained(metadata, canonicalPath, snapshot.RetainedResultDependencies))
        {
            reasons.Add(ManagedStorageProtectionReason.RetainedResultDependency);
        }

        if (TryFindKnownProtectedLocation(canonicalPath, snapshot, out _))
        {
            reasons.Add(ManagedStorageProtectionReason.PersistentData);
        }

        var distinctReasons = reasons.Distinct().ToArray();
        return new ManagedStorageArtifactClassification(
            metadata.ArtifactId,
            canonicalPath,
            metadata.ArtifactKind,
            metadata.Lifecycle,
            metadata.OperationId,
            metadata.SourceId,
            metadata.SourceSetId,
            metadata.CreatedAtUtc,
            IsCleanupEligible: distinctReasons.Length == 0,
            distinctReasons,
            InspectionProblem: null);
    }

    private bool TryClassifyKnownProtectedPath(
        string path,
        ManagedStorageDependencySnapshot snapshot,
        out ManagedStorageArtifactClassification classification)
    {
        if (IsWithin(path, _paths.InstalledBinaryDirectory))
        {
            classification = Protected(
                path,
                ManagedStorageArtifactKind.InstalledApplicationBinary,
                ManagedStorageLifecycle.Persistent,
                ManagedStorageProtectionReason.InstalledBinary);
            return true;
        }

        if (PathsEqual(
                path,
                Path.Combine(
                    _paths.ApplicationDataDirectory,
                    ApplicationSettingsStore.BootstrapFileName)))
        {
            classification = Protected(
                path,
                ManagedStorageArtifactKind.SettingsOrBootstrap,
                ManagedStorageLifecycle.Persistent,
                ManagedStorageProtectionReason.ProtectedManagedRoot);
            return true;
        }

        foreach (var protectedRoot in GetBuiltInProtectedRoots())
        {
            if (IsWithin(path, protectedRoot.Path))
            {
                classification = Protected(
                    path,
                    protectedRoot.Kind,
                    ManagedStorageLifecycle.Persistent,
                    ManagedStorageProtectionReason.ProtectedManagedRoot);
                return true;
            }
        }

        if (TryFindKnownProtectedLocation(path, snapshot, out var known))
        {
            classification = Protected(
                path,
                known.Kind,
                known.Kind == ManagedStorageArtifactKind.OriginalOrExternalSource
                    ? ManagedStorageLifecycle.External
                    : ManagedStorageLifecycle.Persistent,
                known.Kind == ManagedStorageArtifactKind.OriginalOrExternalSource
                    ? ManagedStorageProtectionReason.OutsideApprovedCleanupRoot
                    : ManagedStorageProtectionReason.PersistentData);
            return true;
        }

        classification = null!;
        return false;
    }

    private bool TryFindKnownProtectedLocation(
        string path,
        ManagedStorageDependencySnapshot snapshot,
        out ManagedStorageProtectedLocation location)
    {
        foreach (var candidate in snapshot.KnownProtectedLocations)
        {
            try
            {
                var candidatePath = Canonicalize(candidate.Path);
                if (candidate.IncludeDescendants
                    ? IsWithin(path, candidatePath)
                    : PathsEqual(path, candidatePath))
                {
                    location = candidate;
                    return true;
                }
            }
            catch (Exception exception) when (IsControlledInspectionFailure(exception))
            {
                // Invalid dependency evidence cannot make an artifact eligible.
            }
        }

        location = null!;
        return false;
    }

    private IReadOnlyList<ApprovedCleanupRoot> GetApprovedRoots()
    {
        var roots = new[]
        {
            new ApprovedCleanupRoot(Canonicalize(_paths.TempDirectory), CleanupRootKind.Temporary),
            new ApprovedCleanupRoot(Canonicalize(_paths.WorkingDirectory), CleanupRootKind.Working)
        };

        return roots
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<ProtectedRoot> GetBuiltInProtectedRoots() =>
        [
            new(Canonicalize(_paths.SettingsDirectory), ManagedStorageArtifactKind.SettingsOrBootstrap),
            new(Canonicalize(_paths.ProfilesDirectory), ManagedStorageArtifactKind.ProfileData),
            new(Canonicalize(_paths.DatabaseDirectory), ManagedStorageArtifactKind.DatabaseStorage),
            new(Canonicalize(_paths.LogsDirectory), ManagedStorageArtifactKind.DiagnosticOrHistoryStorage)
        ];

    private bool OverlapsProtectedManagedRoot(string cleanupRoot) =>
        IsWithin(cleanupRoot, _paths.InstalledBinaryDirectory)
        || IsWithin(_paths.InstalledBinaryDirectory, cleanupRoot)
        || GetBuiltInProtectedRoots().Any(protectedRoot =>
            IsWithin(cleanupRoot, protectedRoot.Path)
            || IsWithin(protectedRoot.Path, cleanupRoot));

    private bool OverlapsAnotherCleanupRoot(ApprovedCleanupRoot cleanupRoot) =>
        GetApprovedRoots().Any(other =>
            !PathsEqual(cleanupRoot.Path, other.Path)
            && (IsWithin(cleanupRoot.Path, other.Path)
                || IsWithin(other.Path, cleanupRoot.Path)));

    private static bool IsKindEligibleInRoot(
        ManagedStorageArtifactKind kind,
        CleanupRootKind rootKind) =>
        rootKind switch
        {
            CleanupRootKind.Temporary => kind is
                ManagedStorageArtifactKind.TemporaryOperationArtifact
                or ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction
                or ManagedStorageArtifactKind.WorkingStateStagingCandidate
                or ManagedStorageArtifactKind.ExportStagingCandidate
                or ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            CleanupRootKind.Working => kind is
                ManagedStorageArtifactKind.WorkingStateStagingCandidate
                or ManagedStorageArtifactKind.ManagedIntermediateArtifact,
            _ => false
        };

    private static bool IsNeededByActiveSource(
        ManagedStorageOwnershipMetadata metadata,
        string artifactPath,
        IReadOnlyList<ManagedStorageSourceDependency> activeSources)
    {
        foreach (var source in activeSources)
        {
            if (source.ExtractionRetention != ArchiveExtractionRetention.ManagedTemporary)
            {
                continue;
            }

            if (source.ExtractionRoot is { } extractionRoot
                && PathsEqual(artifactPath, TryCanonicalize(extractionRoot)))
            {
                return true;
            }

            if (metadata.SourceId == source.SourceId
                || metadata.SourceId == source.OriginalArchiveSourceId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRetained(
        ManagedStorageOwnershipMetadata metadata,
        string artifactPath,
        IReadOnlyList<ManagedStorageRetainedDependency> retainedDependencies)
    {
        foreach (var dependency in retainedDependencies)
        {
            if (dependency.ArtifactId == metadata.ArtifactId)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(dependency.CanonicalPath)
                && PathsEqual(artifactPath, TryCanonicalize(dependency.CanonicalPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static ManagedStorageArtifactKind InferProtectedFileKind(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cia", StringComparison.OrdinalIgnoreCase)
            ? ManagedStorageArtifactKind.SavedWorkingState
            : extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? ManagedStorageArtifactKind.CompletedExport
                : ManagedStorageArtifactKind.UnknownUnsafe;
    }

    private static ManagedStorageProtectionReason MapMetadataFailure(
        ManagedStorageMetadataReadState state) =>
        state switch
        {
            ManagedStorageMetadataReadState.Missing =>
                ManagedStorageProtectionReason.MissingOwnershipMetadata,
            ManagedStorageMetadataReadState.Malformed =>
                ManagedStorageProtectionReason.MalformedOwnershipMetadata,
            ManagedStorageMetadataReadState.UnsupportedFutureVersion =>
                ManagedStorageProtectionReason.UnsupportedOwnershipMetadataVersion,
            ManagedStorageMetadataReadState.PathMismatch =>
                ManagedStorageProtectionReason.OwnershipPathMismatch,
            ManagedStorageMetadataReadState.ReparsePoint =>
                ManagedStorageProtectionReason.ReparsePoint,
            _ => ManagedStorageProtectionReason.UnknownOrUnowned
        };

    private static ManagedStorageArtifactClassification Protected(
        string path,
        ManagedStorageArtifactKind kind,
        ManagedStorageLifecycle lifecycle,
        ManagedStorageProtectionReason reason,
        string? problem = null) =>
        new(
            ArtifactId: null,
            CanonicalPath: path,
            ArtifactKind: kind,
            Lifecycle: lifecycle,
            OperationId: null,
            SourceId: null,
            SourceSetId: null,
            CreatedAtUtc: null,
            IsCleanupEligible: false,
            ProtectionReasons: [reason],
            InspectionProblem: problem);

    private static ManagedStorageArtifactClassification AddProtection(
        ManagedStorageArtifactClassification classification,
        ManagedStorageProtectionReason reason) =>
        classification with
        {
            IsCleanupEligible = false,
            ProtectionReasons = classification.ProtectionReasons
                .Append(reason)
                .Distinct()
                .ToArray()
        };

    private static bool HasInvalidDependencyEvidence(
        ManagedStorageDependencySnapshot snapshot) =>
        snapshot.ActiveSources.Any(source =>
            !IsValidAbsolutePath(source.Path)
            || source.ExtractionRoot is { } extractionRoot
            && !IsValidAbsolutePath(extractionRoot))
        || snapshot.RetainedResultDependencies.Any(dependency =>
            dependency.CanonicalPath is { } path && !IsValidAbsolutePath(path))
        || snapshot.KnownProtectedLocations.Any(location =>
            !IsValidAbsolutePath(location.Path));

    private static bool IsValidAbsolutePath(string path)
    {
        try
        {
            _ = Canonicalize(path);
            return true;
        }
        catch (Exception exception) when (IsControlledInspectionFailure(exception))
        {
            return false;
        }
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Managed-storage paths must be absolute.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string TryCanonicalize(string path)
    {
        try
        {
            return Canonicalize(path);
        }
        catch (Exception exception) when (IsControlledInspectionFailure(exception))
        {
            return path;
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var canonicalPath = Canonicalize(path);
        var canonicalDirectory = Canonicalize(directory);
        return PathsEqual(canonicalPath, canonicalDirectory)
            || canonicalPath.StartsWith(
                canonicalDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictlyWithin(string path, string directory) =>
        !PathsEqual(Canonicalize(path), Canonicalize(directory))
        && IsWithin(path, directory);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePoint(string path, string? stopAt)
    {
        var current = new DirectoryInfo(Canonicalize(path));
        var canonicalStop = stopAt is null ? null : Canonicalize(stopAt);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (canonicalStop is not null && PathsEqual(current.FullName, canonicalStop))
            {
                break;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsControlledInspectionFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidDataException;

    private sealed record ApprovedCleanupRoot(string Path, CleanupRootKind Kind);

    private sealed record ProtectedRoot(string Path, ManagedStorageArtifactKind Kind);

    private enum CleanupRootKind
    {
        Temporary = 1,
        Working = 2
    }
}
