namespace CIA.Desktop.Sources;

public interface ISourcePathPicker
{
    string? PickXmlFile();

    string? PickFolder();

    string? PickArchive();
}
