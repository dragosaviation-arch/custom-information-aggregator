using System.Collections.ObjectModel;

namespace CIA.Core.Runtime;

public sealed class ApplicationPaths
{
    public const string ApplicationDirectoryName = "Custom Information Aggregator";

    private ApplicationPaths(string localApplicationDataDirectory)
    {
        LocalApplicationDataDirectory = localApplicationDataDirectory;
        InstalledBinaryDirectory = Path.Combine(
            LocalApplicationDataDirectory,
            "Programs",
            ApplicationDirectoryName);
        ApplicationDataDirectory = Path.Combine(
            LocalApplicationDataDirectory,
            ApplicationDirectoryName);
        SettingsDirectory = Path.Combine(ApplicationDataDirectory, "Settings");
        ProfilesDirectory = Path.Combine(ApplicationDataDirectory, "Profiles");
        WorkingDirectory = Path.Combine(ApplicationDataDirectory, "Working");
        DatabaseDirectory = Path.Combine(ApplicationDataDirectory, "Database");
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "Logs");
        TempDirectory = Path.Combine(ApplicationDataDirectory, "Temp");
        WritableDirectories = new ReadOnlyCollection<string>(
            [
                SettingsDirectory,
                ProfilesDirectory,
                WorkingDirectory,
                DatabaseDirectory,
                LogsDirectory,
                TempDirectory
            ]);
    }

    public string LocalApplicationDataDirectory { get; }

    public string InstalledBinaryDirectory { get; }

    public string ApplicationDataDirectory { get; }

    public string SettingsDirectory { get; }

    public string ProfilesDirectory { get; }

    public string WorkingDirectory { get; }

    public string DatabaseDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public IReadOnlyList<string> WritableDirectories { get; }

    public static ApplicationPaths ForCurrentUser()
    {
        var localApplicationDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
        {
            throw new InvalidOperationException("The LocalAppData directory could not be resolved.");
        }

        return FromLocalApplicationData(localApplicationDataDirectory);
    }

    public static ApplicationPaths FromLocalApplicationData(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);

        if (!Path.IsPathFullyQualified(localApplicationDataDirectory))
        {
            throw new ArgumentException(
                "The LocalAppData directory must be an absolute path.",
                nameof(localApplicationDataDirectory));
        }

        return new ApplicationPaths(Path.GetFullPath(localApplicationDataDirectory));
    }

    public void EnsureWritableDirectoriesExist()
    {
        foreach (var directory in WritableDirectories)
        {
            Directory.CreateDirectory(directory);
        }
    }

    public bool IsInstalledBinaryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return IsWithinDirectory(path, InstalledBinaryDirectory);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var resolvedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedDirectoryPrefix = resolvedDirectory + Path.DirectorySeparatorChar;
        var resolvedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return resolvedPath.Equals(resolvedDirectory, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(resolvedDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
