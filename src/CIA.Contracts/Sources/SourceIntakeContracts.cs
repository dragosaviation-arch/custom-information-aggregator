using System.Text.Json.Serialization;

namespace CIA.Contracts.Sources;

public enum SourceSelectionKind
{
    XmlFile = 1,
    Folder = 2,
    Archive = 3
}

public enum LoadedSourceKind
{
    XmlFile = 1,
    Archive = 2
}

public enum LoadedSourceStatus
{
    Ready = 1,
    Unavailable = 2,
    Unsupported = 3,
    FailedValidation = 4
}

public enum ArchiveExtractionRetention
{
    ManagedTemporary = 1,
    Persistent = 2
}

public sealed record SourceLoadSettings(
    bool IncludeXmlFiles,
    bool IncludeArchiveFiles,
    bool TraverseSubfolders)
{
    public ArchiveNestingDepth MaximumArchiveNestingDepth { get; init; } =
        ArchiveNestingDepth.Default;

    public bool PersistentArchiveExtractionEnabled { get; init; }

    public string? PersistentArchiveExtractionDirectory { get; init; }

    public static SourceLoadSettings Default { get; } = new(
        IncludeXmlFiles: true,
        IncludeArchiveFiles: true,
        TraverseSubfolders: true);
}

[method: JsonConstructor]
public sealed record LoadedSourceContract(
    SourceId SourceId,
    SourceSetId SourceSetId,
    string Path,
    bool IsIncluded,
    LoadedSourceStatus Status,
    LoadedSourceKind Kind)
{
    public LoadedSourceContract(
        SourceId SourceId,
        string Path,
        bool IsIncluded,
        LoadedSourceStatus Status,
        LoadedSourceKind Kind)
        : this(SourceId, SourceSetId.CreateNew(), Path, IsIncluded, Status, Kind)
    {
    }

    public ArchiveSourceProvenance? ArchiveProvenance { get; init; }
}

public sealed record ArchiveLineageItem(
    SourceId ArchiveSourceId,
    string Path,
    int NestingLevel);

public sealed record ArchiveSourceProvenance(
    SourceId OriginalArchiveSourceId,
    string OriginalArchivePath,
    IReadOnlyList<ArchiveLineageItem> ArchiveLineage,
    int ArchiveNestingLevel,
    string ArchiveMemberPath,
    string ExtractionRoot,
    ArchiveExtractionRetention Retention,
    ArchiveNestingDepth MaximumArchiveNestingDepth,
    string? PersistentExtractionDirectory);

public sealed record SourceIntakeIssue(
    string Code,
    string Description,
    string ArchivePath,
    int ArchiveNestingLevel,
    string? EntryPath);

public sealed record SourceIntakeProgressSnapshot(
    string? CurrentArchivePath,
    int CurrentArchiveNestingLevel,
    int EncounteredItemCount,
    int LoadedSourceCount,
    int IssueCount,
    int FailureCount,
    int? TotalItemCount);
