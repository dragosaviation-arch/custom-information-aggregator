using Microsoft.Win32;

namespace CIA.Desktop.Sources;

public sealed class WindowsSourcePathPicker : ISourcePathPicker
{
    public string? PickXmlFile()
    {
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "XML files (*.xml)|*.xml",
            Multiselect = false,
            Title = "Add supported XML source"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Add source folder"
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickArchive()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "Archive files|*.*",
            Multiselect = false,
            Title = "Add supported archive"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
