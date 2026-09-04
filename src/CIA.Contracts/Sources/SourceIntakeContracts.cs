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

public sealed record SourceLoadSettings(
    bool IncludeXmlFiles,
    bool IncludeArchiveFiles,
    bool TraverseSubfolders)
{
    public ArchiveNestingDepth MaximumArchiveNestingDepth { get; init; } =
        ArchiveNestingDepth.Default;

    public static SourceLoadSettings Default { get; } = new(
        IncludeXmlFiles: true,
        IncludeArchiveFiles: true,
        TraverseSubfolders: true);
}

public sealed record LoadedSourceContract(
    SourceId SourceId,
    string Path,
    bool IsIncluded,
    LoadedSourceStatus Status,
    LoadedSourceKind Kind);
