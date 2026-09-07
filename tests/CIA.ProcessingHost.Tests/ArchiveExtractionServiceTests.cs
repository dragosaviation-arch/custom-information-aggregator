using System.IO.Compression;
using System.Text;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.ProcessingHost.SourceIntake;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ArchiveExtractionServiceTests
{
    [TestMethod]
    public async Task DirectArchiveProducesXmlWorkingSourcesUnderManagedTempWithoutChangingOriginal()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "direct.zip",
            TextEntry("folder/source.xml", "<catalog />"),
            TextEntry("folder/notes.txt", "not a supported source"));
        var originalBytes = await File.ReadAllBytesAsync(archivePath);
        var originalWriteTime = File.GetLastWriteTimeUtc(archivePath);

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.HasCount(0, result.Issues);
        var source = result.Sources[0];
        Assert.AreEqual(LoadedSourceKind.XmlFile, source.Kind);
        Assert.IsTrue(source.IsIncluded);
        Assert.AreEqual(LoadedSourceStatus.Ready, source.Status);
        Assert.IsTrue(IsWithin(source.Path, environment.Paths.TempDirectory));
        Assert.IsFalse(IsWithin(source.Path, environment.SourceDirectory));
        Assert.IsTrue(File.Exists(source.Path));
        Assert.HasCount(0, Directory.GetFiles(environment.SourceDirectory, "*.xml", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(archivePath));
        Assert.AreEqual(originalWriteTime, File.GetLastWriteTimeUtc(archivePath));

        var provenance = source.ArchiveProvenance;
        Assert.IsNotNull(provenance);
        Assert.AreEqual(Path.GetFullPath(archivePath), provenance.OriginalArchivePath);
        Assert.AreEqual(1, provenance.ArchiveNestingLevel);
        Assert.AreEqual("folder/source.xml", provenance.ArchiveMemberPath);
        Assert.AreEqual(ArchiveExtractionRetention.ManagedTemporary, provenance.Retention);
        Assert.AreEqual(ArchiveNestingDepth.Default, provenance.MaximumArchiveNestingDepth);
        Assert.IsNull(provenance.PersistentExtractionDirectory);
        Assert.HasCount(1, provenance.ArchiveLineage);
        Assert.AreEqual(provenance.OriginalArchiveSourceId, provenance.ArchiveLineage[0].ArchiveSourceId);
    }

    [TestMethod]
    public async Task DefaultDepthProcessesLevelsOneThroughThreeAndStopsBeforeLevelFour()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateNestedArchive("nested.zip");

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(3, result.Sources);
        CollectionAssert.AreEquivalent(
            new[] { 1, 2, 3 },
            result.Sources.Select(source => source.ArchiveProvenance!.ArchiveNestingLevel).ToArray());
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual("archive-depth-limit", result.Issues[0].Code);
        StringAssert.Contains(result.Issues[0].Description, "level 4");
        Assert.IsFalse(result.Sources.Any(source =>
            source.ArchiveProvenance!.ArchiveMemberPath.EndsWith("four.xml", StringComparison.Ordinal)));
        Assert.IsTrue(result.Sources.All(source =>
            source.SourceId != default
            && source.ArchiveProvenance!.ArchiveLineage.Count
            == source.ArchiveProvenance.ArchiveNestingLevel));
        Assert.AreEqual(
            1,
            result.Sources.Select(source => source.ArchiveProvenance!.OriginalArchiveSourceId)
                .Distinct()
                .Count());
    }

    [TestMethod]
    public async Task ConfiguredDepthOverrideProcessesADeeperSupportedLevel()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateNestedArchive("nested.zip");
        var settings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(4)
        };

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            settings);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(4, result.Sources);
        Assert.HasCount(0, result.Issues);
        Assert.IsTrue(result.Sources.Any(source =>
            source.ArchiveProvenance!.ArchiveNestingLevel == 4
            && source.ArchiveProvenance.ArchiveMemberPath.EndsWith("four.xml", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MultipleIndependentNestedArchivesAreBothTraversed()
    {
        using var environment = new ArchiveTestEnvironment();
        var first = ArchiveTestEnvironment.CreateArchiveBytes(
            TextEntry("first.xml", "<first />"));
        var second = ArchiveTestEnvironment.CreateArchiveBytes(
            TextEntry("second.xml", "<second />"));
        var archivePath = environment.CreateArchive(
            "siblings.zip",
            new ArchiveEntry("nested/first.zip", first),
            new ArchiveEntry("nested/second.zip", second));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(2, result.Sources);
        Assert.IsTrue(result.Sources.All(source =>
            source.ArchiveProvenance?.ArchiveNestingLevel == 2));
        CollectionAssert.AreEquivalent(
            new[] { "first.xml", "second.xml" },
            result.Sources.Select(source => source.ArchiveProvenance!.ArchiveMemberPath).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "nested/first.zip", "nested/second.zip" },
            result.Sources.Select(source => source.ArchiveProvenance!.ArchiveLineage[1].Path).ToArray());
    }

    [TestMethod]
    public async Task DeepConfiguredTraversalUsesBoundedIterativeWorklist()
    {
        using var environment = new ArchiveTestEnvironment();
        const int depth = 20;
        var archivePath = environment.CreateDeepArchive("deep.zip", depth);
        var settings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(depth)
        };

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            settings);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(depth, result.Sources[0].ArchiveProvenance?.ArchiveNestingLevel);
        Assert.HasCount(depth, result.Sources[0].ArchiveProvenance!.ArchiveLineage);
    }

    [TestMethod]
    public async Task UnsafeEntryIsRejectedWhileValidSiblingRemainsAvailable()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "unsafe.zip",
            TextEntry("../escape.xml", "<escape />"),
            TextEntry("C:/absolute.xml", "<absolute />"),
            TextEntry("safe/usable.xml", "<usable />"));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual("safe/usable.xml", result.Sources[0].ArchiveProvenance?.ArchiveMemberPath);
        Assert.HasCount(2, result.Issues);
        Assert.IsTrue(result.Issues.All(issue => issue.Code == "unsafe-archive-entry"));
        Assert.IsTrue(result.Issues.Any(issue => issue.EntryPath == "../escape.xml"));
        Assert.IsTrue(result.Issues.Any(issue => issue.EntryPath == "C:/absolute.xml"));
        Assert.IsTrue(result.Issues.All(
            issue => issue.ArchivePath == Path.GetFullPath(archivePath)));
        Assert.IsTrue(result.Issues.All(issue => issue.ArchiveNestingLevel == 1));
        Assert.AreEqual(LoadedSourceStatus.Ready, result.Sources[0].Status);
        Assert.IsFalse(File.Exists(Path.Combine(environment.TestRoot, "escape.xml")));
    }

    [TestMethod]
    public async Task ArchiveRejectsWhenEverySupportedMemberIsUnsafeAndRetainsContext()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "all-failed.zip",
            TextEntry("../first.xml", "<first />"),
            TextEntry("C:/second.xml", "<second />"));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("archive-no-usable-sources", result.Failure?.Code);
        Assert.IsEmpty(result.Sources);
        Assert.HasCount(2, result.Issues);
        Assert.IsTrue(result.Issues.All(issue => issue.Code == "unsafe-archive-entry"));
        Assert.IsTrue(result.Issues.All(
            issue => issue.ArchivePath == Path.GetFullPath(archivePath)));
        Assert.IsTrue(result.Issues.All(issue => issue.ArchiveNestingLevel == 1));
        CollectionAssert.AreEquivalent(
            new[] { "../first.xml", "C:/second.xml" },
            result.Issues.Select(issue => issue.EntryPath).ToArray());
    }

    [TestMethod]
    public async Task DuplicateEntryDoesNotReplaceOrDeleteEarlierValidWorkingSource()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "duplicate.zip",
            TextEntry("source.xml", "<first />"),
            TextEntry("source.xml", "<second />"));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual("<first />", await File.ReadAllTextAsync(result.Sources[0].Path));
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual("duplicate-archive-entry", result.Issues[0].Code);
    }

    [TestMethod]
    public async Task CorruptNestedArchiveDoesNotDiscardValidSiblingXml()
    {
        using var environment = new ArchiveTestEnvironment();
        var validNestedArchive = ArchiveTestEnvironment.CreateArchiveBytes(
            TextEntry("nested.xml", "<nested />"));
        var corruptNestedArchive = (byte[])validNestedArchive.Clone();
        corruptNestedArchive[6] |= 0x01;
        var centralDirectoryOffset = Enumerable.Range(0, corruptNestedArchive.Length - 3)
            .First(index => corruptNestedArchive[index] == 0x50
                && corruptNestedArchive[index + 1] == 0x4b
                && corruptNestedArchive[index + 2] == 0x01
                && corruptNestedArchive[index + 3] == 0x02);
        Assert.IsGreaterThanOrEqualTo(0, centralDirectoryOffset);
        corruptNestedArchive[centralDirectoryOffset + 8] |= 0x01;
        var archivePath = environment.CreateArchive(
            "partial.zip",
            TextEntry("usable.xml", "<usable />"),
            new ArchiveEntry("broken.zip", corruptNestedArchive));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual("usable.xml", result.Sources[0].ArchiveProvenance?.ArchiveMemberPath);
        Assert.HasCount(1, result.Issues);
        Assert.IsTrue(result.Issues[0].Code is "nested-archive-unreadable" or "archive-entry-failed");
        Assert.AreEqual(Path.GetFullPath(archivePath), result.Issues[0].ArchivePath);
        Assert.IsGreaterThanOrEqualTo(1, result.Issues[0].ArchiveNestingLevel);
        Assert.AreEqual("broken.zip", result.Issues[0].EntryPath);
    }

    [TestMethod]
    public async Task EmptyArchiveReturnsControlledFailureWithoutAnArchiveContainerSource()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "empty.zip",
            TextEntry("notes.txt", "no XML here"));

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            SourceLoadSettings.Default);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("archive-no-usable-sources", result.Failure?.Code);
        Assert.HasCount(0, result.Sources);
    }

    [TestMethod]
    public async Task PersistentModeRequiresAndUsesExplicitNonTemporaryDestination()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "persistent.zip",
            TextEntry("retained.xml", "<retained />"));
        var settings = SourceLoadSettings.Default with
        {
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = environment.PersistentDirectory
        };

        var result = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            settings);

        Assert.IsTrue(result.Accepted);
        var source = result.Sources.Single();
        var provenance = source.ArchiveProvenance;
        Assert.IsNotNull(provenance);
        Assert.IsTrue(IsWithin(source.Path, environment.PersistentDirectory));
        Assert.IsFalse(IsWithin(source.Path, environment.Paths.TempDirectory));
        Assert.AreEqual(ArchiveExtractionRetention.Persistent, provenance.Retention);
        Assert.AreEqual(Path.GetFullPath(environment.PersistentDirectory), provenance.PersistentExtractionDirectory);

        var invalid = await environment.Intake.LoadAsync(
            SourceSelectionKind.Archive,
            archivePath,
            settings with { PersistentArchiveExtractionDirectory = null });
        Assert.IsFalse(invalid.Accepted);
        Assert.AreEqual("invalid-load-settings", invalid.Failure?.Code);
    }

    [TestMethod]
    public async Task PreCancelledArchiveTraversalStopsCooperatively()
    {
        using var environment = new ArchiveTestEnvironment();
        var archivePath = environment.CreateArchive(
            "cancel.zip",
            TextEntry("source.xml", "<source />"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => environment.Intake.LoadAsync(
                SourceSelectionKind.Archive,
                archivePath,
                SourceLoadSettings.Default,
                cancellation.Token));
    }

    private static ArchiveEntry TextEntry(string path, string content)
    {
        return new ArchiveEntry(path, Encoding.UTF8.GetBytes(content));
    }

    private static bool IsWithin(string path, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ArchiveTestEnvironment : IDisposable
    {
        private readonly string _safeRoot;

        public ArchiveTestEnvironment()
        {
            _safeRoot = Path.Combine(Path.GetTempPath(), "CIA.SPR64.Tests");
            TestRoot = Path.Combine(_safeRoot, Guid.NewGuid().ToString("N"));
            SourceDirectory = Path.Combine(TestRoot, "Sources");
            PersistentDirectory = Path.Combine(TestRoot, "PersistentExtraction");
            Directory.CreateDirectory(SourceDirectory);
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(TestRoot, "LocalAppData"));
            Intake = new SourceIntakeService(new ArchiveExtractionService(Paths));
        }

        public string TestRoot { get; }

        public string SourceDirectory { get; }

        public string PersistentDirectory { get; }

        public ApplicationPaths Paths { get; }

        public SourceIntakeService Intake { get; }

        public string CreateArchive(string fileName, params ArchiveEntry[] entries)
        {
            var path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, CreateArchiveBytes(entries));
            return path;
        }

        public string CreateNestedArchive(string fileName)
        {
            var fourth = CreateArchiveBytes(TextEntry("four.xml", "<four />"));
            var third = CreateArchiveBytes(
                TextEntry("three.xml", "<three />"),
                new ArchiveEntry("deeper/level4.zip", fourth));
            var second = CreateArchiveBytes(
                TextEntry("two.xml", "<two />"),
                new ArchiveEntry("deeper/level3.zip", third));
            return CreateArchive(
                fileName,
                TextEntry("one.xml", "<one />"),
                new ArchiveEntry("deeper/level2.zip", second));
        }

        public string CreateDeepArchive(string fileName, int depth)
        {
            var content = CreateArchiveBytes(TextEntry("deep.xml", "<deep />"));

            for (var level = depth - 1; level >= 1; level--)
            {
                content = CreateArchiveBytes(
                    new ArchiveEntry($"nested/level-{level + 1}.zip", content));
            }

            var path = Path.Combine(SourceDirectory, fileName);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(TestRoot))
            {
                return;
            }

            var safePrefix = Path.GetFullPath(_safeRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(TestRoot);

            if (!target.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete an archive-test directory outside its root.");
            }

            Directory.Delete(target, recursive: true);
        }

        internal static byte[] CreateArchiveBytes(params ArchiveEntry[] entries)
        {
            using var stream = new MemoryStream();

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in entries)
                {
                    var entry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    entryStream.Write(item.Content);
                }
            }

            return stream.ToArray();
        }
    }

    internal sealed record ArchiveEntry(string Path, byte[] Content);
}
