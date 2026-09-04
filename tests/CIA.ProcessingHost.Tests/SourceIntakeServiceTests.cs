using System.IO.Compression;
using CIA.Contracts.Sources;
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
        var service = new SourceIntakeService();

        var result = await service.LoadAsync(
            SourceSelectionKind.XmlFile,
            xmlPath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(Path.GetFullPath(xmlPath), result.Sources[0].Path);
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
        var service = new SourceIntakeService();

        var result = await service.LoadAsync(
            SourceSelectionKind.Folder,
            files.Path,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(3, result.Sources);
        CollectionAssert.AreEquivalent(
            new[] { xmlPath, nestedXmlPath, archivePath }.Select(Path.GetFullPath).ToArray(),
            result.Sources.Select(source => source.Path).ToArray());
        Assert.IsTrue(result.Sources.All(source => source.IsIncluded));
        Assert.IsFalse(result.Sources.Any(source => source.Path.EndsWith("notes.txt", StringComparison.Ordinal)));
        Assert.IsTrue(Directory.Exists(nestedDirectory));
    }

    [TestMethod]
    public async Task ArchiveSelectionUsesContentRecognitionWithoutExtractingTheArchive()
    {
        using var files = new TemporarySourceDirectory();
        var archivePath = files.CreateZip("sources.package");
        var service = new SourceIntakeService();

        var result = await service.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(LoadedSourceKind.Archive, result.Sources[0].Kind);
        Assert.AreEqual(Path.GetFullPath(archivePath), result.Sources[0].Path);
        Assert.HasCount(1, Directory.GetFiles(files.Path));
    }

    [TestMethod]
    public async Task UnsupportedAndMissingSelectionsAreRejectedWithControlledFailures()
    {
        using var files = new TemporarySourceDirectory();
        var unsupportedPath = files.WriteFile("source.txt", "not XML or an archive");
        var service = new SourceIntakeService();

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
        var service = new SourceIntakeService();
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

        public TemporarySourceDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
            var resolvedTarget = System.IO.Path.GetFullPath(Path);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a source-test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
