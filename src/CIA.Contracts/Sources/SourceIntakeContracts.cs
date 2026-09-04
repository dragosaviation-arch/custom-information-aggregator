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
    Ready = 1
}

public sealed record SourceLoadSettings(
    bool IncludeXmlFiles,
    bool IncludeArchiveFiles,
    bool TraverseSubfolders)
{
    public static SourceLoadSettings Default { get; } = new(
        IncludeXmlFiles: true,
        IncludeArchiveFiles: true,
        TraverseSubfolders: true);
}

public sealed record LoadedSourceContract(
    string Path,
    bool IsIncluded,
    LoadedSourceStatus Status,
    LoadedSourceKind Kind);
