using System.Diagnostics;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Core.Runtime;
using CIA.Desktop.Views;

namespace CIA.Desktop.Export;

public enum PostExportChoice
{
    StatusOnly = 1,
    OpenExportedFiles = 2,
    OpenContainingFolder = 3
}

public interface IPostExportLauncher
{
    void Open(string path);
}

public interface IPostExportPrompt
{
    PostExportChoice Choose(WorkbookExportBatchSummary batch);
}

public sealed record PostExportActionResult(string? FailureDescription)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(FailureDescription);

    public static PostExportActionResult Success() => new(FailureDescription: null);

    public static PostExportActionResult Failure(string description) => new(description);
}

public sealed class PostExportBehaviorCoordinator(
    ApplicationSettingsService settingsService,
    IPostExportPrompt prompt,
    IPostExportLauncher launcher)
{
    public PostExportActionResult Apply(
        OperationId completedOperationId,
        WorkbookExportBatchSummary batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (completedOperationId != batch.OperationId)
        {
            return PostExportActionResult.Success();
        }

        var behavior = settingsService.Current.PostExportBehavior;
        string? promptFailure = null;
        var choice = behavior switch
        {
            PostExportBehavior.StatusOnly => PostExportChoice.StatusOnly,
            PostExportBehavior.OpenExportedFile => PostExportChoice.OpenExportedFiles,
            PostExportBehavior.OpenContainingFolder => PostExportChoice.OpenContainingFolder,
            PostExportBehavior.AskEachTime => GetPromptChoice(batch, out promptFailure),
            _ => PostExportChoice.StatusOnly
        };

        if (behavior == PostExportBehavior.AskEachTime
            && promptFailure is not null)
        {
            return PostExportActionResult.Failure(promptFailure);
        }

        return choice switch
        {
            PostExportChoice.OpenExportedFiles => OpenExportedFiles(batch),
            PostExportChoice.OpenContainingFolder => OpenContainingFolder(batch),
            _ => PostExportActionResult.Success()
        };
    }

    private PostExportChoice GetPromptChoice(
        WorkbookExportBatchSummary batch,
        out string? failureDescription)
    {
        try
        {
            failureDescription = null;
            return prompt.Choose(batch);
        }
        catch (Exception exception)
        {
            failureDescription =
                $"Export completed, but the post-export choice could not be shown: {exception.Message}";
            return PostExportChoice.StatusOnly;
        }
    }

    private PostExportActionResult OpenContainingFolder(WorkbookExportBatchSummary batch)
    {
        try
        {
            launcher.Open(batch.OutputDirectory);
            return PostExportActionResult.Success();
        }
        catch (Exception exception)
        {
            return PostExportActionResult.Failure(
                $"Export completed, but the containing folder could not be opened: {exception.Message}");
        }
    }

    private PostExportActionResult OpenExportedFiles(WorkbookExportBatchSummary batch)
    {
        var failures = new List<string>();
        foreach (var workbook in batch.Workbooks)
        {
            try
            {
                launcher.Open(workbook.FinalPath);
            }
            catch (Exception exception)
            {
                failures.Add($"{workbook.FinalPath} ({exception.Message})");
            }
        }

        if (failures.Count == 0)
        {
            return PostExportActionResult.Success();
        }

        var subject = failures.Count == 1
            ? "the workbook could not be opened"
            : "one or more workbooks could not be opened";
        return PostExportActionResult.Failure(
            $"Export completed, but {subject}: {string.Join("; ", failures)}");
    }
}

public sealed class WindowsPostExportLauncher : IPostExportLauncher
{
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}

public sealed class WindowsPostExportPrompt : IPostExportPrompt
{
    public PostExportChoice Choose(WorkbookExportBatchSummary batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var dialog = new PostExportPromptDialog(batch)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
