using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceIntake;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class SourceRefreshServiceTests
{
    [TestMethod]
    public async Task RefreshReentersIntakeAndInterpretationWhileRetainingSourceIdentity()
    {
        using var directory = new TemporarySourceDirectory();
        var path = directory.WriteFile("catalog.xml", "<catalog><item>  Exact value  </item></catalog>");
        var source = CreateSource(path);
        var service = CreateService(directory.Path, new CatalogAdapter());

        var result = await service.RefreshAsync(source);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(source.SourceId, result.Source.SourceId);
        Assert.AreEqual(source.Path, result.Source.Path);
        Assert.IsFalse(result.Source.IsIncluded);
        Assert.AreEqual(LoadedSourceStatus.Ready, result.Source.Status);
        Assert.IsNull(result.Failure);
    }

    [TestMethod]
    public async Task RefreshReturnsControlledValidationFailureForMalformedXml()
    {
        using var directory = new TemporarySourceDirectory();
        var path = directory.WriteFile("catalog.xml", "<catalog><item></catalog>");
        var source = CreateSource(path);
        var service = CreateService(directory.Path, new CatalogAdapter());

        var result = await service.RefreshAsync(source);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(source.SourceId, result.Source.SourceId);
        Assert.AreEqual(LoadedSourceStatus.FailedValidation, result.Source.Status);
        Assert.AreEqual("malformed-xml", result.Failure?.Code);
    }

    [TestMethod]
    public async Task RefreshReturnsControlledUnavailableStatusWhenOriginalPathIsGone()
    {
        using var directory = new TemporarySourceDirectory();
        var source = CreateSource(Path.GetFullPath("missing.xml"));
        var service = CreateService(directory.Path, new CatalogAdapter());

        var result = await service.RefreshAsync(source);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(source.SourceId, result.Source.SourceId);
        Assert.AreEqual(LoadedSourceStatus.Unavailable, result.Source.Status);
        Assert.AreEqual("source-not-found", result.Failure?.Code);
    }

    [TestMethod]
    public async Task ArchiveDerivedRefreshReextractsOriginalArchiveAndRetainsSourceIdentity()
    {
        using var directory = new TemporarySourceDirectory();
        var archivePath = directory.CreateZip(
            "catalog.zip",
            "folder/catalog.xml",
            "<catalog><item>Archive value</item></catalog>");
        var applicationPaths = ApplicationPaths.FromLocalApplicationData(
            Path.Combine(directory.Path, "LocalAppData"));
        var intake = new SourceIntakeService(new ArchiveExtractionService(applicationPaths));
        var load = await intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);
        var source = load.Sources.Single() with { IsIncluded = false };
        var originalWorkingPath = source.Path;
        var service = new SourceRefreshService(
            intake,
            new SourceInterpreter([new CatalogAdapter()], NullLogger<SourceInterpreter>.Instance));

        var result = await service.RefreshAsync(source);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(source.SourceId, result.Source.SourceId);
        Assert.IsFalse(result.Source.IsIncluded);
        Assert.AreNotEqual(originalWorkingPath, result.Source.Path);
        Assert.AreEqual(
            source.ArchiveProvenance?.OriginalArchiveSourceId,
            result.Source.ArchiveProvenance?.OriginalArchiveSourceId);
        Assert.AreEqual(
            source.ArchiveProvenance?.ArchiveMemberPath,
            result.Source.ArchiveProvenance?.ArchiveMemberPath);
        Assert.IsTrue(File.Exists(result.Source.Path));
    }

    private static SourceRefreshService CreateService(
        string testRoot,
        params ISourceAdapter[] adapters)
    {
        var applicationPaths = ApplicationPaths.FromLocalApplicationData(
            Path.Combine(testRoot, "LocalAppData"));
        return new SourceRefreshService(
            new SourceIntakeService(new ArchiveExtractionService(applicationPaths)),
            new SourceInterpreter(adapters, NullLogger<SourceInterpreter>.Instance));
    }

    private static LoadedSourceContract CreateSource(string path)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            path,
            IsIncluded: false,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
    }

    private sealed class CatalogAdapter : ISourceAdapter
    {
        public SourceStructureDeclaration Declaration { get; } = new(
            "test.catalog.v1",
            XName.Get("catalog"));

        public async ValueTask<InterpretedSourceDocument> InterpretAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            CancellationToken cancellationToken = default)
        {
            var root = await XElement.LoadAsync(
                reader,
                LoadOptions.PreserveWhitespace,
                cancellationToken);
            return new InterpretedSourceDocument(
                originatingSourceId,
                Declaration.StructureId,
                [new InterpretedSourceValue("Item", root.Element("item")?.Value ?? string.Empty)]);
        }
    }

    private sealed class TemporarySourceDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR62.Tests");

        public TemporarySourceDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreateZip(string fileName, string entryPath, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            using var stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
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
                throw new InvalidOperationException(
                    "Refusing to delete a source-refresh test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
