using System.IO;
using System.Windows;
using CIA.Contracts.Export;
using CIA.Desktop.Views;
using Microsoft.Win32;

namespace CIA.Desktop.Export;

public interface IExportFolderPicker
{
    string? Browse(string? currentFolder);
}

public sealed class WindowsExportFolderPicker : IExportFolderPicker
{
    public string? Browse(string? currentFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Excel export folder",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
        {
            dialog.InitialDirectory = currentFolder;
        }

        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FolderName
            : null;
    }
}

public enum WorkbookCollisionAction
{
    Overwrite = 1,
    DifferentName = 2,
    Cancel = 3
}

public sealed record WorkbookCollisionTarget(
    WorkbookDefinitionId WorkbookDefinitionId,
    string FileName,
    string FinalPath,
    bool Exists);

public sealed record WorkbookCollisionResolutionRequest(
    string OutputDirectory,
    IReadOnlyList<WorkbookCollisionTarget> Targets,
    IReadOnlyList<string> ReservedFileNames);

public sealed record WorkbookCollisionDecision(
    WorkbookDefinitionId WorkbookDefinitionId,
    WorkbookCollisionAction Action,
    string? DifferentFileName = null);

public sealed record WorkbookCollisionResolution(
    IReadOnlyList<WorkbookCollisionDecision> Decisions);

public interface IWorkbookCollisionResolver
{
    WorkbookCollisionResolution? Resolve(WorkbookCollisionResolutionRequest request);
}

public sealed class WindowsWorkbookCollisionResolver : IWorkbookCollisionResolver
{
    public WorkbookCollisionResolution? Resolve(WorkbookCollisionResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new WorkbookCollisionDialog(request)
        {
            Owner = Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Resolution : null;
    }
}
