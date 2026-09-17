using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    public async Task<DatabaseCandidate> CreateDatabaseCandidateAsync(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(specification);
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
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO hierarchy_database_generations (
                        generation_id, created_utc, generation_state)
                    VALUES ($generationId, $createdUtc, $state);
                    """, cancellationToken,
                    ("$generationId", generationId),
                    ("$createdUtc", correlation.InitiatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$state", CandidateGenerationState)).ConfigureAwait(false);

                foreach (var dataset in specification.Datasets)
                {
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO hierarchy_database_datasets (
                            generation_id, source_set_id, display_name, dataset_ordinal, repeated_data_layout)
                        VALUES ($generationId, $sourceSetId, $displayName, $ordinal, $layout);
                        """, cancellationToken,
                        ("$generationId", generationId),
                        ("$sourceSetId", dataset.SourceSetId.ToString()),
                        ("$displayName", dataset.DisplayName),
                        ("$ordinal", dataset.Ordinal),
                        ("$layout", (int)dataset.RepeatedDataLayout)).ConfigureAwait(false);

                    for (var sourceIndex = 0; sourceIndex < dataset.Sources.Count; sourceIndex++)
                    {
                        var source = dataset.Sources[sourceIndex];
                        var fullPath = Path.GetFullPath(source.Path);
                        DateTimeOffset? modifiedUtc = null;
                        try
                        {
                            modifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            modifiedUtc = null;
                        }

                        await ExecuteAsync(connection, transaction, """
                            INSERT INTO hierarchy_database_sources (
                                generation_id, source_set_id, source_id, source_ordinal,
                                source_file_name, full_source_path, source_kind, archive_path,
                                archive_member_path, file_modified_utc)
                            VALUES (
                                $generationId, $sourceSetId, $sourceId, $sourceOrdinal,
                                $fileName, $path, $kind, $archivePath, $archiveMember, $modifiedUtc);
                            """, cancellationToken,
                            ("$generationId", generationId),
                            ("$sourceSetId", dataset.SourceSetId.ToString()),
                            ("$sourceId", source.SourceId.ToString()),
                            ("$sourceOrdinal", sourceIndex + 1),
                            ("$fileName", Path.GetFileName(fullPath)),
                            ("$path", fullPath),
                            ("$kind", (int)source.Kind),
                            ("$archivePath", source.ArchiveProvenance?.OriginalArchivePath),
                            ("$archiveMember", source.ArchiveProvenance?.ArchiveMemberPath),
                            ("$modifiedUtc", modifiedUtc?.ToString("O", CultureInfo.InvariantCulture)))
                            .ConfigureAwait(false);
                    }

                    for (var fieldIndex = 0; fieldIndex < dataset.Fields.Count; fieldIndex++)
                    {
                        var field = dataset.Fields[fieldIndex];
                        await ExecuteAsync(connection, transaction, """
                            INSERT INTO hierarchy_database_mappings (
                                generation_id, source_set_id, mapping_ordinal, mapping_key, field_key,
                                candidate_kind, canonical_field_identity, effective_name,
                                is_explicit_override, detailed_identities_json)
                            VALUES (
                                $generationId, $sourceSetId, $ordinal, $mappingKey, $fieldKey,
                                $kind, $canonical, $effectiveName, $isOverride, $details);
                            """, cancellationToken,
                            ("$generationId", generationId),
                            ("$sourceSetId", dataset.SourceSetId.ToString()),
                            ("$ordinal", fieldIndex + 1),
                            ("$mappingKey", $"{(int)field.LogicalIdentity.CandidateKind}:{field.LogicalIdentity.CanonicalFieldIdentity}"),
                            ("$fieldKey", field.FieldKey),
                            ("$kind", (int)field.LogicalIdentity.CandidateKind),
                            ("$canonical", field.LogicalIdentity.CanonicalFieldIdentity),
                            ("$effectiveName", field.EffectiveName),
                            ("$isOverride", field.IsExplicitOverride ? 1 : 0),
                            ("$details", JsonSerializer.Serialize(field.DetailedIdentities)))
                            .ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DatabaseCandidate(correlation, specification);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task WriteHierarchyDatabaseCandidateSourceAsync(
        DatabaseCandidate candidate,
        SourceSetId sourceSetId,
        SourceId sourceId,
        IReadOnlyList<DatabaseReviewRow> sourceRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceRows);
        if (candidate.Specification is null)
        {
            throw new ArgumentException("The candidate is not hierarchy-aware.", nameof(candidate));
        }
        var dataset = candidate.Specification.Datasets.SingleOrDefault(item => item.SourceSetId == sourceSetId)
            ?? throw new ArgumentException("The candidate does not contain the Source Set.", nameof(sourceSetId));
        if (!dataset.Sources.Any(source => source.SourceId == sourceId)
            || sourceRows.Any(row => row.Source.SourceId != sourceId
                || row.Source.SourceSetId != sourceSetId))
        {
            throw new ArgumentException("Database rows crossed their captured source boundary.", nameof(sourceRows));
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
                var generationId = candidate.Correlation.OperationId.ToString();
                var nextRowOrdinal = await ScalarIntAsync(connection, transaction, """
                    SELECT COALESCE(MAX(row_ordinal), 0) + 1
                    FROM hierarchy_database_rows
                    WHERE generation_id = $generationId AND source_set_id = $sourceSetId;
                    """, cancellationToken,
                    ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()))
                    .ConfigureAwait(false);

                foreach (var sourceRow in sourceRows.OrderBy(row => row.Ordinal))
                {
                    var rowOrdinal = nextRowOrdinal++;
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO hierarchy_database_rows (
                            generation_id, source_set_id, row_ordinal, source_id,
                            source_row_ordinal, record_identity, is_included)
                        VALUES ($generationId, $sourceSetId, $rowOrdinal, $sourceId,
                            $sourceRowOrdinal, $recordIdentity, 1);
                        """, cancellationToken,
                        ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()),
                        ("$rowOrdinal", rowOrdinal), ("$sourceId", sourceId.ToString()),
                        ("$sourceRowOrdinal", sourceRow.Ordinal),
                        ("$recordIdentity", sourceRow.RecordIdentity)).ConfigureAwait(false);

                    foreach (var cell in sourceRow.Cells)
                    {
                        if (cell.Values.Any(value => value.SourceId != sourceId))
                        {
                            throw new StructuredInformationRepositoryException(
                                "A Database cell crossed its row source boundary.");
                        }
                        var coordinatesJson = JsonSerializer.Serialize(
                            cell.ColumnIdentity.RepeatCoordinates.Coordinates);
                        await EnsureHierarchyColumnAsync(
                            connection, transaction, generationId, sourceSetId,
                            cell.ColumnIdentity, coordinatesJson, cancellationToken)
                            .ConfigureAwait(false);
                        await ExecuteAsync(connection, transaction, """
                            INSERT INTO hierarchy_database_cells (
                                generation_id, source_set_id, row_ordinal, field_key,
                                repeat_coordinates_json, has_conflict)
                            VALUES ($generationId, $sourceSetId, $rowOrdinal, $fieldKey,
                                $coordinates, $hasConflict);
                            """, cancellationToken,
                            ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()),
                            ("$rowOrdinal", rowOrdinal), ("$fieldKey", cell.ColumnIdentity.FieldKey),
                            ("$coordinates", coordinatesJson), ("$hasConflict", cell.HasConflict ? 1 : 0))
                            .ConfigureAwait(false);

                        for (var valueIndex = 0; valueIndex < cell.Values.Count; valueIndex++)
                        {
                            var value = cell.Values[valueIndex];
                            await ExecuteAsync(connection, transaction, """
                                INSERT INTO hierarchy_database_cell_values (
                                    generation_id, source_set_id, row_ordinal, field_key,
                                    repeat_coordinates_json, value_ordinal, value, information_type,
                                    structural_path, candidate_kind, structural_identity, source_id, lineage_json)
                                VALUES ($generationId, $sourceSetId, $rowOrdinal, $fieldKey,
                                    $coordinates, $valueOrdinal, $value, $informationType,
                                    $structuralPath, $kind, $structuralIdentity, $sourceId, $lineage);
                                """, cancellationToken,
                                ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()),
                                ("$rowOrdinal", rowOrdinal), ("$fieldKey", cell.ColumnIdentity.FieldKey),
                                ("$coordinates", coordinatesJson), ("$valueOrdinal", valueIndex + 1),
                                ("$value", value.Value), ("$informationType", value.DetailedIdentity.InformationType),
                                ("$structuralPath", value.DetailedIdentity.StructuralPath),
                                ("$kind", (int)value.DetailedIdentity.CandidateKind),
                                ("$structuralIdentity", value.DetailedIdentity.StructuralIdentity),
                                ("$sourceId", value.SourceId.ToString()),
                                ("$lineage", JsonSerializer.Serialize(value.Lineage)))
                                .ConfigureAwait(false);
                        }
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<DatabaseGenerationSummary> ValidateAndPublishHierarchyDatabaseCandidateAsync(
        DatabaseCandidate candidate,
        CancellationToken cancellationToken,
        Func<bool> tryEnterNonCancellableCommitBoundary)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(tryEnterNonCancellableCommitBoundary);
        if (candidate.Specification is null)
        {
            throw new ArgumentException("The candidate is not hierarchy-aware.", nameof(candidate));
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
                var summary = await ReadHierarchySummaryAsync(
                    connection, transaction, candidate.Correlation.OperationId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new StructuredInformationRepositoryException("The Database candidate is unavailable.");
                if (summary.Datasets.Count != candidate.Specification.Datasets.Count
                    || summary.Datasets.Any(dataset => dataset.RowCount < 1 || dataset.ValueCount < 1))
                {
                    throw new StructuredInformationRepositoryException(
                        "Every Source Set dataset must contain complete hierarchy-aware rows before publication.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!tryEnterNonCancellableCommitBoundary())
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                var generationId = candidate.Correlation.OperationId.ToString();
                await ExecuteAsync(connection, transaction, """
                    UPDATE hierarchy_database_generations SET generation_state = 2
                    WHERE generation_id = $generationId AND generation_state = 1;
                    INSERT INTO hierarchy_database_publication (singleton_id, generation_id)
                    VALUES (1, $generationId)
                    ON CONFLICT(singleton_id) DO UPDATE SET generation_id = excluded.generation_id;
                    """, CancellationToken.None, ("$generationId", generationId)).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return summary;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    internal async Task<DatabaseGenerationSummary?> ReadPublishedHierarchyDatabaseGenerationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation_id FROM hierarchy_database_publication WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var id))
        {
            return null;
        }
        return await ReadHierarchySummaryAsync(
            connection, null, OperationId.From(id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseReviewPage?> ReadPublishedDatabasePageAsync(
        DatabaseReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenGenerationConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var publishedId = await ScalarStringAsync(connection, transaction,
            "SELECT generation_id FROM hierarchy_database_publication WHERE singleton_id = 1;",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(publishedId, query.GenerationId.ToString(), StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var summary = await ReadHierarchySummaryAsync(
            connection, transaction, query.GenerationId, cancellationToken).ConfigureAwait(false)
            ?? throw new StructuredInformationRepositoryException("The published Database summary is unavailable.");
        var dataset = summary.Datasets.SingleOrDefault(item => item.SourceSetId == query.SourceSetId)
            ?? throw new StructuredInformationRepositoryException("The requested Source Set dataset is unavailable.");

        var predicate = query.InclusionFilter switch
        {
            DatabaseRowInclusionFilter.Included => " AND rows.is_included = 1",
            DatabaseRowInclusionFilter.Excluded => " AND rows.is_included = 0",
            _ => string.Empty
        };
        var searchPredicate = query.SearchText is null ? string.Empty : """
             AND (
                datasets.display_name LIKE $search ESCAPE '\'
                OR sources.source_id LIKE $search ESCAPE '\'
                OR sources.source_file_name LIKE $search ESCAPE '\'
                OR sources.full_source_path LIKE $search ESCAPE '\'
                OR ($sourceKind >= 0 AND sources.source_kind = $sourceKind)
                OR COALESCE(sources.archive_path, '') LIKE $search ESCAPE '\'
                OR COALESCE(sources.archive_member_path, '') LIKE $search ESCAPE '\'
                OR COALESCE(sources.file_modified_utc, '') LIKE $search ESCAPE '\'
                OR rows.record_identity LIKE $search ESCAPE '\'
                OR EXISTS (
                    SELECT 1 FROM hierarchy_database_cell_values AS search_values
                    WHERE search_values.generation_id = rows.generation_id
                      AND search_values.source_set_id = rows.source_set_id
                      AND search_values.row_ordinal = rows.row_ordinal
                      AND (
                        search_values.value LIKE $search ESCAPE '\'
                        OR search_values.information_type LIKE $search ESCAPE '\'
                        OR search_values.structural_path LIKE $search ESCAPE '\'
                        OR ($candidateKind >= 0 AND search_values.candidate_kind = $candidateKind)
                        OR search_values.structural_identity LIKE $search ESCAPE '\'
                        OR search_values.lineage_json LIKE $search ESCAPE '\')))
            """;
        var where = "WHERE rows.generation_id = $generationId AND rows.source_set_id = $sourceSetId"
            + predicate + searchPredicate;
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = $"""
            SELECT COUNT(*) FROM hierarchy_database_rows AS rows
            JOIN hierarchy_database_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.source_set_id = rows.source_set_id
             AND sources.source_id = rows.source_id
            JOIN hierarchy_database_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.source_set_id = rows.source_set_id
            {where};
            """;
        ConfigureReviewQuery(countCommand, query);
        var totalRows = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        var rows = new List<DatabaseReviewRow>();
        await using var rowCommand = connection.CreateCommand();
        rowCommand.Transaction = transaction;
        rowCommand.CommandText = $"""
            SELECT rows.row_ordinal, rows.is_included, rows.record_identity,
                   sources.source_id, sources.source_file_name, sources.full_source_path,
                   sources.source_kind, sources.archive_path, sources.archive_member_path,
                   sources.file_modified_utc
            FROM hierarchy_database_rows AS rows
            JOIN hierarchy_database_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.source_set_id = rows.source_set_id
             AND sources.source_id = rows.source_id
            JOIN hierarchy_database_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.source_set_id = rows.source_set_id
            {where}
            ORDER BY rows.row_ordinal
            LIMIT $limit OFFSET $offset;
            """;
        ConfigureReviewQuery(rowCommand, query);
        rowCommand.Parameters.AddWithValue("$limit", query.RowCount);
        rowCommand.Parameters.AddWithValue("$offset", query.StartRowOrdinal - 1);
        await using (var reader = await rowCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceId = SourceId.From(Guid.Parse(reader.GetString(3)));
                var metadata = new DatabaseSourceMetadata(
                    query.SourceSetId, dataset.DisplayName, sourceId,
                    reader.GetString(4), reader.GetString(5),
                    (LoadedSourceKind)reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : DateTimeOffset.Parse(
                        reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                rows.Add(new DatabaseReviewRow(
                    reader.GetInt32(0), reader.GetInt32(1) != 0, reader.GetString(2), metadata, []));
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            rows[index] = new DatabaseReviewRow(
                row.Ordinal,
                row.IsIncluded,
                row.RecordIdentity,
                row.Source,
                await ReadHierarchyCellsAsync(
                    connection, transaction, query.GenerationId, dataset,
                    row.Ordinal, row.Source.SourceId, cancellationToken)
                    .ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseReviewPage(
            query.GenerationId, dataset, query.StartRowOrdinal, query.RowCount, totalRows, rows);
    }

    public async Task<int> SetPublishedDatabaseRowsIncludedAsync(
        DatabaseRowInclusionChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenGenerationConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var published = await ScalarStringAsync(connection, transaction,
                "SELECT generation_id FROM hierarchy_database_publication WHERE singleton_id = 1;",
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(published, change.GenerationId.ToString(), StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new StructuredInformationRepositoryException("The requested Database generation is not published.");
            }
            var changed = 0;
            foreach (var ordinal in change.RowOrdinals.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE hierarchy_database_rows SET is_included = $included
                    WHERE generation_id = $generationId AND source_set_id = $sourceSetId
                      AND row_ordinal = $ordinal AND is_included <> $included;
                    """;
                command.Parameters.AddWithValue("$included", change.IsIncluded ? 1 : 0);
                command.Parameters.AddWithValue("$generationId", change.GenerationId.ToString());
                command.Parameters.AddWithValue("$sourceSetId", change.SourceSetId.ToString());
                command.Parameters.AddWithValue("$ordinal", ordinal);
                changed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private static async Task EnsureHierarchyColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        SourceSetId sourceSetId,
        DatabaseColumnIdentity identity,
        string coordinatesJson,
        CancellationToken cancellationToken)
    {
        await using var nameCommand = connection.CreateCommand();
        nameCommand.Transaction = transaction;
        nameCommand.CommandText = """
            SELECT effective_name FROM hierarchy_database_mappings
            WHERE generation_id = $generationId AND source_set_id = $sourceSetId AND field_key = $fieldKey;
            """;
        nameCommand.Parameters.AddWithValue("$generationId", generationId);
        nameCommand.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        nameCommand.Parameters.AddWithValue("$fieldKey", identity.FieldKey);
        var effectiveName = Convert.ToString(
            await nameCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture)
            ?? throw new StructuredInformationRepositoryException("A flattened cell is outside its mapped schema.");
        var nextOrdinal = await ScalarIntAsync(connection, transaction, """
            SELECT COALESCE(MAX(column_ordinal), 0) + 1 FROM hierarchy_database_columns
            WHERE generation_id = $generationId AND source_set_id = $sourceSetId;
            """, cancellationToken, ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()))
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO hierarchy_database_columns (
                generation_id, source_set_id, column_ordinal, field_key, effective_name, repeat_coordinates_json)
            VALUES ($generationId, $sourceSetId, $ordinal, $fieldKey, $name, $coordinates);
            """, cancellationToken,
            ("$generationId", generationId), ("$sourceSetId", sourceSetId.ToString()),
            ("$ordinal", nextOrdinal), ("$fieldKey", identity.FieldKey),
            ("$name", effectiveName), ("$coordinates", coordinatesJson)).ConfigureAwait(false);
    }

    private static async Task<DatabaseGenerationSummary?> ReadHierarchySummaryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationId generationId,
        CancellationToken cancellationToken)
    {
        var datasets = new List<DatabaseDatasetSummary>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_set_id, display_name, dataset_ordinal, repeated_data_layout
            FROM hierarchy_database_datasets WHERE generation_id = $generationId
            ORDER BY dataset_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        var basic = new List<(SourceSetId Id, string Name, int Ordinal, RepeatedDataLayout Layout)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                basic.Add((SourceSetId.From(Guid.Parse(reader.GetString(0))), reader.GetString(1),
                    reader.GetInt32(2), (RepeatedDataLayout)reader.GetInt32(3)));
            }
        }
        if (basic.Count == 0)
        {
            return null;
        }
        foreach (var dataset in basic)
        {
            var mappings = await ReadHierarchyMappingsAsync(
                connection, transaction, generationId, dataset.Id, cancellationToken).ConfigureAwait(false);
            var columns = await ReadHierarchyColumnsAsync(
                connection, transaction, generationId, dataset.Id, cancellationToken).ConfigureAwait(false);
            var rowCount = await CountAsync(connection, transaction,
                "hierarchy_database_rows", generationId, dataset.Id, cancellationToken).ConfigureAwait(false);
            var valueCount = await CountAsync(connection, transaction,
                "hierarchy_database_cell_values", generationId, dataset.Id, cancellationToken).ConfigureAwait(false);
            if (rowCount > 0 && valueCount > 0 && columns.Count > 0)
            {
                datasets.Add(new DatabaseDatasetSummary(
                    dataset.Id, dataset.Name, dataset.Ordinal, dataset.Layout,
                    rowCount, valueCount, columns, mappings));
            }
        }
        return datasets.Count == 0 ? null : new DatabaseGenerationSummary(generationId, datasets);
    }

    private static async Task<IReadOnlyList<DatabaseFieldMapping>> ReadHierarchyMappingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, OperationId generationId,
        SourceSetId sourceSetId, CancellationToken cancellationToken)
    {
        var result = new List<DatabaseFieldMapping>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate_kind, canonical_field_identity, effective_name,
                   is_explicit_override, detailed_identities_json
            FROM hierarchy_database_mappings
            WHERE generation_id = $generationId AND source_set_id = $sourceSetId
            ORDER BY mapping_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var details = JsonSerializer.Deserialize<DiscoveryInformationIdentity[]>(reader.GetString(4))
                ?? throw new StructuredInformationRepositoryException("Stored Database mapping identities are invalid.");
            result.Add(new DatabaseFieldMapping(
                new DatabaseLogicalFieldIdentity(sourceSetId, (SourceValueCandidateKind)reader.GetInt32(0), reader.GetString(1)),
                reader.GetString(2), reader.GetInt32(3) != 0, details));
        }
        return result;
    }

    private static async Task<IReadOnlyList<DatabaseColumnDefinition>> ReadHierarchyColumnsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, OperationId generationId,
        SourceSetId sourceSetId, CancellationToken cancellationToken)
    {
        var result = new List<DatabaseColumnDefinition>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT field_key, effective_name, repeat_coordinates_json, column_ordinal
            FROM hierarchy_database_columns
            WHERE generation_id = $generationId AND source_set_id = $sourceSetId
            ORDER BY column_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var coords = JsonSerializer.Deserialize<int[]>(reader.GetString(2)) ?? [];
            result.Add(new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(sourceSetId, reader.GetString(0), new DatabaseRepeatCoordinatePath(coords)),
                reader.GetString(1), reader.GetInt32(3)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<DatabaseReviewCell>> ReadHierarchyCellsAsync(
        SqliteConnection connection, SqliteTransaction transaction, OperationId generationId,
        DatabaseDatasetSummary dataset, int rowOrdinal, SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseReviewCell>();
        foreach (var column in dataset.Columns)
        {
            var coordinates = JsonSerializer.Serialize(column.Identity.RepeatCoordinates.Coordinates);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT cell.has_conflict, values_table.value, values_table.information_type,
                       values_table.structural_path, values_table.candidate_kind,
                       values_table.structural_identity, values_table.lineage_json
                FROM hierarchy_database_cells AS cell
                JOIN hierarchy_database_cell_values AS values_table
                  ON values_table.generation_id = cell.generation_id
                 AND values_table.source_set_id = cell.source_set_id
                 AND values_table.row_ordinal = cell.row_ordinal
                 AND values_table.field_key = cell.field_key
                 AND values_table.repeat_coordinates_json = cell.repeat_coordinates_json
                WHERE cell.generation_id = $generationId AND cell.source_set_id = $sourceSetId
                  AND cell.row_ordinal = $rowOrdinal AND cell.field_key = $fieldKey
                  AND cell.repeat_coordinates_json = $coordinates
                ORDER BY values_table.value_ordinal;
                """;
            command.Parameters.AddWithValue("$generationId", generationId.ToString());
            command.Parameters.AddWithValue("$sourceSetId", dataset.SourceSetId.ToString());
            command.Parameters.AddWithValue("$rowOrdinal", rowOrdinal);
            command.Parameters.AddWithValue("$fieldKey", column.Identity.FieldKey);
            command.Parameters.AddWithValue("$coordinates", coordinates);
            var values = new List<DatabaseReviewValue>();
            bool conflict = false;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                conflict = reader.GetInt32(0) != 0;
                var identity = new DiscoveryInformationIdentity(
                    dataset.SourceSetId, reader.GetString(3), reader.GetString(2),
                    (SourceValueCandidateKind)reader.GetInt32(4), reader.GetString(5));
                var lineage = JsonSerializer.Deserialize<DatabaseLineageEvidence>(reader.GetString(6))
                    ?? throw new StructuredInformationRepositoryException("Stored Database lineage is invalid.");
                values.Add(new DatabaseReviewValue(
                    reader.GetString(1), identity, sourceId, lineage, column.Identity.RepeatCoordinates));
            }
            if (values.Count > 0)
            {
                result.Add(new DatabaseReviewCell(column.Identity, conflict, values));
            }
        }
        return result;
    }

    private static void ConfigureReviewQuery(SqliteCommand command, DatabaseReviewQuery query)
    {
        command.Parameters.AddWithValue("$generationId", query.GenerationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", query.SourceSetId.ToString());
        if (query.SearchText is not null)
        {
            var escaped = query.SearchText.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$search", $"%{escaped}%");
            command.Parameters.AddWithValue(
                "$sourceKind",
                Enum.TryParse<LoadedSourceKind>(query.SearchText, ignoreCase: true, out var sourceKind)
                    && Enum.IsDefined(sourceKind)
                    ? (int)sourceKind
                    : -1);
            command.Parameters.AddWithValue(
                "$candidateKind",
                Enum.TryParse<SourceValueCandidateKind>(
                    query.SearchText,
                    ignoreCase: true,
                    out var candidateKind)
                    && Enum.IsDefined(candidateKind)
                    ? (int)candidateKind
                    : -1);
        }
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table,
        OperationId generationId, SourceSetId sourceSetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE generation_id = $generationId AND source_set_id = $sourceSetId;";
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", sourceSetId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
