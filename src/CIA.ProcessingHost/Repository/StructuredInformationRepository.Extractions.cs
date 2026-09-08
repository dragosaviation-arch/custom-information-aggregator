using System.Globalization;
using System.Runtime.CompilerServices;
using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    public async Task<ExtractionResultSummary> ExtractPublishedDatabaseAsync(
        OperationCorrelation extractionCorrelation,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken = default,
        Func<bool>? tryEnterPublicationBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(extractionCorrelation);
        ArgumentNullException.ThrowIfNull(databaseGeneration);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var extractionId = extractionCorrelation.OperationId.ToString();
                var generationId = databaseGeneration.OperationId.ToString();
                var publishedGenerationId = await ReadPublishedGenerationIdAsync(
                        connection,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(publishedGenerationId, generationId, StringComparison.Ordinal))
                {
                    throw new StructuredInformationRepositoryException(
                        "Extraction requires the captured Database generation to remain published and current.");
                }

                var storedMapping = await ReadMappingAsync(
                        connection,
                        transaction,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                var storedValueCount = await ReadCandidateValueCountAsync(
                        connection,
                        transaction,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!MappingsEqual(databaseGeneration.Mapping, storedMapping)
                    || databaseGeneration.ValueCount != storedValueCount)
                {
                    throw new StructuredInformationRepositoryException(
                        "The captured Database generation does not match its published repository content.");
                }

                await InsertExtractionResultAsync(
                        connection,
                        transaction,
                        extractionId,
                        generationId,
                        extractionCorrelation,
                        cancellationToken)
                    .ConfigureAwait(false);
                await CopyExtractionSchemaAsync(
                        connection,
                        transaction,
                        extractionId,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                var copiedValueCount = await CopyExtractionValuesAsync(
                        connection,
                        transaction,
                        extractionId,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (copiedValueCount != databaseGeneration.ValueCount)
                {
                    throw new StructuredInformationRepositoryException(
                        "The Extraction Result does not contain the complete captured Database generation.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (tryEnterPublicationBoundary is not null
                    && !tryEnterPublicationBoundary())
                {
                    throw new OperationCanceledException(
                        "Extraction was cancelled before its atomic publication boundary.",
                        cancellationToken);
                }

                var publicationToken = tryEnterPublicationBoundary is null
                    ? cancellationToken
                    : CancellationToken.None;
                var previousExtractionId = await ReadPublishedExtractionIdAsync(
                        connection,
                        transaction,
                        publicationToken)
                    .ConfigureAwait(false);
                await PublishExtractionPointerAsync(
                        connection,
                        transaction,
                        extractionId,
                        publicationToken)
                    .ConfigureAwait(false);

                if (previousExtractionId is not null
                    && !string.Equals(previousExtractionId, extractionId, StringComparison.Ordinal))
                {
                    await DeleteExtractionAsync(
                            connection,
                            transaction,
                            previousExtractionId,
                            publicationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new ExtractionResultSummary(
                    extractionCorrelation.OperationId,
                    databaseGeneration);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The Extraction Result could not be created or published.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<ExtractionResultSummary?> ReadPublishedExtractionResultAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var extractionId = await ReadPublishedExtractionIdAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (extractionId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var (databaseGenerationId, mapping, valueCount) = await ReadExtractionBasisAsync(
                connection,
                transaction,
                extractionId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExtractionResultSummary(
            ParseOperationId(extractionId),
            new DatabaseGenerationSummary(databaseGenerationId, mapping, valueCount));
    }

    public async IAsyncEnumerable<ExtractionResultValue> StreamPublishedExtractionValuesAsync(
        OperationId extractionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (extractionId.Value == Guid.Empty || extractionId.Value.Version != 7)
        {
            throw new ArgumentException(
                "An Extraction Result read requires a UUIDv7 Operation ID.",
                nameof(extractionId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var publishedExtractionId = await ReadPublishedExtractionIdAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                publishedExtractionId,
                extractionId.ToString(),
                StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            yield break;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT database_field_name, source_information_type, value, source_id
            FROM extraction_values
            WHERE extraction_id = $extractionId
            ORDER BY value_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(3), "D", out var sourceId))
            {
                throw new StructuredInformationRepositoryException(
                    "The Extraction Result contains invalid source provenance.");
            }

            yield return new ExtractionResultValue(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                SourceId.From(sourceId));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertExtractionResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        string generationId,
        OperationCorrelation correlation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_results (
                extraction_id,
                database_generation_id,
                created_utc)
            VALUES ($extractionId, $generationId, $createdUtc);
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue(
            "$createdUtc",
            correlation.InitiatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyExtractionSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.Transaction = transaction;
            columnsCommand.CommandText = """
                INSERT INTO extraction_columns (
                    extraction_id,
                    column_ordinal,
                    database_field_name)
                SELECT $extractionId, column_ordinal, database_tag_name
                FROM database_columns
                WHERE generation_id = $generationId
                ORDER BY column_ordinal;
                """;
            columnsCommand.Parameters.AddWithValue("$extractionId", extractionId);
            columnsCommand.Parameters.AddWithValue("$generationId", generationId);
            await columnsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var sourcesCommand = connection.CreateCommand();
        sourcesCommand.Transaction = transaction;
        sourcesCommand.CommandText = """
            INSERT INTO extraction_column_sources (
                extraction_id,
                database_field_name,
                source_information_ordinal,
                source_information_type)
            SELECT
                $extractionId,
                database_tag_name,
                source_information_ordinal,
                source_information_type
            FROM database_column_sources
            WHERE generation_id = $generationId
            ORDER BY database_tag_name, source_information_ordinal;
            """;
        sourcesCommand.Parameters.AddWithValue("$extractionId", extractionId);
        sourcesCommand.Parameters.AddWithValue("$generationId", generationId);
        await sourcesCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CopyExtractionValuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_values (
                extraction_id,
                value_ordinal,
                database_field_name,
                source_information_type,
                value,
                source_id)
            SELECT
                $extractionId,
                value_ordinal,
                database_tag_name,
                source_information_type,
                value,
                source_id
            FROM database_values
            WHERE generation_id = $generationId
            ORDER BY value_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$generationId", generationId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadPublishedExtractionIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT extraction_id FROM extraction_publication WHERE singleton_id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task PublishExtractionPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO extraction_publication (singleton_id, extraction_id)
            VALUES (1, $extractionId)
            ON CONFLICT(singleton_id) DO UPDATE SET extraction_id = excluded.extraction_id;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM extraction_results WHERE extraction_id = $extractionId;";
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(
        OperationId DatabaseGenerationId,
        DatabaseMappingSnapshot Mapping,
        int ValueCount)> ReadExtractionBasisAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string extractionId,
            CancellationToken cancellationToken)
    {
        string? generationId;
        await using (var generationCommand = connection.CreateCommand())
        {
            generationCommand.Transaction = transaction;
            generationCommand.CommandText = """
                SELECT database_generation_id
                FROM extraction_results
                WHERE extraction_id = $extractionId;
                """;
            generationCommand.Parameters.AddWithValue("$extractionId", extractionId);
            generationId = Convert.ToString(
                await generationCommand.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(generationId))
        {
            throw new StructuredInformationRepositoryException(
                "The published Extraction Result basis is unavailable.");
        }

        var columns = new List<DatabaseColumnMapping>();
        await using (var mappingCommand = connection.CreateCommand())
        {
            mappingCommand.Transaction = transaction;
            mappingCommand.CommandText = """
                SELECT columns.database_field_name, sources.source_information_type
                FROM extraction_columns AS columns
                JOIN extraction_column_sources AS sources
                    ON sources.extraction_id = columns.extraction_id
                    AND sources.database_field_name = columns.database_field_name
                WHERE columns.extraction_id = $extractionId
                ORDER BY columns.column_ordinal, sources.source_information_ordinal;
                """;
            mappingCommand.Parameters.AddWithValue("$extractionId", extractionId);
            string? currentName = null;
            var currentSources = new List<string>();
            await using var reader = await mappingCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var databaseFieldName = reader.GetString(0);
                if (currentName is not null
                    && !string.Equals(currentName, databaseFieldName, StringComparison.Ordinal))
                {
                    columns.Add(new DatabaseColumnMapping(currentName, currentSources.ToArray()));
                    currentSources.Clear();
                }

                currentName = databaseFieldName;
                currentSources.Add(reader.GetString(1));
            }

            if (currentName is not null)
            {
                columns.Add(new DatabaseColumnMapping(currentName, currentSources.ToArray()));
            }
        }

        int valueCount;
        await using (var valueCountCommand = connection.CreateCommand())
        {
            valueCountCommand.Transaction = transaction;
            valueCountCommand.CommandText =
                "SELECT COUNT(*) FROM extraction_values WHERE extraction_id = $extractionId;";
            valueCountCommand.Parameters.AddWithValue("$extractionId", extractionId);
            valueCount = Convert.ToInt32(
                await valueCountCommand.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        return (
            ParseOperationId(generationId),
            new DatabaseMappingSnapshot(columns),
            valueCount);
    }
}
