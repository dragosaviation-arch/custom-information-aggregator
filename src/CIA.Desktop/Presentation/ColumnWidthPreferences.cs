using CIA.Core.Runtime;

namespace CIA.Desktop.Presentation;

internal static class ColumnWidthPreferences
{
    public static double Resolve(
        ApplicationSettingsService? settingsService,
        string key,
        double defaultWidth,
        double minimumWidth,
        double maximumWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return settingsService?.Current.ColumnWidths.TryGetValue(key, out var savedWidth) == true
               && double.IsFinite(savedWidth)
               && savedWidth >= minimumWidth
               && savedWidth <= maximumWidth
            ? savedWidth
            : defaultWidth;
    }

    public static void Save(
        ApplicationSettingsService? settingsService,
        params (string Key, double Width)[] widths)
    {
        if (settingsService is null || widths.Length == 0)
        {
            return;
        }

        var savedWidths = new Dictionary<string, double>(
            settingsService.Current.ColumnWidths,
            StringComparer.Ordinal);
        foreach (var (key, width) in widths)
        {
            if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(width))
            {
                savedWidths[key] = width;
            }
        }

        settingsService.Save(settingsService.Current with { ColumnWidths = savedWidths });
    }

    public static double ApplyAdjacentDelta(
        double leftWidth,
        double rightWidth,
        double horizontalChange,
        double leftMinimum,
        double rightMinimum,
        double maximumWidth)
    {
        return Math.Clamp(
            horizontalChange,
            Math.Max(leftMinimum - leftWidth, rightWidth - maximumWidth),
            Math.Min(maximumWidth - leftWidth, rightWidth - rightMinimum));
    }
}
