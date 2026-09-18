using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
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
        if (!databaseGeneration.IsHierarchyAware)
        {
            throw new StructuredInformationRepositoryException(
                "Extraction requires a hierarchy-aware Database generation.");
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
                var extractionId = extractionCorrelation.OperationId.ToString();
                var generationId = databaseGeneration.OperationId.ToString();
                var publishedGenerationId = await ExtractionScalarStringAsync(
                        connection,
                        transaction,
                        "SELECT generation_id FROM hierarchy_database_publication WHERE singleton_id = 1;",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(publishedGenerationId, generationId, StringComparison.Ordinal))
                {
                    throw new StructuredInformationRepositoryException(
                        "Extraction requires the captured Database generation to remain published and current.");
                }

                var storedGeneration = await ReadHierarchySummaryAsync(
                        connection,
                        transaction,
                        databaseGeneration.OperationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!DatabaseGenerationSnapshotComparer.AreEquivalent(
                    databaseGeneration,
                    storedGeneration))
                {
                    throw new StructuredInformationRepositoryException(
                        "The captured hierarchy-aware Database generation does not match its repository snapshot.");
                }

                await InsertHierarchyExtractionResultAsync(
                        connection,
                        transaction,
                        extractionId,
                        databaseGeneration,
                        extractionCorrelation,
                        cancellationToken)
                    .ConfigureAwait(false);
                await CopyHierarchyExtractionSnapshotAsync(
                        connection,
                        transaction,
                        extractionId,
                        generationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                await ValidateHierarchyExtractionCandidateAsync(
                        connection,
                        transaction,
                        extractionId,
                        databaseGeneration,
                        cancellationToken)
                    .ConfigureAwait(false);

                var result = await ReadHierarchyExtractionSummaryAsync(
                        connection,
                        transaction,
                        extractionId,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new StructuredInformationRepositoryException(
                        "The hierarchy-aware Extraction candidate summary is incomplete.");

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
                var previousExtractionId = await ReadPublishedHierarchyExtractionIdAsync(
                        connection,
                        transaction,
                        publicationToken)
                    .ConfigureAwait(false);
                await PublishHierarchyExtractionPointerAsync(
                        connection,
                        transaction,
                        extractionId,
                        publicationToken)
                    .ConfigureAwait(false);
                if (previousExtractionId is not null
                    && !string.Equals(previousExtractionId, extractionId, StringComparison.Ordinal))
                {
                    await DeleteHierarchyExtractionAsync(
                            connection,
                            transaction,
                            previousExtractionId,
                            publicationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return result;
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
                "The hierarchy-aware Extraction Result could not be created or published.",
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
        var extractionId = await ReadPublishedHierarchyExtractionIdAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (extractionId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var result = await ReadHierarchyExtractionSummaryAsync(
                connection,
                transaction,
                extractionId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async IAsyncEnumerable<ExtractionResultRow> StreamPublishedExtractionRowsAsync(
        OperationId extractionId,
        SourceSetId sourceSetId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (extractionId.Value == Guid.Empty
            || extractionId.Value.Version != 7
            || sourceSetId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An Extraction Result row read requires valid extraction and Source Set identities.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var publishedExtractionId = await ReadPublishedHierarchyExtractionIdAsync(
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
            throw new StructuredInformationRepositoryException(
                "The requested Extraction Result is no longer the active published result.");
        }

        var lastRowOrdinal = 0;
        while (true)
        {
            ExtractionRowHeader? row = null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT row_ordinal, database_row_ordinal, source_row_ordinal,
                       record_identity, source_id, source_set_name, source_file_name,
                       full_source_path, source_kind, archive_path, archive_member_path,
                       file_modified_utc
                FROM hierarchy_extraction_rows
                WHERE extraction_id = $extractionId
                  AND source_set_id = $sourceSetId
                  AND row_ordinal > $lastRowOrdinal
                ORDER BY row_ordinal
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId.ToString());
            command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
            command.Parameters.AddWithValue("$lastRowOrdinal", lastRowOrdinal);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    row = new ExtractionRowHeader(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        new DatabaseSourceMetadata(
                            sourceSetId,
                            reader.GetString(5),
                            SourceId.From(Guid.Parse(reader.GetString(4))),
                            reader.GetString(6),
                            reader.GetString(7),
                            (LoadedSourceKind)reader.GetInt32(8),
                            reader.IsDBNull(9) ? null : reader.GetString(9),
                            reader.IsDBNull(10) ? null : reader.GetString(10),
                            reader.IsDBNull(11)
                                ? null
                                : DateTimeOffset.Parse(
                                    reader.GetString(11),
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind)));
                }
            }

            if (row is null)
            {
                break;
            }

            var cells = await ReadHierarchyExtractionCellsAsync(
                    connection,
                    transaction,
                    extractionId,
                    sourceSetId,
                    row.Ordinal,
                    row.Source.SourceId,
                    cancellationToken)
                .ConfigureAwait(false);
            yield return new ExtractionResultRow(
                row.Ordinal,
                row.DatabaseRowOrdinal,
                row.SourceRowOrdinal,
                row.RecordIdentity,
                row.Source,
                cells);
            lastRowOrdinal = row.Ordinal;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertHierarchyExtractionResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        DatabaseGenerationSummary databaseGeneration,
        OperationCorrelation correlation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO hierarchy_extraction_results (
                extraction_id,
                database_generation_id,
                database_generation_json,
                created_utc)
            VALUES ($extractionId, $generationId, $generationJson, $createdUtc);
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$generationId", databaseGeneration.OperationId.ToString());
        command.Parameters.AddWithValue("$generationJson", JsonSerializer.Serialize(databaseGeneration));
        command.Parameters.AddWithValue(
            "$createdUtc",
            correlation.InitiatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyHierarchyExtractionSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_extraction_datasets (
                extraction_id, source_set_id, display_name, dataset_ordinal,
                repeated_data_layout, row_count, value_count)
            SELECT $extractionId, source_set_id, display_name, dataset_ordinal,
                   repeated_data_layout, 0, 0
            FROM hierarchy_database_datasets
            WHERE generation_id = $generationId
            ORDER BY dataset_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_extraction_columns (
                extraction_id, source_set_id, column_ordinal, field_key,
                effective_name, repeat_coordinates_json)
            SELECT $extractionId, source_set_id, column_ordinal, field_key,
                   effective_name, repeat_coordinates_json
            FROM hierarchy_database_columns
            WHERE generation_id = $generationId
            ORDER BY source_set_id, column_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_extraction_rows (
                extraction_id, source_set_id, row_ordinal, database_row_ordinal,
                source_row_ordinal, record_identity, source_id, source_set_name,
                source_file_name, full_source_path, source_kind, archive_path,
                archive_member_path, file_modified_utc)
            SELECT
                $extractionId,
                rows.source_set_id,
                ROW_NUMBER() OVER (
                    PARTITION BY rows.source_set_id ORDER BY rows.row_ordinal),
                rows.row_ordinal,
                rows.source_row_ordinal,
                rows.record_identity,
                rows.source_id,
                datasets.display_name,
                sources.source_file_name,
                sources.full_source_path,
                sources.source_kind,
                sources.archive_path,
                sources.archive_member_path,
                sources.file_modified_utc
            FROM hierarchy_database_rows AS rows
            JOIN hierarchy_database_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.source_set_id = rows.source_set_id
            JOIN hierarchy_database_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.source_set_id = rows.source_set_id
             AND sources.source_id = rows.source_id
            WHERE rows.generation_id = $generationId AND rows.is_included = 1
            ORDER BY datasets.dataset_ordinal, rows.row_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_extraction_cells (
                extraction_id, source_set_id, row_ordinal, field_key,
                effective_name, repeat_coordinates_json, has_conflict)
            SELECT
                $extractionId,
                result_rows.source_set_id,
                result_rows.row_ordinal,
                cells.field_key,
                columns_table.effective_name,
                cells.repeat_coordinates_json,
                cells.has_conflict
            FROM hierarchy_extraction_rows AS result_rows
            JOIN hierarchy_database_cells AS cells
              ON cells.generation_id = $generationId
             AND cells.source_set_id = result_rows.source_set_id
             AND cells.row_ordinal = result_rows.database_row_ordinal
            JOIN hierarchy_database_columns AS columns_table
              ON columns_table.generation_id = cells.generation_id
             AND columns_table.source_set_id = cells.source_set_id
             AND columns_table.field_key = cells.field_key
             AND columns_table.repeat_coordinates_json = cells.repeat_coordinates_json
            WHERE result_rows.extraction_id = $extractionId
            ORDER BY result_rows.source_set_id, result_rows.row_ordinal,
                     columns_table.column_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_extraction_cell_values (
                extraction_id, source_set_id, row_ordinal, field_key,
                repeat_coordinates_json, value_ordinal, value, information_type,
                structural_path, candidate_kind, structural_identity, source_id,
                lineage_json)
            SELECT
                $extractionId,
                result_rows.source_set_id,
                result_rows.row_ordinal,
                values_table.field_key,
                values_table.repeat_coordinates_json,
                values_table.value_ordinal,
                values_table.value,
                values_table.information_type,
                values_table.structural_path,
                values_table.candidate_kind,
                values_table.structural_identity,
                values_table.source_id,
                values_table.lineage_json
            FROM hierarchy_extraction_rows AS result_rows
            JOIN hierarchy_database_cell_values AS values_table
              ON values_table.generation_id = $generationId
             AND values_table.source_set_id = result_rows.source_set_id
             AND values_table.row_ordinal = result_rows.database_row_ordinal
            WHERE result_rows.extraction_id = $extractionId
            ORDER BY result_rows.source_set_id, result_rows.row_ordinal,
                     values_table.field_key, values_table.repeat_coordinates_json,
                     values_table.value_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE hierarchy_extraction_datasets
            SET row_count = (
                    SELECT COUNT(*) FROM hierarchy_extraction_rows AS rows
                    WHERE rows.extraction_id = hierarchy_extraction_datasets.extraction_id
                      AND rows.source_set_id = hierarchy_extraction_datasets.source_set_id),
                value_count = (
                    SELECT COUNT(*) FROM hierarchy_extraction_cell_values AS values_table
                    WHERE values_table.extraction_id = hierarchy_extraction_datasets.extraction_id
                      AND values_table.source_set_id = hierarchy_extraction_datasets.source_set_id)
            WHERE extraction_id = $extractionId;
            """;
        update.Parameters.AddWithValue("$extractionId", extractionId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateHierarchyExtractionCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken)
    {
        var datasetCount = await ExtractionScalarIntAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM hierarchy_extraction_datasets WHERE extraction_id = $extractionId;",
            extractionId,
            cancellationToken).ConfigureAwait(false);
        var incompleteRows = await ExtractionScalarIntAsync(
            connection,
            transaction,
            """
                SELECT COUNT(*) FROM hierarchy_extraction_rows AS rows
                WHERE rows.extraction_id = $extractionId
                  AND NOT EXISTS (
                    SELECT 1 FROM hierarchy_extraction_cells AS cells
                    WHERE cells.extraction_id = rows.extraction_id
                      AND cells.source_set_id = rows.source_set_id
                      AND cells.row_ordinal = rows.row_ordinal);
                """,
            extractionId,
            cancellationToken).ConfigureAwait(false);
        var incompleteCells = await ExtractionScalarIntAsync(
            connection,
            transaction,
            """
                SELECT COUNT(*) FROM hierarchy_extraction_cells AS cells
                WHERE cells.extraction_id = $extractionId
                  AND NOT EXISTS (
                    SELECT 1 FROM hierarchy_extraction_cell_values AS values_table
                    WHERE values_table.extraction_id = cells.extraction_id
                      AND values_table.source_set_id = cells.source_set_id
                      AND values_table.row_ordinal = cells.row_ordinal
                      AND values_table.field_key = cells.field_key
                      AND values_table.repeat_coordinates_json = cells.repeat_coordinates_json);
                """,
            extractionId,
            cancellationToken).ConfigureAwait(false);
        var crossedSources = await ExtractionScalarIntAsync(
            connection,
            transaction,
            """
                SELECT COUNT(*)
                FROM hierarchy_extraction_cell_values AS values_table
                JOIN hierarchy_extraction_rows AS rows
                  ON rows.extraction_id = values_table.extraction_id
                 AND rows.source_set_id = values_table.source_set_id
                 AND rows.row_ordinal = values_table.row_ordinal
                WHERE values_table.extraction_id = $extractionId
                  AND values_table.source_id <> rows.source_id;
                """,
            extractionId,
            cancellationToken).ConfigureAwait(false);
        if (datasetCount != databaseGeneration.Datasets.Count
            || incompleteRows != 0
            || incompleteCells != 0
            || crossedSources != 0)
        {
            throw new StructuredInformationRepositoryException(
                "The hierarchy-aware Extraction candidate is incomplete or crosses source boundaries.");
        }
    }

    private static async Task<ExtractionResultSummary?> ReadHierarchyExtractionSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        string? generationJson;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT database_generation_json
                FROM hierarchy_extraction_results
                WHERE extraction_id = $extractionId;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            generationJson = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(generationJson))
        {
            return null;
        }

        var databaseGeneration = JsonSerializer.Deserialize<DatabaseGenerationSummary>(generationJson)
            ?? throw new StructuredInformationRepositoryException(
                "The Extraction Result Database basis is invalid.");
        var datasets = new List<ExtractionDatasetSummary>();
        await using var datasetCommand = connection.CreateCommand();
        datasetCommand.Transaction = transaction;
        datasetCommand.CommandText = """
            SELECT source_set_id, display_name, dataset_ordinal,
                   repeated_data_layout, row_count, value_count
            FROM hierarchy_extraction_datasets
            WHERE extraction_id = $extractionId
            ORDER BY dataset_ordinal;
            """;
        datasetCommand.Parameters.AddWithValue("$extractionId", extractionId);
        var storedDatasets = new List<(
            SourceSetId SourceSetId,
            string Name,
            int Ordinal,
            RepeatedDataLayout Layout,
            int RowCount,
            int ValueCount)>();
        await using (var reader = await datasetCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                storedDatasets.Add((
                    SourceSetId.From(Guid.Parse(reader.GetString(0))),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    (RepeatedDataLayout)reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }
        }

        foreach (var dataset in storedDatasets)
        {
            datasets.Add(new ExtractionDatasetSummary(
                dataset.SourceSetId,
                dataset.Name,
                dataset.Ordinal,
                dataset.Layout,
                dataset.RowCount,
                dataset.ValueCount,
                await ReadHierarchyExtractionColumnsAsync(
                        connection,
                        transaction,
                        extractionId,
                        dataset.SourceSetId,
                        cancellationToken)
                    .ConfigureAwait(false)));
        }

        return new ExtractionResultSummary(
            ParseOperationId(extractionId),
            databaseGeneration,
            datasets);
    }

    private static async Task<IReadOnlyList<DatabaseColumnDefinition>>
        ReadHierarchyExtractionColumnsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string extractionId,
            SourceSetId sourceSetId,
            CancellationToken cancellationToken)
    {
        var columns = new List<DatabaseColumnDefinition>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT field_key, effective_name, repeat_coordinates_json, column_ordinal
            FROM hierarchy_extraction_columns
            WHERE extraction_id = $extractionId AND source_set_id = $sourceSetId
            ORDER BY column_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var coordinates = JsonSerializer.Deserialize<int[]>(reader.GetString(2)) ?? [];
            columns.Add(new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    sourceSetId,
                    reader.GetString(0),
                    new DatabaseRepeatCoordinatePath(coordinates)),
                reader.GetString(1),
                reader.GetInt32(3)));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<ExtractionResultCell>>
        ReadHierarchyExtractionCellsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            OperationId extractionId,
            SourceSetId sourceSetId,
            int rowOrdinal,
            SourceId sourceId,
            CancellationToken cancellationToken)
    {
        var records = new List<ExtractionCellValueRecord>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cells.field_key, cells.effective_name,
                   cells.repeat_coordinates_json, cells.has_conflict,
                   values_table.value_ordinal, values_table.value,
                   values_table.information_type, values_table.structural_path,
                   values_table.candidate_kind, values_table.structural_identity,
                   values_table.source_id, values_table.lineage_json
            FROM hierarchy_extraction_cells AS cells
            JOIN hierarchy_extraction_columns AS columns_table
              ON columns_table.extraction_id = cells.extraction_id
             AND columns_table.source_set_id = cells.source_set_id
             AND columns_table.field_key = cells.field_key
             AND columns_table.repeat_coordinates_json = cells.repeat_coordinates_json
            JOIN hierarchy_extraction_cell_values AS values_table
              ON values_table.extraction_id = cells.extraction_id
             AND values_table.source_set_id = cells.source_set_id
             AND values_table.row_ordinal = cells.row_ordinal
             AND values_table.field_key = cells.field_key
             AND values_table.repeat_coordinates_json = cells.repeat_coordinates_json
            WHERE cells.extraction_id = $extractionId
              AND cells.source_set_id = $sourceSetId
              AND cells.row_ordinal = $rowOrdinal
            ORDER BY columns_table.column_ordinal, values_table.value_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        command.Parameters.AddWithValue("$rowOrdinal", rowOrdinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(new ExtractionCellValueRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3) != 0,
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    (SourceValueCandidateKind)reader.GetInt32(8),
                    reader.GetString(9),
                    SourceId.From(Guid.Parse(reader.GetString(10))),
                    reader.GetString(11)));
            }
        }

        if (records.Any(record => record.SourceId != sourceId))
        {
            throw new StructuredInformationRepositoryException(
                "An Extraction row contains cross-file values.");
        }

        var cells = new List<ExtractionResultCell>();
        foreach (var group in records.GroupBy(record =>
            (record.FieldKey, record.EffectiveName, record.CoordinatesJson, record.HasConflict)))
        {
            var coordinates = new DatabaseRepeatCoordinatePath(
                JsonSerializer.Deserialize<int[]>(group.Key.CoordinatesJson) ?? []);
            cells.Add(new ExtractionResultCell(
                new DatabaseColumnIdentity(sourceSetId, group.Key.FieldKey, coordinates),
                group.Key.EffectiveName,
                group.Key.HasConflict,
                group.Select(record => new ExtractionResultValue(
                    record.ValueOrdinal,
                    record.Value,
                    new DiscoveryInformationIdentity(
                        sourceSetId,
                        record.StructuralPath,
                        record.InformationType,
                        record.CandidateKind,
                        record.StructuralIdentity),
                    record.SourceId,
                    JsonSerializer.Deserialize<DatabaseLineageEvidence>(record.LineageJson)
                        ?? throw new StructuredInformationRepositoryException(
                            "Stored Extraction lineage is invalid."),
                    coordinates)).ToArray()));
        }

        return cells;
    }

    private static async Task<string?> ReadPublishedHierarchyExtractionIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken) =>
        await ExtractionScalarStringAsync(
                connection,
                transaction,
                "SELECT extraction_id FROM hierarchy_extraction_publication WHERE singleton_id = 1;",
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task PublishHierarchyExtractionPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO hierarchy_extraction_publication (singleton_id, extraction_id)
            VALUES (1, $extractionId)
            ON CONFLICT(singleton_id) DO UPDATE SET extraction_id = excluded.extraction_id;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteHierarchyExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM hierarchy_extraction_results WHERE extraction_id = $extractionId;";
        command.Parameters.AddWithValue("$extractionId", extractionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteExtractionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string extractionId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$generationId", generationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ExtractionScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExtractionScalarIntAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string extractionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private sealed record ExtractionRowHeader(
        int Ordinal,
        int DatabaseRowOrdinal,
        int SourceRowOrdinal,
        string RecordIdentity,
        DatabaseSourceMetadata Source);

    private sealed record ExtractionCellValueRecord(
        string FieldKey,
        string EffectiveName,
        string CoordinatesJson,
        bool HasConflict,
        int ValueOrdinal,
        string Value,
        string InformationType,
        string StructuralPath,
        SourceValueCandidateKind CandidateKind,
        string StructuralIdentity,
        SourceId SourceId,
        string LineageJson);
}
