using CIA.Core.Diagnostics;
using CIA.Core.Runtime;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ApplicationPathsTests
{
    [TestMethod]
    public void CurrentUserPathsMatchApprovedPerUserLayout()
    {
        var localApplicationDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var paths = ApplicationPaths.ForCurrentUser();

        Assert.AreEqual(
            Path.Combine(
                localApplicationDataDirectory,
                "Programs",
                ApplicationPaths.ApplicationDirectoryName),
            paths.InstalledBinaryDirectory);
        Assert.AreEqual(
            Path.Combine(
                localApplicationDataDirectory,
                ApplicationPaths.ApplicationDirectoryName),
            paths.ApplicationDataDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Settings"),
            paths.SettingsDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Profiles"),
            paths.ProfilesDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Working"),
            paths.WorkingDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Database"),
            paths.DatabaseDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Logs"),
            paths.LogsDirectory);
        Assert.AreEqual(
            Path.Combine(paths.ApplicationDataDirectory, "Temp"),
            paths.TempDirectory);
    }

    [TestMethod]
    public void WritableDirectoriesRemainOutsideInstalledBinaryDirectory()
    {
        using var root = new TemporaryDirectory();
        var paths = ApplicationPaths.FromLocalApplicationData(root.Path);

        Assert.HasCount(6, paths.WritableDirectories);
        Assert.IsTrue(paths.WritableDirectories.All(
            directory => !paths.IsInstalledBinaryPath(directory)));
    }

    [TestMethod]
    public void WritableDirectoriesCanBeCreatedWithoutCreatingInstallationDirectory()
    {
        using var root = new TemporaryDirectory();
        var paths = ApplicationPaths.FromLocalApplicationData(root.Path);

        paths.EnsureWritableDirectoriesExist();

        Assert.IsTrue(paths.WritableDirectories.All(Directory.Exists));
        Assert.IsFalse(Directory.Exists(paths.InstalledBinaryDirectory));
    }

    [TestMethod]
    public void ReplacingInstalledBinariesDoesNotRemoveWritableApplicationData()
    {
        using var root = new TemporaryDirectory();
        var paths = ApplicationPaths.FromLocalApplicationData(root.Path);
        paths.EnsureWritableDirectoriesExist();
        Directory.CreateDirectory(paths.InstalledBinaryDirectory);
        var installedFile = Path.Combine(paths.InstalledBinaryDirectory, "CIA.exe");
        var settingsFile = Path.Combine(paths.SettingsDirectory, "settings.json");
        File.WriteAllText(installedFile, "installed binary placeholder");
        File.WriteAllText(settingsFile, "writable application data");

        Directory.Delete(paths.InstalledBinaryDirectory, recursive: true);
        Directory.CreateDirectory(paths.InstalledBinaryDirectory);

        Assert.IsFalse(File.Exists(installedFile));
        Assert.IsTrue(File.Exists(settingsFile));
    }

    [TestMethod]
    public void DefaultLogDirectoryUsesSharedWritablePathModel()
    {
        Assert.AreEqual(
            ApplicationPaths.ForCurrentUser().LogsDirectory,
            ApplicationLogPaths.ResolveDirectory(configuredDirectory: null));
    }

    [TestMethod]
    public void ConfiguredLogDirectoryCannotUsePerUserInstallationDirectory()
    {
        var installedBinaryDirectory = ApplicationPaths.ForCurrentUser().InstalledBinaryDirectory;

        Assert.ThrowsExactly<InvalidOperationException>(
            () => ApplicationLogPaths.ResolveDirectory(installedBinaryDirectory));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ApplicationLogPaths.ResolveDirectory(
                Path.Combine(installedBinaryDirectory, "Logs")));
    }

    [TestMethod]
    public void LocalApplicationDataRootMustBeAbsolute()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ApplicationPaths.FromLocalApplicationData("relative-path"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _testRoot;

        public TemporaryDirectory()
        {
            _testRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CIA.SPR109.Tests");
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_testRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
