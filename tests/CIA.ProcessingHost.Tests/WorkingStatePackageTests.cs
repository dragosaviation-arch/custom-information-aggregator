using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Export;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.Core.WorkingState;
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
    public async Task VersionTwoRoundTripsFamilyDatabaseAndExtractionWithoutChangingFamilyIdentity()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreateFamilyPublishedStateAsync();
        var exportConfiguration = CreateFamilyExportConfiguration(state.Generation);
        var snapshot = new WorkingStateSnapshot(
            state.Snapshot.PackageId,
            state.Snapshot.SavedAtUtc,
            state.Snapshot.DatabaseGeneration,
            state.Snapshot.ExtractionResult,
            exportConfiguration,
            state.Snapshot.SourceSets,
            state.Snapshot.ActiveSourceSetId,
            state.Snapshot.Sources,
            state.Snapshot.DiscoveryConfiguration,
            state.Snapshot.DatabaseTagOverrides);
        var packagePath = Path.Combine(workspace.Root, "family-v2.cia");
        WorkingStateDatabaseCoherenceValidator.Validate(snapshot);
        var directSnapshotPath = Path.Combine(workspace.Root, "family-v2.sqlite3");
        await workspace.Repository.CreateWorkingStateSnapshotAsync(
            directSnapshotPath,
            state.Generation,
            snapshot.ExtractionResult);
        var contractJson = JsonSerializer.Serialize(
            new WorkingStateManifest(2, 7, new string('0', 64), snapshot),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(JsonSerializer.Deserialize<WorkingStateManifest>(
            contractJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var saved = await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            packagePath,
            snapshot);
        Assert.IsTrue(saved.Accepted, saved.Failure?.Description);
        Assert.AreEqual(2, saved.Manifest?.PackageSchemaVersion);
        foreach (var source in snapshot.Sources)
        {
            File.Delete(source.Path);
        }

        using var restored = workspace.CreateFreshRepositoryContext();
        var result = await restored.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            packagePath);
        var database = await restored.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        var extraction = await restored.Repository.ReadPublishedExtractionResultAsync();

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(
            state.Generation,
            database));
        Assert.IsTrue(ExtractionResultSnapshotComparer.AreEquivalent(
            snapshot.ExtractionResult,
            extraction));
        Assert.AreEqual(
            JsonSerializer.Serialize(exportConfiguration),
            JsonSerializer.Serialize(result.Manifest?.Snapshot.ExportConfiguration));
        Assert.IsTrue(state.Generation.Datasets.Single().RowFamilies
            .Select(family => family.Id)
            .SequenceEqual(database!.Datasets.Single().RowFamilies.Select(family => family.Id)));
        Assert.IsTrue(state.Snapshot.ExtractionResult!.Datasets.Single().RowFamilies
            .Select(family => family.Id)
            .SequenceEqual(extraction!.Datasets.Single().RowFamilies.Select(family => family.Id)));

        var repeated = extraction.Datasets.Single().RowFamilies.Single(family =>
            family.Kind == RowFamilyKind.Repeated);
        var rows = new List<ExtractionResultRow>();
        await foreach (var row in restored.Repository.StreamPublishedExtractionRowsAsync(
                           extraction.OperationId,
                           repeated.Id))
        {
            rows.Add(row);
        }

        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(row =>
            row.SemanticIdentity?.RowFamilyId == repeated.Id
            && row.Cells.SelectMany(cell => cell.Values).All(value =>
                value.OriginatingRowFamilyId is not null
                && value.SourceId == row.Source.SourceId)));
    }

    private static ExportConfigurationSnapshot CreateFamilyExportConfiguration(
        DatabaseGenerationSummary generation)
    {
        var workbookId = WorkbookDefinitionId.CreateNew();
        var families = generation.Datasets.SelectMany(dataset => dataset.RowFamilies)
            .OrderBy(family => family.Ordinal).ToArray();
        var worksheetIds = families.ToDictionary(
            family => family.Id,
            _ => WorksheetDefinitionId.CreateNew());
        return new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                workbookId,
                "Families.xlsx",
                1,
                families.Select((family, index) => new WorksheetDefinition(
                    worksheetIds[family.Id],
                    workbookId,
                    family.Id.SourceSetId,
                    family.Id,
                    $"Family {index + 1}",
                    index + 1)).ToArray())],
            families.Select((family, index) => new SourceSetExportConfiguration(
                family.Id.SourceSetId,
                family.Id,
                isEnabled: index != 1,
                worksheetIds[family.Id],
                family.Columns.Select(column => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    false)).ToArray(),
                [])).ToArray());
    }

    [TestMethod]
    public async Task VersionOnePreservesUserConfigurationButInvalidatesDerivedPublications()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreateFamilyPublishedStateAsync();
        var versionTwoPath = Path.Combine(workspace.Root, "family-v2-source.cia");
        Assert.IsTrue((await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            versionTwoPath,
            state.Snapshot)).Accepted);
        var versionOnePath = Path.Combine(workspace.Root, "legacy-v1.cia");
        File.Copy(versionTwoPath, versionOnePath);
        ReplaceEntry(versionOnePath, WorkingStatePackageFormat.ManifestEntryName, bytes =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"packageSchemaVersion\": 2",
                "\"packageSchemaVersion\": 1",
                StringComparison.Ordinal)));
        var originalBytes = await File.ReadAllBytesAsync(versionOnePath);

        using var restored = workspace.CreateFreshRepositoryContext();
        var result = await restored.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            versionOnePath);

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.AreEqual(1, result.Manifest?.PackageSchemaVersion);
        Assert.IsNull(result.Manifest?.Snapshot.DatabaseGeneration);
        Assert.IsNull(result.Manifest?.Snapshot.ExtractionResult);
        Assert.IsTrue(result.Manifest!.Snapshot.Sources.Select(source => source.SourceId)
            .SequenceEqual(state.Snapshot.Sources.Select(source => source.SourceId)));
        Assert.IsTrue(result.Manifest.Snapshot.DiscoveryConfiguration.Items
            .SequenceEqual(state.Snapshot.DiscoveryConfiguration.Items));
        Assert.IsNull(await restored.Repository.ReadPublishedHierarchyDatabaseGenerationAsync());
        Assert.IsNull(await restored.Repository.ReadPublishedExtractionResultAsync());
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(versionOnePath));

        var resavedPath = Path.Combine(workspace.Root, "migrated-v2.cia");
        var resaved = await restored.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            resavedPath,
            result.Manifest.Snapshot);
        Assert.IsTrue(resaved.Accepted, resaved.Failure?.Description);
        Assert.AreEqual(2, resaved.Manifest?.PackageSchemaVersion);
    }

    [TestMethod]
    public async Task VersionTwoRejectsIncompleteExtractionFamilyStateWithoutReplacingPublications()
    {
        using var workspace = new Workspace();
        var state = await workspace.CreateFamilyPublishedStateAsync();
        var validPath = Path.Combine(workspace.Root, "family-valid.cia");
        Assert.IsTrue((await workspace.PackageService.SaveAsync(
            OperationCorrelation.CreateNew(),
            validPath,
            state.Snapshot)).Accepted);
        var validBytes = await File.ReadAllBytesAsync(validPath);
        var corruptPath = Path.Combine(workspace.Root, "family-incomplete.cia");
        File.Copy(validPath, corruptPath);
        ReplaceEntry(corruptPath, WorkingStatePackageFormat.ManifestEntryName, bytes =>
        {
            var root = JsonNode.Parse(bytes)
                ?? throw new AssertFailedException("The V2 manifest must be valid JSON.");
            var families = root["snapshot"]?["extractionResult"]?["datasets"]?[0]?
                ["rowFamilies"]?.AsArray()
                ?? throw new AssertFailedException(
                    "The V2 manifest must contain Extraction Row Families.");
            Assert.IsGreaterThan(1, families.Count);
            families.RemoveAt(families.Count - 1);
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        });

        var result = await workspace.PackageService.RestoreAsync(
            OperationCorrelation.CreateNew(),
            corruptPath);
        var database = await workspace.Repository.ReadPublishedHierarchyDatabaseGenerationAsync();
        var extraction = await workspace.Repository.ReadPublishedExtractionResultAsync();

        Assert.IsFalse(result.Accepted);
        Assert.IsTrue(DatabaseGenerationSnapshotComparer.AreEquivalent(state.Generation, database));
        Assert.IsTrue(ExtractionResultSnapshotComparer.AreEquivalent(
            state.Snapshot.ExtractionResult,
            extraction));
        CollectionAssert.AreEqual(validBytes, await File.ReadAllBytesAsync(validPath));
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
                $"\"packageSchemaVersion\": {WorkingStatePackageFormat.CurrentSchemaVersion}",
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

        public async Task<PublishedState> CreateFamilyPublishedStateAsync()
        {
            var sourceSet = SourceSetId.CreateNew();
            var source = CreateSource(
                sourceSet,
                "family.xml",
                "<catalog><title>Catalog</title><record><code>A</code></record><record><code>B</code></record></catalog>");
            var identities = await ReadIdentitiesAsync(sourceSet, source);
            var root = new RowFamilyDefinition(
                new RowFamilyId(sourceSet, RowFamilyKind.Root, "/catalog"),
                parentRowFamilyId: null);
            var repeated = new RowFamilyDefinition(
                new RowFamilyId(sourceSet, RowFamilyKind.Repeated, "/catalog/record"),
                root.Id);
            RowFamilyDefinition[] families = [root, repeated];
            var assignments = identities.ToDictionary(
                identity => identity,
                identity => identity.StructuralPath == repeated.Id.AnchorPath
                    || identity.StructuralPath.StartsWith(
                        repeated.Id.AnchorPath + "/",
                        StringComparison.Ordinal)
                            ? repeated.Id
                            : root.Id);
            var configurationItems = identities.Select(identity =>
                new DiscoveryConfigurationItem(
                    identity,
                    DiscoveryInformationDisposition.Selected)).ToArray();
            var overridden = identities.Single(identity => identity.InformationType == "code");
            var overrides = new Dictionary<DiscoveryInformationIdentity, string>
            {
                [overridden] = "BookCode"
            };
            var specification = new DatabaseBuildSpecification(
            [
                new DatabaseDatasetBuildSpecification(
                    sourceSet,
                    "Family set",
                    1,
                    RepeatedDataLayout.StructuralRows,
                    [source],
                    families,
                    DatabaseTagMapper.CreateFieldMappings(
                        sourceSet,
                        configurationItems,
                        overrides,
                        assignments))
            ]);
            var built = await DatabaseService.BuildAsync(
                OperationCorrelation.CreateNew(),
                specification);
            Assert.IsTrue(built.Accepted, built.Failure?.Description);
            var extraction = await Repository.ExtractPublishedDatabaseAsync(
                OperationCorrelation.CreateNew(),
                built.PublishedGeneration!);
            var snapshot = new WorkingStateSnapshot(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                built.PublishedGeneration,
                extraction,
                [new WorkingStateSourceSet(sourceSet, "Family set", 1)],
                sourceSet,
                [source],
                new DiscoveryConfigurationSnapshot(
                    configurationItems,
                    [new SourceSetDiscoveryConfiguration(
                        sourceSet,
                        RepeatedDataLayout.StructuralRows)]),
                [new WorkingStateDatabaseTagOverride(overridden, "BookCode")]);
            return new PublishedState(built.PublishedGeneration!, snapshot);
        }

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
