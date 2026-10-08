using Microsoft.Win32;

namespace CIA.Desktop.Sources;

public sealed class WindowsSourcePathPicker : ISourcePathPicker
{
    public string? PickXmlFile() => PickXmlFiles(multiselect: false).FirstOrDefault();

    public IReadOnlyList<string> PickXmlFiles() => PickXmlFiles(multiselect: true);

    public string? PickFolder() => PickFolders(multiselect: false).FirstOrDefault();

    public IReadOnlyList<string> PickFolders() => PickFolders(multiselect: true);

    public string? PickArchive() => PickArchives(multiselect: false).FirstOrDefault();

    public IReadOnlyList<string> PickArchives() => PickArchives(multiselect: true);

    private static IReadOnlyList<string> PickXmlFiles(bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "XML files (*.xml)|*.xml",
            Multiselect = multiselect,
            Title = "Add supported XML source"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    private static IReadOnlyList<string> PickFolders(bool multiselect)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = multiselect,
            Title = "Add source folder"
        };

        return dialog.ShowDialog() == true ? dialog.FolderNames : [];
    }

    private static IReadOnlyList<string> PickArchives(bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "Archive files|*.*",
            Multiselect = multiselect,
            Title = "Add supported archive"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
