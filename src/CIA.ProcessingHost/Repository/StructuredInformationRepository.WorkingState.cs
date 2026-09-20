using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    public async Task<DatabaseGenerationSummary?> CreateWorkingStateSnapshotAsync(
        string snapshotPath,
        DatabaseGenerationSummary? expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(snapshotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!);

            await using (var source = CreateConnection(allowCreate: false))
            await using (var destination = CreateExternalConnection(snapshotPath, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            await RemoveExtractionPublicationAsync(snapshotPath, cancellationToken)
                .ConfigureAwait(false);
            var actual = await ValidateWorkingStateSnapshotAsync(
                    snapshotPath,
                    CurrentSchemaVersion,
                    expectedGeneration,
                    expectedSources: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return actual;
        }
        catch
        {
            DeleteIfExists(snapshotPath);
            throw;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<DatabaseGenerationSummary?> ValidateWorkingStateSnapshotAsync(
        string snapshotPath,
        int expectedSchemaVersion,
        DatabaseGenerationSummary? expectedGeneration,
        IReadOnlyList<LoadedSourceContract>? expectedSources,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        if (!File.Exists(snapshotPath))
        {
            throw new StructuredInformationRepositoryException(
                "The working-state SQLite snapshot is unavailable.");
        }

        try
        {
            await using var connection = CreateExternalConnection(snapshotPath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version != expectedSchemaVersion || version != CurrentSchemaVersion)
            {
                throw new UnsupportedRepositorySchemaVersionException(
                    version,
                    CurrentSchemaVersion);
            }

            await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await ValidateIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
            var actualGeneration = await ReadSnapshotPublishedGenerationAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!GenerationsAgree(expectedGeneration, actualGeneration))
            {
                throw new StructuredInformationRepositoryException(
                    "The SQLite snapshot published Database does not match the working-state manifest.");
            }

            if (expectedSources is not null && actualGeneration is not null)
            {
                await ValidatePublishedSourceRelationshipsAsync(
                        connection,
                        actualGeneration,
                        expectedSources,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await HasPublishedExtractionAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new StructuredInformationRepositoryException(
                    "A working-state SQLite snapshot cannot retain an Extraction publication.");
            }

            return actualGeneration;
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The working-state SQLite snapshot is invalid.",
                exception);
        }
    }

    public async Task<DatabaseGenerationSummary?> RestoreWorkingStateSnapshotAsync(
        string snapshotPath,
        int expectedSchemaVersion,
        DatabaseGenerationSummary? expectedGeneration,
        IReadOnlyList<LoadedSourceContract> expectedSources,
        Func<bool> tryEnterNonCancellablePublicationBoundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tryEnterNonCancellablePublicationBoundary);
        await ValidateWorkingStateSnapshotAsync(
                snapshotPath,
                expectedSchemaVersion,
                expectedGeneration,
                expectedSources,
                cancellationToken)
            .ConfigureAwait(false);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var rollbackPath = DatabasePath + ".restore-rollback-" + Guid.CreateVersion7().ToString("N");
        var replacementStarted = false;
        try
        {
            await BackupDatabaseAsync(DatabasePath, rollbackPath, resetDestination: true, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!tryEnterNonCancellablePublicationBoundary())
            {
                throw new OperationCanceledException(
                    "Working-state restore was cancelled before repository publication.",
                    cancellationToken);
            }

            replacementStarted = true;
            SqliteConnection.ClearAllPools();
            await BackupDatabaseAsync(snapshotPath, DatabasePath, resetDestination: false, CancellationToken.None)
                .ConfigureAwait(false);
            var restored = await ValidateWorkingStateSnapshotAsync(
                    DatabasePath,
                    expectedSchemaVersion,
                    expectedGeneration,
                    expectedSources,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return restored;
        }
        catch
        {
            if (replacementStarted && File.Exists(rollbackPath))
            {
                SqliteConnection.ClearAllPools();
                await BackupDatabaseAsync(rollbackPath, DatabasePath, resetDestination: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            DeleteIfExists(rollbackPath);
            _writerGate.Release();
        }
    }

    private static async Task RemoveExtractionPublicationAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateExternalConnection(snapshotPath, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            await foreignKeys.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM hierarchy_extraction_publication;
                DELETE FROM hierarchy_extraction_results;
                DELETE FROM extraction_publication;
                DELETE FROM extraction_results;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string destinationPath,
        bool resetDestination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resetDestination)
        {
            DeleteIfExists(destinationPath);
        }
        await using var source = CreateExternalConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = CreateExternalConnection(
            destinationPath,
            SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task<DatabaseGenerationSummary?> ReadSnapshotPublishedGenerationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT generation_id FROM hierarchy_database_publication WHERE singleton_id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        var value = Convert.ToString(result, CultureInfo.InvariantCulture);
        if (!Guid.TryParseExact(value, "D", out var operationId))
        {
            throw new StructuredInformationRepositoryException(
                "The working-state snapshot contains an invalid Database publication identity.");
        }

        return await ReadHierarchySummaryAsync(
                connection,
                transaction: null,
                OperationId.From(operationId),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new StructuredInformationRepositoryException(
                "The working-state snapshot Database publication is incomplete.");
    }

    private static async Task<bool> HasPublishedExtractionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM hierarchy_extraction_publication)
                OR EXISTS(SELECT 1 FROM extraction_publication);
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ValidatePublishedSourceRelationshipsAsync(
        SqliteConnection connection,
        DatabaseGenerationSummary generation,
        IReadOnlyList<LoadedSourceContract> expectedSources,
        CancellationToken cancellationToken)
    {
        var sources = expectedSources.ToDictionary(
            source => (source.SourceSetId, source.SourceId));
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_set_id, source_id, full_source_path, source_kind,
                   archive_path, archive_member_path
            FROM hierarchy_database_sources
            WHERE generation_id = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generation.OperationId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sourceSetId = SourceSetId.From(Guid.Parse(reader.GetString(0)));
            var sourceId = SourceId.From(Guid.Parse(reader.GetString(1)));
            if (!sources.TryGetValue((sourceSetId, sourceId), out var expected)
                || !string.Equals(
                    Path.GetFullPath(expected.Path),
                    Path.GetFullPath(reader.GetString(2)),
                    StringComparison.OrdinalIgnoreCase)
                || (int)expected.Kind != reader.GetInt32(3)
                || !string.Equals(
                    expected.ArchiveProvenance?.OriginalArchivePath,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    expected.ArchiveProvenance?.ArchiveMemberPath,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    StringComparison.Ordinal))
            {
                throw new StructuredInformationRepositoryException(
                    "The working-state manifest source references do not match the published Database snapshot.");
            }
        }
    }

    private static async Task ValidateIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new StructuredInformationRepositoryException(
                "The working-state SQLite snapshot failed integrity validation.");
        }
    }

    private static bool GenerationsAgree(
        DatabaseGenerationSummary? expected,
        DatabaseGenerationSummary? actual) =>
        expected is null
            ? actual is null
            : DatabaseGenerationSnapshotComparer.AreEquivalent(expected, actual);

    private static SqliteConnection CreateExternalConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Pooling = false
        };
        return new SqliteConnection(builder.ToString());
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
