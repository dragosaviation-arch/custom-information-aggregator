using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using CIA.ProcessingHost.WorkingState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class WorkingStatePackageTests
{
    [TestMethod]
    public async Task HierarchyDatabaseRoundTripsWithoutSourceXmlAndWithoutExtractionPublication()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var firstDataset = state.Generation.Datasets[0];
        var firstPage = await workspace.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(
                state.Generation.OperationId,
                firstDataset.SourceSetId,
                1,
                100,
                null,
                DatabaseRowInclusionFilter.All));
        await workspace.Repository.SetPublishedDatabaseRowsIncludedAsync(
            new DatabaseRowInclusionChange(
                state.Generation.OperationId,
                firstDataset.SourceSetId,
                [firstPage!.Rows[0].Ordinal],
                false));
        await workspace.Repository.ExtractPublishedDatabaseAsync(
            OperationCorrelation.CreateNew(),
            state.Generation);

        var packagePath = Path.Combine(workspace.Root, "saved-state.cia");
        var saved = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            state.Snapshot);

        Assert.IsTrue(saved.Accepted, saved.Failure?.Description);
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    WorkingStatePackageFormat.ManifestEntryName,
                    WorkingStatePackageFormat.DatabaseEntryName
                },
                archive.Entries.Select(entry => entry.FullName).ToArray());
        }

        foreach (var source in state.Snapshot.Sources)
        {
            File.Delete(source.Path);
        }

        using var restored = workspace.CreateFreshRepositoryContext();
        var result = await restored.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            packagePath);
        var generation = await restored.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        var restoredPage = await restored.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(
                generation!.OperationId,
                firstDataset.SourceSetId,
                1,
                100,
                null,
                DatabaseRowInclusionFilter.All));
        var excludedPage = await restored.Repository.ReadPublishedDatabasePageAsync(
            new DatabaseReviewQuery(
                generation.OperationId,
                firstDataset.SourceSetId,
                1,
                100,
                null,
                DatabaseRowInclusionFilter.Excluded));
        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(state.Generation, generation));
        Assert.IsNotNull(restoredPage);
        Assert.IsGreaterThan(0, restoredPage.Rows.Count);
        Assert.AreEqual(1, excludedPage!.TotalRowCount);
        Assert.IsNull(await restored.Repository.ReadPublishedExtractionResultAsync());
        var extraction = await restored.Repository.ExtractPublishedDatabaseAsync(
            OperationCorrelation.CreateNew(),
            generation);
        Assert.AreEqual(generation.OperationId, extraction.DatabaseGeneration.OperationId);
    }

    [TestMethod]
    public async Task ManifestPreservesSetsSourcesDiscoveryOverridesAndLayouts()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "context.cia");

        var result = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            state.Snapshot);

        Assert.IsTrue(result.Accepted);
        var restored = result.Manifest!.Snapshot;
        Assert.AreEqual(WorkingStatePackageFormat.CurrentSchemaVersion, result.Manifest.PackageSchemaVersion);
        Assert.AreEqual(StructuredInformationRepository.CurrentSchemaVersion, result.Manifest.RepositorySchemaVersion);
        Assert.HasCount(2, restored.SourceSets);
        Assert.AreEqual("Set 1", restored.SourceSets[0].Name);
        Assert.AreEqual("Other set", restored.SourceSets[1].Name);
        Assert.AreEqual(restored.SourceSets[1].SourceSetId, restored.ActiveSourceSetId);
        Assert.IsTrue(restored.Sources.Select(source => source.SourceId)
            .SequenceEqual(state.Snapshot.Sources.Select(source => source.SourceId)));
        Assert.IsTrue(restored.Sources.All(source => source.IsIncluded));
        Assert.AreEqual(
            state.Snapshot.Sources[1].ArchiveProvenance,
            restored.Sources[1].ArchiveProvenance);
        Assert.HasCount(1, restored.DatabaseTagOverrides);
        Assert.AreEqual("Renamed", restored.DatabaseTagOverrides[0].DatabaseTagName);
        Assert.AreEqual(
            RepeatedDataLayout.StructuralRows,
            restored.DiscoveryConfiguration.SourceSets[0].RepeatedDataLayout);
        Assert.AreEqual(
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            restored.DiscoveryConfiguration.SourceSets[1].RepeatedDataLayout);
    }

    [TestMethod]
    public async Task CommittedWalDataIsIncludedInSelfContainedSnapshot()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "wal-state.cia");
        await using var walConnection = new SqliteConnection(
            $"Data Source={workspace.Repository.DatabasePath};Mode=ReadWrite;Pooling=False");
        await walConnection.OpenAsync();
        await using (var command = walConnection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO indexed_occurrences (tag, value, source_id)
                VALUES ('wal-evidence', 'committed', $sourceId);
                """;
            command.Parameters.AddWithValue("$sourceId", state.Snapshot.Sources[0].SourceId.ToString());
            await command.ExecuteNonQueryAsync();
        }

        Assert.IsTrue(File.Exists(workspace.Repository.DatabasePath + "-wal"));
        var result = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            state.Snapshot);
        await walConnection.CloseAsync();
        using var restored = workspace.CreateFreshRepositoryContext();
        var restore = await restored.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            packagePath);

        Assert.IsTrue(result.Accepted);
        Assert.IsTrue(restore.Accepted);
        var generation = await restored.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(state.Generation, generation));
        Assert.HasCount(1, await restored.Repository.QueryByTagAsync("wal-evidence"));
    }

    [TestMethod]
    public async Task CancelledReplacementPreservesPreviousPackageAndCleansCandidate()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "replace.cia");
        Assert.IsTrue((await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(), packagePath, state.Snapshot)).Accepted);
        var before = await File.ReadAllBytesAsync(packagePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            CopySnapshot(state.Snapshot, savedAtUtc: DateTimeOffset.UtcNow),
            cancellation.Token);

        Assert.IsFalse(result.Accepted);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(packagePath));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task CreateNewPublishesOnceRejectsExistingAndReplaceModeStillReplaces()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var createNewPath = Path.Combine(workspace.Root, "create-new.cia");

        var created = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            createNewPath,
            state.Snapshot,
            WorkingStatePublicationMode.CreateNew);
        Assert.IsTrue(created.Accepted, created.Failure?.Description);
        var createdBytes = await File.ReadAllBytesAsync(createNewPath);
        var rejected = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            createNewPath,
            CopySnapshot(state.Snapshot, savedAtUtc: DateTimeOffset.UtcNow),
            WorkingStatePublicationMode.CreateNew);

        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual("working-state-save-target-exists", rejected.Failure?.Code);
        CollectionAssert.AreEqual(createdBytes, await File.ReadAllBytesAsync(createNewPath));

        var replaced = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            createNewPath,
            CopySnapshot(state.Snapshot, savedAtUtc: DateTimeOffset.UtcNow.AddMinutes(1)),
            WorkingStatePublicationMode.ReplaceExisting);

        Assert.IsTrue(replaced.Accepted, replaced.Failure?.Description);
        Assert.AreNotEqual(created.Manifest?.Snapshot.SavedAtUtc, replaced.Manifest?.Snapshot.SavedAtUtc);
        var replacedBytes = await File.ReadAllBytesAsync(createNewPath);
        Assert.IsFalse(createdBytes.SequenceEqual(replacedBytes));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task LateCreateNewTargetWinsRaceAndRemainsByteIdentical()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "raced.cia");
        var unrelatedPath = Path.Combine(workspace.Root, "unrelated.cia");
        byte[] sentinel = [9, 7, 5, 3, 1];
        byte[] unrelated = [2, 4, 6, 8];
        await File.WriteAllBytesAsync(unrelatedPath, unrelated);
        var publisher = new GatedWorkingStatePackagePublisher();
        var service = workspace.CreatePackageService(publisher);

        var saveTask = service.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            state.Snapshot,
            WorkingStatePublicationMode.CreateNew);
        await publisher.PublicationReached;
        Assert.IsFalse(File.Exists(packagePath));
        await File.WriteAllBytesAsync(packagePath, sentinel);
        publisher.Release();
        var result = await saveTask;

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("working-state-save-target-exists", result.Failure?.Code);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(packagePath));
        CollectionAssert.AreEqual(unrelated, await File.ReadAllBytesAsync(unrelatedPath));
        Assert.IsEmpty(Directory.GetFiles(workspace.Root, "*.incomplete"));
    }

    [TestMethod]
    public async Task TamperedDatabaseAndFutureManifestFailWithoutReplacingRepository()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "tampered.cia");
        Assert.IsTrue((await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(), packagePath, state.Snapshot)).Accepted);

        var tamperedDatabase = Path.Combine(workspace.Root, "tampered-database.cia");
        File.Copy(packagePath, tamperedDatabase);
        ReplaceEntry(tamperedDatabase, WorkingStatePackageFormat.DatabaseEntryName, bytes =>
        {
            bytes[^1] ^= 0xff;
            return bytes;
        });
        var before = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        var digestResult = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(), tamperedDatabase);
        var afterDigestFailure = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        var futureManifest = Path.Combine(workspace.Root, "future.cia");
        File.Copy(packagePath, futureManifest);
        ReplaceEntry(futureManifest, WorkingStatePackageFormat.ManifestEntryName, bytes =>
        {
            var json = Encoding.UTF8.GetString(bytes).Replace(
                "\"packageSchemaVersion\": 1",
                "\"packageSchemaVersion\": 9",
                StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(json);
        });
        var futureResult = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(), futureManifest);
        var afterFutureFailure = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        Assert.IsFalse(digestResult.Accepted);
        Assert.IsFalse(futureResult.Accepted);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(before, afterDigestFailure));
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(before, afterFutureFailure));
    }

    [TestMethod]
    public async Task SnapshotSummaryMismatchIsRejected()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var wrong = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            state.Generation.Datasets);
        var path = Path.Combine(workspace.Root, "mismatch.cia");

        var result = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            path,
            CopySnapshot(state.Snapshot, databaseGeneration: wrong));

        Assert.IsFalse(result.Accepted);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task PublishedDatabaseCannotBeSavedAsDatabaseLessWorkingState()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var databaseLess = new WorkingStateSnapshot(
            state.Snapshot.PackageId,
            state.Snapshot.SavedAtUtc,
            databaseGeneration: null,
            state.Snapshot.SourceSets,
            state.Snapshot.ActiveSourceSetId,
            state.Snapshot.Sources,
            state.Snapshot.DiscoveryConfiguration,
            state.Snapshot.DatabaseTagOverrides);
        var path = Path.Combine(workspace.Root, "false-database-less.cia");

        var result = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            path,
            databaseLess);

        Assert.IsFalse(result.Accepted);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow("layout")]
    [DataRow("override")]
    [DataRow("membership")]
    public async Task IncoherentManifestRecoveryContextIsRejectedForDirectSaveAndRestore(
        string mismatchKind)
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var validPath = Path.Combine(workspace.Root, "coherent.cia");
        var saved = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            validPath,
            state.Snapshot);
        Assert.IsTrue(saved.Accepted, saved.Failure?.Description);
        var incoherent = CreateIncoherentSnapshot(state.Snapshot, mismatchKind);

        var directPath = Path.Combine(workspace.Root, $"direct-{mismatchKind}.cia");
        var direct = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            directPath,
            incoherent);

        var tamperedPath = Path.Combine(workspace.Root, $"tampered-{mismatchKind}.cia");
        File.Copy(validPath, tamperedPath);
        var tamperedManifest = new WorkingStateManifest(
            saved.Manifest!.PackageSchemaVersion,
            saved.Manifest.RepositorySchemaVersion,
            saved.Manifest.DatabaseSha256,
            incoherent);
        ReplaceEntry(
            tamperedPath,
            WorkingStatePackageFormat.ManifestEntryName,
            _ => JsonSerializer.SerializeToUtf8Bytes(
                tamperedManifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var before = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        var restored = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            tamperedPath);
        var after = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        Assert.IsFalse(direct.Accepted);
        Assert.IsFalse(File.Exists(directPath));
        Assert.IsFalse(restored.Accepted);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(before, after));
    }

    [TestMethod]
    public async Task EmptyRepositoryRoundTripsWithNoPublishedDatabase()
    {
        using var workspace = new Workspace();
        var snapshot = new WorkingStateSnapshot(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            databaseGeneration: null,
            sourceSets: [],
            activeSourceSetId: null,
            sources: [],
            new DiscoveryConfigurationSnapshot([], []),
            databaseTagOverrides: []);
        var packagePath = Path.Combine(workspace.Root, "empty.cia");

        var save = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            snapshot);
        using var restored = workspace.CreateFreshRepositoryContext();
        var restore = await restored.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            packagePath);

        Assert.IsTrue(save.Accepted);
        Assert.IsTrue(restore.Accepted);
        Assert.IsNull(restore.Manifest?.Snapshot.DatabaseGeneration);
        Assert.IsNull(await restored.Repository.ReadPublishedHierarchyDatabaseGenerationAsync());
        Assert.IsNull(await restored.Repository.ReadPublishedExtractionResultAsync());
    }

    [TestMethod]
    public async Task UnsafeEntryAndInconsistentManifestFailBeforeRepositoryMutation()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreatePublishedStateAsync();
        var packagePath = Path.Combine(workspace.Root, "valid.cia");
        var save = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            state.Snapshot);
        Assert.IsTrue(save.Accepted);
        var baseline = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        var unsafePath = Path.Combine(workspace.Root, "unsafe.cia");
        File.Copy(packagePath, unsafePath);
        using (var archive = ZipFile.Open(unsafePath, ZipArchiveMode.Update))
        {
            var unsafeEntry = archive.CreateEntry("../unexpected.txt");
            using var writer = new StreamWriter(unsafeEntry.Open());
            writer.Write("unsafe");
        }

        var unsafeResult = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            unsafePath);

        var inconsistentPath = Path.Combine(workspace.Root, "inconsistent.cia");
        File.Copy(packagePath, inconsistentPath);
        var wrongGeneration = new DatabaseGenerationSummary(
            OperationId.CreateNew(),
            state.Generation.Datasets);
        var wrongManifest = new WorkingStateManifest(
            save.Manifest!.PackageSchemaVersion,
            save.Manifest.RepositorySchemaVersion,
            save.Manifest.DatabaseSha256,
            CopySnapshot(state.Snapshot, databaseGeneration: wrongGeneration));
        ReplaceEntry(
            inconsistentPath,
            WorkingStatePackageFormat.ManifestEntryName,
            _ => JsonSerializer.SerializeToUtf8Bytes(
                wrongManifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var inconsistentResult = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            inconsistentPath);
        var after = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        Assert.IsFalse(unsafeResult.Accepted);
        Assert.IsFalse(inconsistentResult.Accepted);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(baseline, after));
    }

    [TestMethod]
    public async Task RepositoryReplacementFailureRollsBackToPreviousPublishedDatabase()
    {
        using var workspace = new Workspace();
        var first = await workspace.CreatePublishedStateAsync();
        var snapshotPath = Path.Combine(workspace.Root, "restore-candidate.sqlite3");
        await workspace.Repository.CreateWorkingStateSnapshotAsync(
            snapshotPath,
            first.Generation);
        var retained = await workspace.CreatePublishedStateAsync();

        await Assert.ThrowsAsync<Exception>(() =>
            workspace.Repository.RestoreWorkingStateSnapshotAsync(
                snapshotPath,
                StructuredInformationRepository.CurrentSchemaVersion,
                first.Generation,
                first.Snapshot.Sources,
                () =>
                {
                    using var corrupt = new FileStream(snapshotPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    corrupt.SetLength(Math.Max(1, corrupt.Length / 2));
                    return true;
                }));
        var after = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();

        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(retained.Generation, after));
    }

    private static void ReplaceEntry(
        string packagePath,
        string entryName,
        Func<byte[], byte[]> transform)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(entryName)!;
        byte[] bytes;
        using (var source = entry.Open())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var destination = replacement.Open();
        destination.Write(transform(bytes));
    }

    private static WorkingStateSnapshot CopySnapshot(
        WorkingStateSnapshot source,
        DateTimeOffset? savedAtUtc = null,
        DatabaseGenerationSummary? databaseGeneration = null) =>
        new(
            source.PackageId,
            savedAtUtc ?? source.SavedAtUtc,
            databaseGeneration ?? source.DatabaseGeneration,
            source.SourceSets,
            source.ActiveSourceSetId,
            source.Sources,
            source.DiscoveryConfiguration,
            source.DatabaseTagOverrides);

    private static WorkingStateSnapshot CreateIncoherentSnapshot(
        WorkingStateSnapshot source,
        string mismatchKind)
    {
        var discovery = source.DiscoveryConfiguration;
        var overrides = source.DatabaseTagOverrides;
        var sources = source.Sources;
        switch (mismatchKind)
        {
            case "layout":
                discovery = new DiscoveryConfigurationSnapshot(
                    discovery.Items,
                    discovery.SourceSets.Select((configuration, index) =>
                        index == 0
                            ? new SourceSetDiscoveryConfiguration(
                                configuration.SourceSetId,
                                RepeatedDataLayout.AlignRepeatedGroupsByPosition)
                            : configuration).ToArray());
                break;
            case "override":
                overrides =
                [
                    new WorkingStateDatabaseTagOverride(
                        source.DatabaseTagOverrides.Single().Identity,
                        "TamperedName")
                ];
                break;
            case "membership":
                sources = source.Sources.Select((item, index) =>
                    index == 0
                        ? item with { SourceSetId = source.SourceSets[1].SourceSetId }
                        : item).ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatchKind));
        }

        return new WorkingStateSnapshot(
            source.PackageId,
            source.SavedAtUtc,
            source.DatabaseGeneration,
            source.SourceSets,
            source.ActiveSourceSetId,
            sources,
            discovery,
            overrides);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR94.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly ApplicationPaths _paths;
        private readonly SourceInterpreter _interpreter;

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            _paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData"));
            Repository = new StructuredInformationRepository(_paths);
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            DatabaseService = new DatabaseGenerationService(
                Repository,
                _interpreter,
                new HierarchyFlatteningEngine(),
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<DatabaseGenerationService>.Instance);
            PackageService = CreatePackageService(Repository, _paths);
        }

        private Workspace(string root, string name)
        {
            _root = root;
            _paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(root, name));
            Repository = new StructuredInformationRepository(_paths);
            _interpreter = new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
            DatabaseService = new DatabaseGenerationService(
                Repository,
                _interpreter,
                new HierarchyFlatteningEngine(),
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<DatabaseGenerationService>.Instance);
            PackageService = CreatePackageService(Repository, _paths);
        }

        public string Root => _root;

        public StructuredInformationRepository Repository { get; }

        public DatabaseGenerationService DatabaseService { get; }

        public WorkingStatePackageService PackageService { get; }

        public WorkingStatePackageService CreatePackageService(
            IWorkingStatePackagePublisher publisher) =>
            new(
                Repository,
                _paths,
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<WorkingStatePackageService>.Instance,
                publisher);

        public Workspace CreateFreshRepositoryContext() => new(_root, "RestoredLocalAppData");

        public async Task<PublishedState> CreatePublishedStateAsync()
        {
            var firstSet = SourceSetId.CreateNew();
            var secondSet = SourceSetId.CreateNew();
            var first = CreateSource(
                firstSet,
                "first.xml",
                "<records><record><code>A</code><qty>1</qty></record><record><code>B</code><qty>2</qty></record></records>");
            var second = CreateSource(
                secondSet,
                "second.xml",
                "<items><item key=\"C\"><name>Gamma</name></item></items>");
            var originalArchiveId = SourceId.CreateNew();
            var originalArchivePath = Path.Combine(_root, "sources", "source.zip");
            File.WriteAllBytes(originalArchivePath, [0x50, 0x4b]);
            second = second with
            {
                ArchiveProvenance = new ArchiveSourceProvenance(
                    originalArchiveId,
                    originalArchivePath,
                    [new ArchiveLineageItem(originalArchiveId, originalArchivePath, 1)],
                    1,
                    "second.xml",
                    Path.GetDirectoryName(second.Path)!,
                    ArchiveExtractionRetention.ManagedTemporary,
                    ArchiveNestingDepth.Default,
                    PersistentExtractionDirectory: null)
            };
            var firstIdentities = await ReadIdentitiesAsync(firstSet, first);
            var secondIdentities = await ReadIdentitiesAsync(secondSet, second);
            var firstConfiguration = firstIdentities.Select(identity => new DiscoveryConfigurationItem(
                identity,
                DiscoveryInformationDisposition.Selected)).ToArray();
            var secondConfiguration = secondIdentities.Select(identity => new DiscoveryConfigurationItem(
                identity,
                DiscoveryInformationDisposition.Selected)).ToArray();
            var overridden = firstIdentities[0];
            var overrides = new Dictionary<DiscoveryInformationIdentity, string>
            {
                [overridden] = "Renamed"
            };
            var specification = new DatabaseBuildSpecification(
            [
                new DatabaseDatasetBuildSpecification(
                    firstSet,
                    "Set 1",
                    1,
                    RepeatedDataLayout.StructuralRows,
                    [first],
                    DatabaseTagMapper.CreateFieldMappings(firstSet, firstConfiguration, overrides)),
                new DatabaseDatasetBuildSpecification(
                    secondSet,
                    "Other set",
                    2,
                    RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                    [second],
                    DatabaseTagMapper.CreateFieldMappings(secondSet, secondConfiguration, overrides))
            ]);
            var built = await DatabaseService.BuildAsync(
                OperationCorrelation.CreateNew(),
                specification);
            Assert.IsTrue(built.Accepted, built.Failure?.Description);
            var discovery = new DiscoveryConfigurationSnapshot(
                firstConfiguration.Concat(secondConfiguration).ToArray(),
                [
                    new SourceSetDiscoveryConfiguration(firstSet, RepeatedDataLayout.StructuralRows),
                    new SourceSetDiscoveryConfiguration(
                        secondSet,
                        RepeatedDataLayout.NumberRepeatedValuesIntoColumns)
                ]);
            var snapshot = new WorkingStateSnapshot(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                built.PublishedGeneration,
                [
                    new WorkingStateSourceSet(firstSet, "Set 1", 1),
                    new WorkingStateSourceSet(secondSet, "Other set", 2)
                ],
                secondSet,
                [first, second],
                discovery,
                [new WorkingStateDatabaseTagOverride(overridden, "Renamed")]);
            return new PublishedState(built.PublishedGeneration!, snapshot);
        }

        private LoadedSourceContract CreateSource(SourceSetId set, string name, string xml)
        {
            var path = Path.Combine(_root, "sources", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, xml);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                set,
                path,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        private async Task<DiscoveryInformationIdentity[]> ReadIdentitiesAsync(
            SourceSetId set,
            LoadedSourceContract source)
        {
            var interpreted = await _interpreter.InterpretAsync(source);
            return interpreted.Source!.Values
                .Where(value => value.Lineage is not null)
                .Select(value => HierarchySourceOccurrence.FromInterpretedValue(set, value).Identity)
                .Distinct()
                .ToArray();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static WorkingStatePackageService CreatePackageService(
            StructuredInformationRepository repository,
            ApplicationPaths paths) =>
            new(
                repository,
                paths,
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<WorkingStatePackageService>.Instance);
    }

    private sealed class GatedWorkingStatePackagePublisher : IWorkingStatePackagePublisher
    {
        private readonly TaskCompletionSource _publicationReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublicationReached => _publicationReached.Task;

        public async Task PublishAsync(
            string candidatePath,
            string targetPath,
            WorkingStatePublicationMode publicationMode)
        {
            _publicationReached.TrySetResult();
            await _release.Task;
            await FileSystemWorkingStatePackagePublisher.Instance.PublishAsync(
                candidatePath,
                targetPath,
                publicationMode);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed record PublishedState(
        DatabaseGenerationSummary Generation,
        WorkingStateSnapshot Snapshot);

    private sealed class NullHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record) { }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record) { }
    }
}
