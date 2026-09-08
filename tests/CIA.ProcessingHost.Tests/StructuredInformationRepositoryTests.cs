using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Repository;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class StructuredInformationRepositoryTests
{
    [TestMethod]
    public async Task CandidateRemainsNonAuthoritativeUntilValidatedPublication()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var sourceId = SourceId.CreateNew();
        var mapping = CreateMapping("Original", "Published");
        var candidate = await repository.CreateDatabaseCandidateAsync(
            OperationCorrelation.CreateNew(),
            mapping,
            [sourceId]);

        await WriteCandidateValuesAsync(
            repository,
            candidate,
            sourceId,
            [new MappedDatabaseValue("Published", "Original", " exact value ", sourceId)]);

        Assert.IsNull(await repository.ReadPublishedDatabaseGenerationAsync());
        Assert.IsEmpty(await repository.QueryPublishedDatabaseValuesAsync());

        var published = await repository.ValidateAndPublishDatabaseCandidateAsync(candidate);
        var values = await repository.QueryPublishedDatabaseValuesAsync();

        Assert.AreEqual(candidate.Correlation.OperationId, published.OperationId);
        Assert.AreEqual(1, published.ValueCount);
        Assert.HasCount(1, values);
        Assert.AreEqual("Published", values[0].DatabaseTagName);
        Assert.AreEqual("Original", values[0].SourceInformationType);
        Assert.AreEqual(" exact value ", values[0].Value);
        Assert.AreEqual(sourceId, values[0].SourceId);
    }

    [TestMethod]
    public async Task InvalidOrDiscardedReplacementCannotChangePublishedGeneration()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var sourceId = SourceId.CreateNew();
        var original = await CreateAndPublishAsync(
            repository,
            sourceId,
            CreateMapping("tag", "tag"),
            "first");
        var replacement = await repository.CreateDatabaseCandidateAsync(
            OperationCorrelation.CreateNew(),
            CreateMapping("tag", "Renamed"),
            [sourceId]);

        await Assert.ThrowsExactlyAsync<StructuredInformationRepositoryException>(
            () => repository.WriteDatabaseCandidateSourceAsync(
                replacement,
                sourceId,
                async (writer, cancellationToken) =>
                {
                    await writer.AddBatchAsync(
                        [new MappedDatabaseValue("Wrong", "tag", "second", sourceId)],
                        cancellationToken);
                    return true;
                }));
        await Assert.ThrowsExactlyAsync<StructuredInformationRepositoryException>(
            () => repository.ValidateAndPublishDatabaseCandidateAsync(replacement));
        await repository.DiscardDatabaseCandidateAsync(replacement);

        var retained = await repository.ReadPublishedDatabaseGenerationAsync();
        var values = await repository.QueryPublishedDatabaseValuesAsync();
        Assert.AreEqual(original.OperationId, retained?.OperationId);
        Assert.HasCount(1, values);
        Assert.AreEqual("first", values[0].Value);
        Assert.AreEqual("tag", values[0].DatabaseTagName);
    }

    [TestMethod]
    public async Task SuccessfulReplacementIsAtomicAndDeterministic()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var firstSource = SourceId.CreateNew();
        var secondSource = SourceId.CreateNew();
        var mapping = new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping("Shared", ["beta", "alpha"])
        ]);
        await CreateAndPublishAsync(repository, firstSource, mapping, "one", "alpha");
        var replacement = await repository.CreateDatabaseCandidateAsync(
            OperationCorrelation.CreateNew(),
            mapping,
            [firstSource, secondSource]);
        await WriteCandidateValuesAsync(
            repository,
            replacement,
            firstSource,
            [new MappedDatabaseValue("Shared", "alpha", "one", firstSource)]);
        await WriteCandidateValuesAsync(
            repository,
            replacement,
            secondSource,
            [new MappedDatabaseValue("Shared", "beta", "two", secondSource)]);

        var beforePublication = await repository.QueryPublishedDatabaseValuesAsync();
        Assert.HasCount(1, beforePublication);
        Assert.AreEqual("one", beforePublication[0].Value);

        await repository.ValidateAndPublishDatabaseCandidateAsync(replacement);
        var published = await repository.ReadPublishedDatabaseGenerationAsync();
        var afterPublication = await repository.QueryPublishedDatabaseValuesAsync();
        CollectionAssert.AreEqual(
            new[] { "beta", "alpha" },
            published!.Mapping.Columns.Single().SourceInformationTypes.ToArray());
        CollectionAssert.AreEqual(
            new[] { "alpha:one", "beta:two" },
            afterPublication.Select(value =>
                $"{value.SourceInformationType}:{value.Value}").ToArray());
    }

    [TestMethod]
    public async Task NewRepositoryCreatesVersionedWalSchemaInManagedDatabaseDirectory()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();

        await repository.InitializeAsync();

        Assert.AreEqual(
            Path.Combine(workspace.Paths.DatabaseDirectory, "cia.sqlite3"),
            repository.DatabasePath);
        Assert.IsTrue(File.Exists(repository.DatabasePath));
        Assert.IsFalse(workspace.Paths.IsInstalledBinaryPath(repository.DatabasePath));

        await using var connection = await OpenConnectionAsync(repository.DatabasePath);
        Assert.AreEqual(StructuredInformationRepository.CurrentSchemaVersion, await ScalarIntAsync(
            connection,
            "PRAGMA user_version;"));
        Assert.AreEqual("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));

        var indexes = await ReadIndexNamesAsync(connection);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "ix_indexed_occurrences_tag",
                "ix_indexed_occurrences_source_id",
                "ix_indexed_occurrences_tag_value",
                "ix_extraction_values_database_field",
                "ix_extraction_values_source"
            },
            indexes);
    }

    [TestMethod]
    public async Task ExactOccurrencesPersistAcrossCloseAndReopen()
    {
        using var workspace = new RepositoryWorkspace();
        var sourceId = SourceId.CreateNew();
        var original = new IndexedOccurrence(
            "  Tag.MixedCase  ",
            "  exact <content attr=\"'quoted'\">Åß中</content>  ",
            sourceId);
        var firstRepository = workspace.CreateRepository();

        await firstRepository.AddBatchAsync([original]);
        var reopenedRepository = workspace.CreateRepository();
        var results = await reopenedRepository.QueryBySourceIdAsync(sourceId);

        Assert.HasCount(1, results);
        Assert.AreEqual(original, results[0]);
    }

    [TestMethod]
    public async Task IndexedParameterizedQueriesRetainCrossSourceIdentity()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var firstSourceId = SourceId.CreateNew();
        var secondSourceId = SourceId.CreateNew();
        const string specialTag = "Tag'; DROP TABLE indexed_occurrences; --";
        const string specialValue = "value % _ ' \" [brackets]";

        await repository.AddBatchAsync(
            [
                new IndexedOccurrence(specialTag, specialValue, firstSourceId),
                new IndexedOccurrence(specialTag, "other", secondSourceId),
                new IndexedOccurrence("DifferentTag", specialValue, firstSourceId)
            ]);

        var byTag = await repository.QueryByTagAsync(specialTag);
        var bySource = await repository.QueryBySourceIdAsync(firstSourceId);
        var byTagAndValue = await repository.QueryByTagAndValueAsync(specialTag, specialValue);

        Assert.HasCount(2, byTag);
        CollectionAssert.AreEquivalent(
            new[] { firstSourceId, secondSourceId },
            byTag.Select(occurrence => occurrence.SourceId).ToArray());
        Assert.IsTrue(byTag.All(occurrence => occurrence.Tag == specialTag));
        Assert.HasCount(2, bySource);
        Assert.IsTrue(bySource.All(occurrence => occurrence.SourceId == firstSourceId));
        Assert.HasCount(1, byTagAndValue);
        Assert.AreEqual(specialValue, byTagAndValue[0].Value);
        Assert.AreEqual(firstSourceId, byTagAndValue[0].SourceId);
    }

    [TestMethod]
    public async Task BatchFailureRollsBackEveryOccurrence()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var sourceId = SourceId.CreateNew();
        await repository.InitializeAsync();

        await using (var connection = await OpenConnectionAsync(repository.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TRIGGER reject_test_value
                BEFORE INSERT ON indexed_occurrences
                WHEN NEW.value = 'reject-for-rollback-test'
                BEGIN
                    SELECT RAISE(ABORT, 'intentional rollback test');
                END;
                """);
        }

        await Assert.ThrowsExactlyAsync<StructuredInformationRepositoryException>(
            () => repository.AddBatchAsync(
                [
                    new IndexedOccurrence("First", "valid", sourceId),
                    new IndexedOccurrence("Second", "reject-for-rollback-test", sourceId)
                ]));

        Assert.IsEmpty(await repository.QueryBySourceIdAsync(sourceId));
    }

    [TestMethod]
    public async Task WalReadersSeeOnlyCommittedSnapshotsWhileWriterCommits()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var sourceId = SourceId.CreateNew();
        await repository.InitializeAsync();

        await using var readerConnection = await OpenConnectionAsync(repository.DatabasePath);
        await using var readerTransaction = readerConnection.BeginTransaction(deferred: true);
        Assert.AreEqual(0, await CountOccurrencesAsync(readerConnection, readerTransaction));

        await repository.AddBatchAsync([new IndexedOccurrence("Tag", "Value", sourceId)]);

        Assert.AreEqual(0, await CountOccurrencesAsync(readerConnection, readerTransaction));
        await readerTransaction.CommitAsync();
        Assert.HasCount(1, await repository.QueryBySourceIdAsync(sourceId));
    }

    [TestMethod]
    public async Task CurrentSchemaReopensWithoutChangingExistingData()
    {
        using var workspace = new RepositoryWorkspace();
        var sourceId = SourceId.CreateNew();
        var firstRepository = workspace.CreateRepository();
        await firstRepository.AddBatchAsync([new IndexedOccurrence("Tag", "Value", sourceId)]);
        var databaseWriteTime = File.GetLastWriteTimeUtc(firstRepository.DatabasePath);

        var reopenedRepository = workspace.CreateRepository();
        await reopenedRepository.InitializeAsync();
        var results = await reopenedRepository.QueryBySourceIdAsync(sourceId);

        Assert.HasCount(1, results);
        Assert.AreEqual("Value", results[0].Value);
        Assert.IsTrue(File.GetLastWriteTimeUtc(reopenedRepository.DatabasePath) >= databaseWriteTime);
    }

    [TestMethod]
    public async Task VersionTwoRepositoryAddsExtractionSchemaWithoutChangingExistingData()
    {
        using var workspace = new RepositoryWorkspace();
        var sourceId = SourceId.CreateNew();
        var originalRepository = workspace.CreateRepository();
        await originalRepository.AddBatchAsync(
            [new IndexedOccurrence("Tag", "retained", sourceId)]);

        await using (var connection = await OpenConnectionAsync(originalRepository.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                """
                DROP TABLE extraction_publication;
                DROP TABLE extraction_values;
                DROP TABLE extraction_column_sources;
                DROP TABLE extraction_columns;
                DROP TABLE extraction_results;
                PRAGMA user_version = 2;
                """);
        }

        var migratedRepository = workspace.CreateRepository();
        await migratedRepository.InitializeAsync();
        var retained = await migratedRepository.QueryBySourceIdAsync(sourceId);

        Assert.HasCount(1, retained);
        Assert.AreEqual("retained", retained[0].Value);
        await using var migratedConnection = await OpenConnectionAsync(
            migratedRepository.DatabasePath);
        Assert.AreEqual(
            StructuredInformationRepository.CurrentSchemaVersion,
            await ScalarIntAsync(migratedConnection, "PRAGMA user_version;"));
        Assert.AreEqual(
            1,
            await ScalarIntAsync(
                migratedConnection,
                "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name = 'extraction_results');"));
    }

    [TestMethod]
    public async Task FutureAndInvalidCurrentSchemasAreRejectedWithoutReplacement()
    {
        using var futureWorkspace = new RepositoryWorkspace();
        Directory.CreateDirectory(futureWorkspace.Paths.DatabaseDirectory);
        var futureRepository = futureWorkspace.CreateRepository();
        await using (var connection = await OpenConnectionAsync(futureRepository.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                $"PRAGMA user_version = {StructuredInformationRepository.CurrentSchemaVersion + 1};");
            await ExecuteAsync(connection, "CREATE TABLE future_data (value TEXT NOT NULL);");
            await ExecuteAsync(connection, "INSERT INTO future_data (value) VALUES ('preserve-me');");
        }

        var futureException = await Assert.ThrowsExactlyAsync<
            UnsupportedRepositorySchemaVersionException>(
                () => futureRepository.InitializeAsync());
        Assert.AreEqual(
            StructuredInformationRepository.CurrentSchemaVersion + 1,
            futureException.ActualVersion);
        await using (var connection = await OpenConnectionAsync(futureRepository.DatabasePath))
        {
            Assert.AreEqual("preserve-me", await ScalarStringAsync(
                connection,
                "SELECT value FROM future_data;"));
        }

        using var invalidWorkspace = new RepositoryWorkspace();
        Directory.CreateDirectory(invalidWorkspace.Paths.DatabaseDirectory);
        var invalidRepository = invalidWorkspace.CreateRepository();
        await using (var connection = await OpenConnectionAsync(invalidRepository.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                $"PRAGMA user_version = {StructuredInformationRepository.CurrentSchemaVersion};");
        }

        await Assert.ThrowsExactlyAsync<StructuredInformationRepositoryException>(
            () => invalidRepository.InitializeAsync());
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string[]> ReadIndexNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<int> CountOccurrencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM indexed_occurrences;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static DatabaseMappingSnapshot CreateMapping(
        string informationType,
        string databaseTagName)
    {
        return new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping(databaseTagName, [informationType])
        ]);
    }

    private static async Task<DatabaseGenerationSummary> CreateAndPublishAsync(
        StructuredInformationRepository repository,
        SourceId sourceId,
        DatabaseMappingSnapshot mapping,
        string value,
        string informationType = "tag")
    {
        var candidate = await repository.CreateDatabaseCandidateAsync(
            OperationCorrelation.CreateNew(),
            mapping,
            [sourceId]);
        var databaseTagName = mapping.Columns.Single().DatabaseTagName;
        await WriteCandidateValuesAsync(
            repository,
            candidate,
            sourceId,
            [new MappedDatabaseValue(databaseTagName, informationType, value, sourceId)]);
        return await repository.ValidateAndPublishDatabaseCandidateAsync(candidate);
    }

    private static async Task WriteCandidateValuesAsync(
        StructuredInformationRepository repository,
        DatabaseCandidate candidate,
        SourceId sourceId,
        IReadOnlyList<MappedDatabaseValue> values)
    {
        Assert.IsTrue(await repository.WriteDatabaseCandidateSourceAsync(
            candidate,
            sourceId,
            async (writer, cancellationToken) =>
            {
                await writer.AddBatchAsync(values, cancellationToken);
                return true;
            }));
    }

    private sealed class RepositoryWorkspace : IDisposable
    {
        private readonly string _testRoot;

        public RepositoryWorkspace()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), "CIA.SPR77.Tests");
            Root = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = ApplicationPaths.FromLocalApplicationData(Path.Combine(Root, "LocalAppData"));
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public StructuredInformationRepository CreateRepository()
        {
            return new StructuredInformationRepository(Paths);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            SqliteConnection.ClearAllPools();
            var resolvedRoot = Path.GetFullPath(_testRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var resolvedTarget = Path.GetFullPath(Root);
            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a test directory outside the SPR-77 test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
