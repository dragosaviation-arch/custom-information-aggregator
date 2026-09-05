using System.Globalization;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed class StructuredInformationRepository
{
    public const int CurrentSchemaVersion = 1;
    public const string DatabaseFileName = "cia.sqlite3";

    private const string OccurrencesTableName = "indexed_occurrences";
    private const string TagIndexName = "ix_indexed_occurrences_tag";
    private const string SourceIndexName = "ix_indexed_occurrences_source_id";
    private const string TagValueIndexName = "ix_indexed_occurrences_tag_value";

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _isInitialized;

    public StructuredInformationRepository(ApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        DatabasePath = Path.Combine(applicationPaths.DatabaseDirectory, DatabaseFileName);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isInitialized) != 0)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isInitialized != 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            await using var connection = CreateConnection(allowCreate: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnableWalAsync(connection, cancellationToken).ConfigureAwait(false);

            var version = await ReadSchemaVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (version > CurrentSchemaVersion)
            {
                throw new UnsupportedRepositorySchemaVersionException(
                    version,
                    CurrentSchemaVersion);
            }

            if (version == 0)
            {
                if (await ContainsApplicationSchemaAsync(connection, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new StructuredInformationRepositoryException(
                        "The unversioned repository is not empty and cannot be initialized safely.");
                }

                version = await ApplyPendingMigrationsAsync(
                    connection,
                    version,
                    cancellationToken).ConfigureAwait(false);
            }

            if (version != CurrentSchemaVersion)
            {
                throw new StructuredInformationRepositoryException(
                    $"The repository schema version {version} is not supported.");
            }

            await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _isInitialized, 1);
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The structured information repository could not be initialized.",
                exception);
        }
        catch (IOException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The structured information repository storage could not be initialized.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The structured information repository storage is not accessible.",
                exception);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task AddBatchAsync(
        IReadOnlyCollection<IndexedOccurrence> occurrences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (occurrences.Count == 0)
        {
            return;
        }

        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"INSERT INTO {OccurrencesTableName} (tag, value, source_id) " +
                    "VALUES ($tag, $value, $sourceId);";

                var tagParameter = command.Parameters.Add("$tag", SqliteType.Text);
                var valueParameter = command.Parameters.Add("$value", SqliteType.Text);
                var sourceIdParameter = command.Parameters.Add("$sourceId", SqliteType.Text);

                foreach (var occurrence in occurrences)
                {
                    ArgumentNullException.ThrowIfNull(occurrence);
                    tagParameter.Value = occurrence.Tag;
                    valueParameter.Value = occurrence.Value;
                    sourceIdParameter.Value = occurrence.SourceId.ToString();
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The occurrence batch could not be committed.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public Task<IReadOnlyList<IndexedOccurrence>> QueryByTagAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        return QueryAsync(
            "WHERE tag = $tag",
            command => command.Parameters.AddWithValue("$tag", tag),
            cancellationToken);
    }

    public Task<IReadOnlyList<IndexedOccurrence>> QueryBySourceIdAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        EnsureValidSourceId(sourceId);
        return QueryAsync(
            "WHERE source_id = $sourceId",
            command => command.Parameters.AddWithValue("$sourceId", sourceId.ToString()),
            cancellationToken);
    }

    public Task<IReadOnlyList<IndexedOccurrence>> QueryByTagAndValueAsync(
        string tag,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(value);
        return QueryAsync(
            "WHERE tag = $tag AND value = $value",
            command =>
            {
                command.Parameters.AddWithValue("$tag", tag);
                command.Parameters.AddWithValue("$value", value);
            },
            cancellationToken);
    }

    private static async Task EnableWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        if (!string.Equals(result, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new StructuredInformationRepositoryException(
                "The repository could not enable WAL journal mode.");
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ContainsApplicationSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(" +
            "SELECT 1 FROM sqlite_schema " +
            "WHERE name NOT LIKE 'sqlite_%'" +
            ");";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<int> ApplyPendingMigrationsAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        while (version < CurrentSchemaVersion)
        {
            version = version switch
            {
                0 => await ApplyVersionOneAsync(connection, cancellationToken).ConfigureAwait(false),
                _ => throw new StructuredInformationRepositoryException(
                    $"No repository migration is available from schema version {version}.")
            };
        }

        return version;
    }

    private static async Task<int> ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                CREATE TABLE {OccurrencesTableName} (
                    occurrence_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    tag TEXT NOT NULL CHECK (length(tag) > 0),
                    value TEXT NOT NULL,
                    source_id TEXT NOT NULL CHECK (length(source_id) = 36)
                );
                CREATE INDEX {TagIndexName}
                    ON {OccurrencesTableName} (tag);
                CREATE INDEX {SourceIndexName}
                    ON {OccurrencesTableName} (source_id);
                CREATE INDEX {TagValueIndexName}
                    ON {OccurrencesTableName} (tag, value);
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ValidateCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT occurrence_id, tag, value, source_id FROM {OccurrencesTableName} LIMIT 0;";

            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                throw new StructuredInformationRepositoryException(
                    "The repository schema is incomplete or invalid.",
                    exception);
            }
        }

        string[] requiredIndexes = [TagIndexName, SourceIndexName, TagValueIndexName];
        foreach (var indexName in requiredIndexes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(" +
                "SELECT 1 FROM sqlite_schema WHERE type = 'index' AND name = $name" +
                ");";
            command.Parameters.AddWithValue("$name", indexName);
            var exists = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0;

            if (!exists)
            {
                throw new StructuredInformationRepositoryException(
                    $"The repository schema is missing the required index '{indexName}'.");
            }
        }
    }

    private async Task<IReadOnlyList<IndexedOccurrence>> QueryAsync(
        string predicate,
        Action<SqliteCommand> configureCommand,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT tag, value, source_id FROM {OccurrencesTableName} " +
                predicate +
                " ORDER BY occurrence_id;";
            configureCommand(command);

            var results = new List<IndexedOccurrence>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceIdValue = reader.GetString(2);
                if (!Guid.TryParseExact(sourceIdValue, "D", out var sourceId))
                {
                    throw new StructuredInformationRepositoryException(
                        "The repository contains an invalid source ID.");
                }

                results.Add(new IndexedOccurrence(
                    reader.GetString(0),
                    reader.GetString(1),
                    SourceId.From(sourceId)));
            }

            return results;
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The structured information query could not be completed.",
                exception);
        }
    }

    private SqliteConnection CreateConnection(bool allowCreate)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = true
        };

        return new SqliteConnection(connectionString.ToString());
    }

    private static void EnsureValidSourceId(SourceId sourceId)
    {
        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source ID is required.", nameof(sourceId));
        }
    }
}
