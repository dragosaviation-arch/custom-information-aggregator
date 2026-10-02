namespace CIA.Desktop.Sources;

public interface ISourcePathPicker
{
    string? PickXmlFile();

    IReadOnlyList<string> PickXmlFiles()
    {
        var path = PickXmlFile();
        return path is null ? [] : [path];
    }

    string? PickFolder();

    IReadOnlyList<string> PickFolders()
    {
        var path = PickFolder();
        return path is null ? [] : [path];
    }

    string? PickArchive();

    IReadOnlyList<string> PickArchives()
    {
        var path = PickArchive();
        return path is null ? [] : [path];
    }
}
