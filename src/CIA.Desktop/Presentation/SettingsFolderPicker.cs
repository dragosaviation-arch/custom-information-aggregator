using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CIA.Desktop.Presentation;

public interface ISettingsFolderPicker
{
    string? Browse(string title, string? currentDirectory);
}

public sealed class WindowsSettingsFolderPicker : ISettingsFolderPicker
{
    public string? Browse(string title, string? currentDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FolderName
            : null;
    }
}
