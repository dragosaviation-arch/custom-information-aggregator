using System.Collections.ObjectModel;
using System.Globalization;
using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    private const int CandidateGenerationState = 1;
    private const int PublishedGenerationState = 2;

    public async Task<DatabaseCandidate> CreateDatabaseCandidateAsync(
        OperationCorrelation correlation,
        DatabaseMappingSnapshot mapping,
        IReadOnlyList<SourceId> sourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(sourceIds);

        if (correlation.OperationId.Value == Guid.Empty
            || correlation.OperationId.Value.Version != 7)
        {
            throw new ArgumentException(
                "A Database candidate requires a UUIDv7 Operation ID.",
                nameof(correlation));
        }

        if (correlation.InitiatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A Database candidate initiation timestamp must be UTC.",
                nameof(correlation));
        }

        if (mapping.Columns.Count == 0)
        {
            throw new ArgumentException(
                "A Database candidate requires at least one mapped column.",
                nameof(mapping));
        }

        var sources = sourceIds.ToArray();
        if (sources.Length == 0
            || sources.Any(sourceId => sourceId.Value == Guid.Empty)
            || sources.Distinct().Count() != sources.Length)
        {
            throw new ArgumentException(
                "A Database candidate requires unique valid source IDs.",
                nameof(sourceIds));
        }

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
                var generationId = correlation.OperationId.ToString();
                await ExecuteGenerationInsertAsync(
                        connection,
                        transaction,
                        generationId,
                        correlation,
                        mapping,
                        sources,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new DatabaseCandidate(correlation, mapping, sources);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The Database candidate could not be created.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<bool> WriteDatabaseCandidateSourceAsync(
        DatabaseCandidate candidate,
        SourceId sourceId,
        Func<DatabaseCandidateValueWriter, CancellationToken, Task<bool>> writeSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(writeSource);

        if (!candidate.SourceIds.Contains(sourceId))
        {
            throw new ArgumentException(
                "The source is not part of this Database candidate.",
                nameof(sourceId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var writer = await DatabaseCandidateValueWriter
                .CreateAsync(connection, transaction, candidate, sourceId, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!await writeSource(writer, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
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
                "A Database candidate source could not be committed.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<DatabaseGenerationSummary> ValidateAndPublishDatabaseCandidateAsync(
        DatabaseCandidate candidate,
        CancellationToken cancellationToken = default,
        Func<bool>? tryEnterPublicationBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
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
                var generationId = candidate.Correlation.OperationId.ToString();
                await ValidateCandidateIdentityAsync(
                        connection,
                        transaction,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);
                await ValidateCandidateMappingAsync(
                        connection,
                        transaction,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);

                var valueCount = await ReadCandidateValueCountAsync(
                        connection,
                        transaction,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (valueCount < 1)
                {
                    throw new StructuredInformationRepositoryException(
                        "An empty Database candidate cannot be published.");
                }

                await EnsureCandidateIntegrityAsync(
                        connection,
                        transaction,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (tryEnterPublicationBoundary is not null
                    && !tryEnterPublicationBoundary())
                {
                    throw new OperationCanceledException(
                        "Database publication was cancelled before its atomic commit boundary.",
                        cancellationToken);
                }

                var publicationToken = tryEnterPublicationBoundary is null
                    ? cancellationToken
                    : CancellationToken.None;

                var previousGenerationId = await ReadPublishedGenerationIdAsync(
                        connection,
                        transaction,
                        publicationToken)
                    .ConfigureAwait(false);
                await PublishCandidatePointerAsync(
                        connection,
                        transaction,
                        generationId,
                        publicationToken)
                    .ConfigureAwait(false);

                if (previousGenerationId is not null
                    && !string.Equals(previousGenerationId, generationId, StringComparison.Ordinal))
                {
                    await DeleteGenerationAsync(
                            connection,
                            transaction,
                            previousGenerationId,
                            publicationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return new DatabaseGenerationSummary(
                    candidate.Correlation.OperationId,
                    candidate.Mapping,
                    valueCount);
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
                "The Database candidate could not be validated and published.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task DiscardDatabaseCandidateAsync(
        DatabaseCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM database_generations " +
                "WHERE generation_id = $generationId AND generation_state = $candidateState;";
            command.Parameters.AddWithValue(
                "$generationId",
                candidate.Correlation.OperationId.ToString());
            command.Parameters.AddWithValue("$candidateState", CandidateGenerationState);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The discarded Database candidate could not be removed.",
                exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<DatabaseGenerationSummary?> ReadPublishedDatabaseGenerationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var generationId = await ReadPublishedGenerationIdAsync(
                connection,
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (generationId is null)
        {
            return null;
        }

        var operationId = ParseOperationId(generationId);
        var mapping = await ReadMappingAsync(
                connection,
                transaction: null,
                generationId,
                cancellationToken)
            .ConfigureAwait(false);
        var valueCount = await ReadCandidateValueCountAsync(
                connection,
                transaction: null,
                generationId,
                cancellationToken)
            .ConfigureAwait(false);
        return new DatabaseGenerationSummary(operationId, mapping, valueCount);
    }

    public async Task<IReadOnlyList<MappedDatabaseValue>> QueryPublishedDatabaseValuesAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                values_table.database_tag_name,
                values_table.source_information_type,
                values_table.value,
                values_table.source_id
            FROM database_publication AS publication
            JOIN database_values AS values_table
                ON values_table.generation_id = publication.generation_id
            WHERE publication.singleton_id = 1
            ORDER BY values_table.value_ordinal;
            """;

        var values = new List<MappedDatabaseValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(3), "D", out var sourceId))
            {
                throw new StructuredInformationRepositoryException(
                    "The published Database contains an invalid Source ID.");
            }

            values.Add(new MappedDatabaseValue(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                SourceId.From(sourceId)));
        }

        return values;
    }

    public async Task<DatabaseReviewPage?> ReadPublishedDatabasePageAsync(
        OperationId generationId,
        int startRowOrdinal,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        if (generationId.Value == Guid.Empty || generationId.Value.Version != 7)
        {
            throw new ArgumentException(
                "A Database review request requires a UUIDv7 generation ID.",
                nameof(generationId));
        }

        if (startRowOrdinal < 1
            || startRowOrdinal > int.MaxValue - DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(startRowOrdinal));
        }

        if (rowCount is < 1 or > DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var publishedGenerationId = await ReadPublishedGenerationIdAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    publishedGenerationId,
                    generationId.ToString(),
                    StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var orderedColumnNames = new List<string>();
            var totalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var valuesByColumn = new Dictionary<string, List<DatabaseReviewValue>>(
                StringComparer.Ordinal);
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.Transaction = transaction;
                columnCommand.CommandText = """
                    SELECT
                        columns.database_tag_name,
                        COUNT(values_table.value_ordinal)
                    FROM database_columns AS columns
                    LEFT JOIN database_values AS values_table
                        ON values_table.generation_id = columns.generation_id
                        AND values_table.database_tag_name = columns.database_tag_name
                    WHERE columns.generation_id = $generationId
                    GROUP BY columns.column_ordinal, columns.database_tag_name
                    ORDER BY columns.column_ordinal;
                    """;
                columnCommand.Parameters.AddWithValue(
                    "$generationId",
                    generationId.ToString());
                await using var reader = await columnCommand
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var databaseTagName = reader.GetString(0);
                    var totalCount = checked((int)reader.GetInt64(1));
                    orderedColumnNames.Add(databaseTagName);
                    totalCounts.Add(databaseTagName, totalCount);
                    valuesByColumn.Add(databaseTagName, []);
                }
            }

            await using (var valueCommand = connection.CreateCommand())
            {
                valueCommand.Transaction = transaction;
                valueCommand.CommandText = """
                    WITH ordered_values AS (
                        SELECT
                            columns.column_ordinal,
                            values_table.database_tag_name,
                            values_table.source_information_type,
                            values_table.value,
                            values_table.source_id,
                            ROW_NUMBER() OVER (
                                PARTITION BY values_table.database_tag_name
                                ORDER BY values_table.value_ordinal) AS column_value_ordinal
                        FROM database_columns AS columns
                        JOIN database_values AS values_table
                            ON values_table.generation_id = columns.generation_id
                            AND values_table.database_tag_name = columns.database_tag_name
                        WHERE columns.generation_id = $generationId
                    )
                    SELECT
                        database_tag_name,
                        source_information_type,
                        value,
                        source_id,
                        column_value_ordinal
                    FROM ordered_values
                    WHERE column_value_ordinal >= $startRowOrdinal
                        AND column_value_ordinal < $endRowOrdinal
                    ORDER BY column_ordinal, column_value_ordinal;
                    """;
                valueCommand.Parameters.AddWithValue(
                    "$generationId",
                    generationId.ToString());
                valueCommand.Parameters.AddWithValue("$startRowOrdinal", startRowOrdinal);
                valueCommand.Parameters.AddWithValue(
                    "$endRowOrdinal",
                    startRowOrdinal + rowCount);
                await using var reader = await valueCommand
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var databaseTagName = reader.GetString(0);
                    if (!Guid.TryParseExact(reader.GetString(3), "D", out var sourceId))
                    {
                        throw new StructuredInformationRepositoryException(
                            "The published Database contains an invalid Source ID.");
                    }

                    valuesByColumn[databaseTagName].Add(new DatabaseReviewValue(
                        checked((int)reader.GetInt64(4)),
                        reader.GetString(2),
                        reader.GetString(1),
                        SourceId.From(sourceId)));
                }
            }

            var columns = orderedColumnNames.Select(databaseTagName =>
                new DatabaseReviewColumn(
                    databaseTagName,
                    totalCounts[databaseTagName],
                    valuesByColumn[databaseTagName])).ToArray();
            var totalMappedValueCount = checked(totalCounts.Values.Sum());
            var page = new DatabaseReviewPage(
                generationId,
                startRowOrdinal,
                rowCount,
                totalMappedValueCount,
                columns);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return page;
        }
        catch (StructuredInformationRepositoryException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The published Database review page could not be read.",
                exception);
        }
    }

    private static async Task ExecuteGenerationInsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        OperationCorrelation correlation,
        DatabaseMappingSnapshot mapping,
        IReadOnlyList<SourceId> sourceIds,
        CancellationToken cancellationToken)
    {
        await using (var generationCommand = connection.CreateCommand())
        {
            generationCommand.Transaction = transaction;
            generationCommand.CommandText = """
                INSERT INTO database_generations (
                    generation_id,
                    operation_id,
                    created_utc,
                    generation_state)
                VALUES ($generationId, $operationId, $createdUtc, $state);
                """;
            generationCommand.Parameters.AddWithValue("$generationId", generationId);
            generationCommand.Parameters.AddWithValue(
                "$operationId",
                correlation.OperationId.ToString());
            generationCommand.Parameters.AddWithValue(
                "$createdUtc",
                correlation.InitiatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            generationCommand.Parameters.AddWithValue("$state", CandidateGenerationState);
            await generationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var sourceOrdinal = 0; sourceOrdinal < sourceIds.Count; sourceOrdinal++)
        {
            await using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = """
                INSERT INTO database_generation_sources (
                    generation_id,
                    source_ordinal,
                    source_id)
                VALUES ($generationId, $sourceOrdinal, $sourceId);
                """;
            sourceCommand.Parameters.AddWithValue("$generationId", generationId);
            sourceCommand.Parameters.AddWithValue("$sourceOrdinal", sourceOrdinal);
            sourceCommand.Parameters.AddWithValue("$sourceId", sourceIds[sourceOrdinal].ToString());
            await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var columnOrdinal = 0; columnOrdinal < mapping.Columns.Count; columnOrdinal++)
        {
            var column = mapping.Columns[columnOrdinal];
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.Transaction = transaction;
                columnCommand.CommandText = """
                    INSERT INTO database_columns (
                        generation_id,
                        column_ordinal,
                        database_tag_name)
                    VALUES ($generationId, $columnOrdinal, $databaseTagName);
                    """;
                columnCommand.Parameters.AddWithValue("$generationId", generationId);
                columnCommand.Parameters.AddWithValue("$columnOrdinal", columnOrdinal);
                columnCommand.Parameters.AddWithValue(
                    "$databaseTagName",
                    column.DatabaseTagName);
                await columnCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var sourceInformationOrdinal = 0;
                 sourceInformationOrdinal < column.SourceInformationTypes.Count;
                 sourceInformationOrdinal++)
            {
                var sourceInformationType = column.SourceInformationTypes[sourceInformationOrdinal];
                await using var mappingCommand = connection.CreateCommand();
                mappingCommand.Transaction = transaction;
                mappingCommand.CommandText = """
                    INSERT INTO database_column_sources (
                        generation_id,
                        database_tag_name,
                        source_information_ordinal,
                        source_information_type)
                    VALUES (
                        $generationId,
                        $databaseTagName,
                        $sourceInformationOrdinal,
                        $sourceInformationType);
                    """;
                mappingCommand.Parameters.AddWithValue("$generationId", generationId);
                mappingCommand.Parameters.AddWithValue(
                    "$databaseTagName",
                    column.DatabaseTagName);
                mappingCommand.Parameters.AddWithValue(
                    "$sourceInformationOrdinal",
                    sourceInformationOrdinal);
                mappingCommand.Parameters.AddWithValue(
                    "$sourceInformationType",
                    sourceInformationType);
                await mappingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ValidateCandidateIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, generation_state
            FROM database_generations
            WHERE generation_id = $generationId;
            """;
        command.Parameters.AddWithValue(
            "$generationId",
            candidate.Correlation.OperationId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(
                reader.GetString(0),
                candidate.Correlation.OperationId.ToString(),
                StringComparison.Ordinal)
            || reader.GetInt32(1) != CandidateGenerationState)
        {
            throw new StructuredInformationRepositoryException(
                "The Database candidate does not belong to the initiating operation or is not publishable.");
        }
    }

    private static async Task ValidateCandidateMappingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var storedMapping = await ReadMappingAsync(
                connection,
                transaction,
                candidate.Correlation.OperationId.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        if (!MappingsEqual(candidate.Mapping, storedMapping))
        {
            throw new StructuredInformationRepositoryException(
                "The Database candidate schema does not match its captured build configuration.");
        }

        await using var sourceCommand = connection.CreateCommand();
        sourceCommand.Transaction = transaction;
        sourceCommand.CommandText = """
            SELECT source_id
            FROM database_generation_sources
            WHERE generation_id = $generationId
            ORDER BY source_ordinal;
            """;
        sourceCommand.Parameters.AddWithValue(
            "$generationId",
            candidate.Correlation.OperationId.ToString());
        var storedSources = new List<SourceId>();
        await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var sourceId))
            {
                throw new StructuredInformationRepositoryException(
                    "The Database candidate contains invalid source provenance.");
            }

            storedSources.Add(SourceId.From(sourceId));
        }

        if (!candidate.SourceIds.SequenceEqual(storedSources))
        {
            throw new StructuredInformationRepositoryException(
                "The Database candidate source snapshot does not match its initiating operation.");
        }
    }

    private static async Task EnsureCandidateIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using (var invalidValueCommand = connection.CreateCommand())
        {
            invalidValueCommand.Transaction = transaction;
            invalidValueCommand.CommandText = """
                SELECT COUNT(*)
                FROM database_values AS values_table
                LEFT JOIN database_column_sources AS mapping
                    ON mapping.generation_id = values_table.generation_id
                    AND mapping.source_information_type = values_table.source_information_type
                    AND mapping.database_tag_name = values_table.database_tag_name
                LEFT JOIN database_generation_sources AS sources
                    ON sources.generation_id = values_table.generation_id
                    AND sources.source_id = values_table.source_id
                WHERE values_table.generation_id = $generationId
                    AND (mapping.generation_id IS NULL OR sources.generation_id IS NULL);
                """;
            invalidValueCommand.Parameters.AddWithValue("$generationId", generationId);
            if (Convert.ToInt32(
                    await invalidValueCommand.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 0)
            {
                throw new StructuredInformationRepositoryException(
                    "The Database candidate contains values outside its mapped schema or source snapshot.");
            }
        }

        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.Transaction = transaction;
        integrityCommand.CommandText = "PRAGMA integrity_check;";
        var integrity = Convert.ToString(
            await integrityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new StructuredInformationRepositoryException(
                "The Database candidate failed repository integrity validation.");
        }
    }

    private static async Task PublishCandidatePointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText = """
                UPDATE database_generations
                SET generation_state = $publishedState
                WHERE generation_id = $generationId
                    AND generation_state = $candidateState;
                """;
            stateCommand.Parameters.AddWithValue("$publishedState", PublishedGenerationState);
            stateCommand.Parameters.AddWithValue("$generationId", generationId);
            stateCommand.Parameters.AddWithValue("$candidateState", CandidateGenerationState);
            if (await stateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new StructuredInformationRepositoryException(
                    "The Database candidate is no longer available for publication.");
            }
        }

        await using var publicationCommand = connection.CreateCommand();
        publicationCommand.Transaction = transaction;
        publicationCommand.CommandText = """
            INSERT INTO database_publication (singleton_id, generation_id)
            VALUES (1, $generationId)
            ON CONFLICT(singleton_id) DO UPDATE SET generation_id = excluded.generation_id;
            """;
        publicationCommand.Parameters.AddWithValue("$generationId", generationId);
        await publicationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM database_generations WHERE generation_id = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadPublishedGenerationIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT generation_id FROM database_publication WHERE singleton_id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadCandidateValueCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM database_values WHERE generation_id = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<DatabaseMappingSnapshot> ReadMappingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT columns.database_tag_name, sources.source_information_type
            FROM database_columns AS columns
            JOIN database_column_sources AS sources
                ON sources.generation_id = columns.generation_id
                AND sources.database_tag_name = columns.database_tag_name
            WHERE columns.generation_id = $generationId
            ORDER BY columns.column_ordinal, sources.source_information_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);

        var columns = new List<DatabaseColumnMapping>();
        string? currentName = null;
        var currentSources = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var databaseTagName = reader.GetString(0);
            if (currentName is not null
                && !string.Equals(currentName, databaseTagName, StringComparison.Ordinal))
            {
                columns.Add(new DatabaseColumnMapping(currentName, currentSources.ToArray()));
                currentSources.Clear();
            }

            currentName = databaseTagName;
            currentSources.Add(reader.GetString(1));
        }

        if (currentName is not null)
        {
            columns.Add(new DatabaseColumnMapping(currentName, currentSources.ToArray()));
        }

        return new DatabaseMappingSnapshot(columns);
    }

    private static bool MappingsEqual(
        DatabaseMappingSnapshot expected,
        DatabaseMappingSnapshot actual)
    {
        return expected.Columns.Count == actual.Columns.Count
            && expected.Columns.Zip(actual.Columns).All(pair =>
                string.Equals(
                    pair.First.DatabaseTagName,
                    pair.Second.DatabaseTagName,
                    StringComparison.Ordinal)
                && pair.First.SourceInformationTypes.SequenceEqual(
                    pair.Second.SourceInformationTypes,
                    StringComparer.Ordinal));
    }

    private async Task<SqliteConnection> OpenGenerationConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = CreateConnection(allowCreate: false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static OperationId ParseOperationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var operationId)
            || operationId == Guid.Empty
            || operationId.Version != 7)
        {
            throw new StructuredInformationRepositoryException(
                "The published Database has an invalid Operation ID.");
        }

        return OperationId.From(operationId);
    }
}

