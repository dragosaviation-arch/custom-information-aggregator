using System.IO.Compression;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.ProcessingHost.SourceIntake;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class SourceIntakeServiceTests
{
    [TestMethod]
    public async Task IndividualReadableXmlFileLoadsAsIncludedReadySource()
    {
        using var files = new TemporarySourceDirectory();
        var xmlPath = files.WriteFile("source.xml", "<root />");
        var service = files.CreateService();

        var result = await service.LoadAsync(
            SourceSelectionKind.XmlFile,
            xmlPath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(Path.GetFullPath(xmlPath), result.Sources[0].Path);
        Assert.AreNotEqual(default, result.Sources[0].SourceId);
        Assert.IsTrue(result.Sources[0].IsIncluded);
        Assert.AreEqual(LoadedSourceStatus.Ready, result.Sources[0].Status);
        Assert.AreEqual(LoadedSourceKind.XmlFile, result.Sources[0].Kind);
    }

    [TestMethod]
    public async Task FolderLoadsSupportedXmlAndArchivesRecursivelyUsingDefaults()
    {
        using var files = new TemporarySourceDirectory();
        var xmlPath = files.WriteFile("source.xml", "<root />");
        var nestedDirectory = files.CreateDirectory("nested");
        var nestedXmlPath = files.WriteFile(Path.Combine("nested", "second.XML"), "<root />");
        var archivePath = files.CreateZip(Path.Combine("nested", "sources.package"));
        files.WriteFile("notes.txt", "not a supported source");
        var service = files.CreateService();

        var result = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(3, result.Sources);
        Assert.IsTrue(result.Sources.Any(source => source.Path == Path.GetFullPath(xmlPath)));
        Assert.IsTrue(result.Sources.Any(source => source.Path == Path.GetFullPath(nestedXmlPath)));
        Assert.IsTrue(result.Sources.Any(source =>
            source.ArchiveProvenance?.OriginalArchivePath == Path.GetFullPath(archivePath)));
        Assert.IsTrue(result.Sources.All(source => source.IsIncluded));
        Assert.IsTrue(result.Sources.All(source => source.Kind == LoadedSourceKind.XmlFile));
        Assert.AreEqual(
            result.Sources.Count,
            result.Sources.Select(source => source.SourceId).Distinct().Count());
        Assert.IsFalse(result.Sources.Any(source => source.Path.EndsWith("notes.txt", StringComparison.Ordinal)));
        Assert.IsTrue(Directory.Exists(nestedDirectory));
    }

    [TestMethod]
    public async Task FolderIncludesOnlyTheEnabledSupportedSourceKinds()
    {
        using var files = new TemporarySourceDirectory();
        var xmlPath = files.WriteFile("source.xml", "<root />");
        var archivePath = files.CreateZip("sources.zip");
        var service = files.CreateService();

        var xmlOnly = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            new SourceLoadSettings(true, false, false));
        var archivesOnly = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            new SourceLoadSettings(false, true, false));

        Assert.IsTrue(xmlOnly.Accepted);
        Assert.HasCount(1, xmlOnly.Sources);
        Assert.AreEqual(Path.GetFullPath(xmlPath), xmlOnly.Sources[0].Path);
        Assert.AreEqual(LoadedSourceKind.XmlFile, xmlOnly.Sources[0].Kind);
        Assert.IsTrue(archivesOnly.Accepted);
        Assert.HasCount(1, archivesOnly.Sources);
        Assert.AreEqual(
            Path.GetFullPath(archivePath),
            archivesOnly.Sources[0].ArchiveProvenance?.OriginalArchivePath);
        Assert.AreEqual(LoadedSourceKind.XmlFile, archivesOnly.Sources[0].Kind);
    }

    [TestMethod]
    public async Task FolderTraversalFollowsTheActiveRecursiveSettingExactly()
    {
        using var files = new TemporarySourceDirectory();
        var topLevelPath = files.WriteFile("top.xml", "<root />");
        var nestedPath = files.WriteFile(Path.Combine("nested", "child.xml"), "<root />");
        var service = files.CreateService();

        var topLevelOnly = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            new SourceLoadSettings(true, false, false));
        var recursive = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            new SourceLoadSettings(true, false, true));

        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(topLevelPath) },
            topLevelOnly.Sources.Select(source => source.Path).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { topLevelPath, nestedPath }.Select(Path.GetFullPath).ToArray(),
            recursive.Sources.Select(source => source.Path).ToArray());
    }

    [TestMethod]
    public async Task DirectFileAndArchiveSelectionsIgnoreFolderInclusionSwitches()
    {
        using var files = new TemporarySourceDirectory();
        var xmlPath = files.WriteFile("source.xml", "<root />");
        var archivePath = files.CreateZip("sources.zip");
        var folderSwitchesDisabled = new SourceLoadSettings(false, false, false);
        var service = files.CreateService();

        var xml = await service.LoadAsync(
            SourceSelectionKind.XmlFile,
            xmlPath,
            folderSwitchesDisabled);
        var archive = await service.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            folderSwitchesDisabled);

        Assert.IsTrue(xml.Accepted);
        Assert.HasCount(1, xml.Sources);
        Assert.AreEqual(LoadedSourceKind.XmlFile, xml.Sources[0].Kind);
        Assert.IsTrue(archive.Accepted);
        Assert.HasCount(1, archive.Sources);
        Assert.AreEqual(LoadedSourceKind.XmlFile, archive.Sources[0].Kind);
        Assert.IsNotNull(archive.Sources[0].ArchiveProvenance);
    }

    [TestMethod]
    public async Task InvalidArchiveDepthIsRejectedAsAControlledLoadSettingsFailure()
    {
        using var files = new TemporarySourceDirectory();
        var archivePath = files.CreateZip("sources.zip");
        var invalidSettings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = default
        };
        var service = files.CreateService();

        var result = await service.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            invalidSettings);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("invalid-load-settings", result.Failure?.Code);
        Assert.HasCount(0, result.Sources);
    }

    [TestMethod]
    public async Task ArchiveSelectionUsesContentRecognitionAndReturnsExtractedXmlWorkingSource()
    {
        using var files = new TemporarySourceDirectory();
        var archivePath = files.CreateZip("sources.package");
        var service = files.CreateService();

        var result = await service.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(LoadedSourceKind.XmlFile, result.Sources[0].Kind);
        Assert.AreNotEqual(Path.GetFullPath(archivePath), result.Sources[0].Path);
        Assert.AreEqual(
            Path.GetFullPath(archivePath),
            result.Sources[0].ArchiveProvenance?.OriginalArchivePath);
        Assert.IsTrue(File.Exists(result.Sources[0].Path));
        Assert.HasCount(1, Directory.GetFiles(files.Path));
    }

    [TestMethod]
    public async Task UnsupportedAndMissingSelectionsAreRejectedWithControlledFailures()
    {
        using var files = new TemporarySourceDirectory();
        var unsupportedPath = files.WriteFile("source.txt", "not XML or an archive");
        var service = files.CreateService();

        var unsupported = await service.LoadAsync(
            SourceSelectionKind.Archive,
            unsupportedPath,
            SourceLoadSettings.Default);
        var missing = await service.LoadAsync(
            SourceSelectionKind.XmlFile,
            Path.Combine(files.Path, "missing.xml"),
            SourceLoadSettings.Default);

        Assert.IsFalse(unsupported.Accepted);
        Assert.AreEqual("unsupported-archive", unsupported.Failure?.Code);
        Assert.HasCount(0, unsupported.Sources);
        Assert.IsFalse(missing.Accepted);
        Assert.AreEqual("source-not-found", missing.Failure?.Code);
    }

    [TestMethod]
    public async Task UnreadableSelectedXmlIsRejectedWithoutReturningAnExceptionContract()
    {
        using var files = new TemporarySourceDirectory();
        var path = files.WriteFile("locked.xml", "<root />");
        var service = files.CreateService();
        await using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await service.LoadAsync(
            SourceSelectionKind.XmlFile,
            path,
            SourceLoadSettings.Default);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("source-unreadable", result.Failure?.Code);
        Assert.HasCount(0, result.Sources);
    }

    private sealed class TemporarySourceDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR61.Tests");
        private readonly string _testRoot;

        public TemporarySourceDirectory()
        {
            _testRoot = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(_testRoot, "Sources");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public SourceIntakeService CreateService()
        {
            var applicationPaths = ApplicationPaths.FromLocalApplicationData(
                System.IO.Path.Combine(_testRoot, "LocalAppData"));
            return new SourceIntakeService(new ArchiveExtractionService(applicationPaths));
        }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreateZip(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            using var stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("source.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<root />");
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(_testRoot);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a source-test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
