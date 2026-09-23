using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Core.ManagedStorage;

public enum ManagedStorageArtifactKind
{
    UnknownUnsafe = 0,
    TemporaryOperationArtifact = 1,
    ManagedTemporaryArchiveExtraction = 2,
    WorkingStateStagingCandidate = 3,
    ExportStagingCandidate = 4,
    ManagedIntermediateArtifact = 5,
    PersistentArchiveExtraction = 6,
    SavedWorkingState = 7,
    CompletedExport = 8,
    ProfileData = 9,
    SettingsOrBootstrap = 10,
    DatabaseStorage = 11,
    DiagnosticOrHistoryStorage = 12,
    InstalledApplicationBinary = 13,
    OriginalOrExternalSource = 14
}

public enum ManagedStorageLifecycle
{
    Unknown = 0,
    OperationTemporary = 1,
    SessionTemporary = 2,
    Intermediate = 3,
    Staging = 4,
    Persistent = 5,
    External = 6
}

public enum ManagedStorageProtectionReason
{
    UnknownOrUnowned = 1,
    ActiveOperation = 2,
    ActiveSessionSource = 3,
    RetainedResultDependency = 4,
    PersistentData = 5,
    OutsideApprovedCleanupRoot = 6,
    ProtectedManagedRoot = 7,
    InstalledBinary = 8,
    ReparsePoint = 9,
    InvalidOrEscapingPath = 10,
    MissingOwnershipMetadata = 11,
    MalformedOwnershipMetadata = 12,
    UnsupportedOwnershipMetadataVersion = 13,
    OwnershipPathMismatch = 14,
    InspectionFailed = 15,
    ArtifactKindNotEligibleInRoot = 16
}

public sealed record ManagedStorageOwnershipMetadata(
    int SchemaVersion,
    ManagedStorageArtifactId ArtifactId,
    ManagedStorageArtifactKind ArtifactKind,
    ManagedStorageLifecycle Lifecycle,
    string CanonicalArtifactPath,
    OperationId? OperationId,
    SourceId? SourceId,
    SourceSetId? SourceSetId,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ManagedStorageSourceDependency(
    SourceId SourceId,
    SourceSetId SourceSetId,
    SourceId? OriginalArchiveSourceId,
    string Path,
    string? ExtractionRoot,
    ArchiveExtractionRetention? ExtractionRetention);

public sealed record ManagedStorageRetainedDependency(
    ManagedStorageArtifactId? ArtifactId,
    string? CanonicalPath,
    string Description);

public sealed record ManagedStorageProtectedLocation(
    string Path,
    ManagedStorageArtifactKind Kind,
    bool IncludeDescendants = true);

public sealed record ManagedStorageDependencySnapshot(
    IReadOnlyList<OperationId> ActiveOperationIds,
    IReadOnlyList<ManagedStorageSourceDependency> ActiveSources,
    IReadOnlyList<ManagedStorageRetainedDependency> RetainedResultDependencies,
    IReadOnlyList<ManagedStorageProtectedLocation> KnownProtectedLocations)
{
    public static ManagedStorageDependencySnapshot Empty { get; } = new([], [], [], []);
}

public interface IManagedStorageDependencySnapshotProvider
{
    ManagedStorageDependencySnapshot CreateSnapshot();
}

public sealed record ManagedStorageArtifactClassification(
    ManagedStorageArtifactId? ArtifactId,
    string CanonicalPath,
    ManagedStorageArtifactKind ArtifactKind,
    ManagedStorageLifecycle Lifecycle,
    OperationId? OperationId,
    SourceId? SourceId,
    SourceSetId? SourceSetId,
    DateTimeOffset? CreatedAtUtc,
    bool IsCleanupEligible,
    IReadOnlyList<ManagedStorageProtectionReason> ProtectionReasons,
    string? InspectionProblem)
{
    public bool IsProtected => !IsCleanupEligible;
}

public sealed record ManagedStorageInventory(
    IReadOnlyList<ManagedStorageArtifactClassification> Items)
{
    public IReadOnlyList<ManagedStorageArtifactClassification> CleanupCandidates =>
        Items.Where(item => item.IsCleanupEligible).ToArray();

    public IReadOnlyList<ManagedStorageArtifactClassification> ProtectedItems =>
        Items.Where(item => item.IsProtected).ToArray();
}