public sealed class DatabaseCandidate
{
    internal DatabaseCandidate(
        OperationCorrelation correlation,
        DatabaseMappingSnapshot mapping,
        IReadOnlyList<SourceId> sourceIds)
    {
        Correlation = correlation;
        Mapping = mapping;
        SourceIds = new ReadOnlyCollection<SourceId>(sourceIds.ToArray());
    }

    public OperationCorrelation Correlation { get; }

    public DatabaseMappingSnapshot Mapping { get; }

    public IReadOnlyList<SourceId> SourceIds { get; }
}

public sealed class DatabaseCandidateValueWriter : IAsyncDisposable
{
    private readonly DatabaseCandidate _candidate;
    private readonly SourceId _sourceId;
    private readonly SqliteCommand _insertCommand;
    private long _nextOrdinal;

    private DatabaseCandidateValueWriter(
        DatabaseCandidate candidate,
        SourceId sourceId,
        SqliteCommand insertCommand,
        long nextOrdinal)
    {
        _candidate = candidate;
        _sourceId = sourceId;
        _insertCommand = insertCommand;
        _nextOrdinal = nextOrdinal;
    }

    internal static async Task<DatabaseCandidateValueWriter> CreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseCandidate candidate,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        await using var ordinalCommand = connection.CreateCommand();
        ordinalCommand.Transaction = transaction;
        ordinalCommand.CommandText = """
            SELECT COALESCE(MAX(value_ordinal) + 1, 0)
            FROM database_values
            WHERE generation_id = $generationId;
            """;
        ordinalCommand.Parameters.AddWithValue(
            "$generationId",
            candidate.Correlation.OperationId.ToString());
        var nextOrdinal = Convert.ToInt64(
            await ordinalCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO database_values (
                generation_id,
                value_ordinal,
                database_tag_name,
                source_information_type,
                value,
                source_id)
            VALUES (
                $generationId,
                $valueOrdinal,
                $databaseTagName,
                $sourceInformationType,
                $value,
                $sourceId);
            """;
        insertCommand.Parameters.Add("$generationId", SqliteType.Text);
        insertCommand.Parameters.Add("$valueOrdinal", SqliteType.Integer);
        insertCommand.Parameters.Add("$databaseTagName", SqliteType.Text);
        insertCommand.Parameters.Add("$sourceInformationType", SqliteType.Text);
        insertCommand.Parameters.Add("$value", SqliteType.Text);
        insertCommand.Parameters.Add("$sourceId", SqliteType.Text);
        return new DatabaseCandidateValueWriter(
            candidate,
            sourceId,
            insertCommand,
            nextOrdinal);
    }

    public async ValueTask AddBatchAsync(
        IReadOnlyList<MappedDatabaseValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.SourceId != _sourceId)
            {
                throw new StructuredInformationRepositoryException(
                    "A Database candidate value has source provenance outside its source transaction.");
            }

            var mappedColumn = _candidate.Mapping.Columns.SingleOrDefault(
                column => column.SourceInformationTypes.Contains(
                    value.SourceInformationType,
                    StringComparer.Ordinal));
            if (mappedColumn is null
                || !string.Equals(
                    mappedColumn.DatabaseTagName,
                    value.DatabaseTagName,
                    StringComparison.Ordinal))
            {
                throw new StructuredInformationRepositoryException(
                    "A Database candidate value is outside its captured mapped schema.");
            }

            _insertCommand.Parameters["$generationId"].Value =
                _candidate.Correlation.OperationId.ToString();
            _insertCommand.Parameters["$valueOrdinal"].Value = _nextOrdinal++;
            _insertCommand.Parameters["$databaseTagName"].Value = value.DatabaseTagName;
            _insertCommand.Parameters["$sourceInformationType"].Value =
                value.SourceInformationType;
            _insertCommand.Parameters["$value"].Value = value.Value;
            _insertCommand.Parameters["$sourceId"].Value = value.SourceId.ToString();
            await _insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _insertCommand.DisposeAsync();
    }
}
