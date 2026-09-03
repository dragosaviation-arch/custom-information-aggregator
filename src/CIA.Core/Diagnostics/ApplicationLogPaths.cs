using CIA.Core.Runtime;

namespace CIA.Core.Diagnostics;

public static class ApplicationLogPaths
{
    public const string DirectoryConfigurationKey = "CIA:Logging:Directory";
    public const string UiFileNameTemplate = "cia-ui-.clef";
    public const string ProcessingHostFileNameTemplate = "cia-processing-host-.clef";

    public static string ResolveDirectory(string? configuredDirectory)
    {
        string resolvedDirectory;

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            if (!Path.IsPathFullyQualified(configuredDirectory))
            {
                throw new InvalidOperationException("The configured log directory must be an absolute path.");
            }

            resolvedDirectory = Path.GetFullPath(configuredDirectory);
        }
        else
        {
            resolvedDirectory = ApplicationPaths.ForCurrentUser().LogsDirectory;
        }

        EnsureSeparateFromInstalledBinaries(resolvedDirectory);
        return resolvedDirectory;
    }

    public static string GetUiFilePath(string logDirectory)
    {
        return GetFilePath(logDirectory, UiFileNameTemplate);
    }

    public static string GetProcessingHostFilePath(string logDirectory)
    {
        return GetFilePath(logDirectory, ProcessingHostFileNameTemplate);
    }

    private static string GetFilePath(string logDirectory, string fileNameTemplate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        return Path.Combine(logDirectory, fileNameTemplate);
    }

    private static void EnsureSeparateFromInstalledBinaries(string logDirectory)
    {
        if (ApplicationPaths.ForCurrentUser().IsInstalledBinaryPath(logDirectory))
        {
            throw new InvalidOperationException(
                "The log directory must remain separate from installed application binaries.");
        }

        var installedDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installedDirectoryPrefix = installedDirectory + Path.DirectorySeparatorChar;
        var resolvedLogDirectory = Path.GetFullPath(logDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (resolvedLogDirectory.Equals(installedDirectory, StringComparison.OrdinalIgnoreCase)
            || resolvedLogDirectory.StartsWith(
                installedDirectoryPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The log directory must remain separate from installed application binaries.");
        }
    }
}
