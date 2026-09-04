using System.Windows;

namespace CIA.Desktop.Sources;

public interface ISourceRemovalConfirmation
{
    bool Confirm(int entryCount);
}

public sealed class WindowsSourceRemovalConfirmation : ISourceRemovalConfirmation
{
    public bool Confirm(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryCount);
        var message = entryCount == 1
            ? "Remove 1 entry?"
            : $"Remove {entryCount} entries?";

        return MessageBox.Show(
            message,
            "Remove loaded sources",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
