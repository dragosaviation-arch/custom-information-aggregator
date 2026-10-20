using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    private static async Task EnsureOutputSetExtractionSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_datasets (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL CHECK (length(output_set_id) = 36),
                    parent_source_set_id TEXT NOT NULL CHECK (length(parent_source_set_id) = 36),
                    display_name TEXT NOT NULL CHECK (length(display_name) > 0),
                    dataset_ordinal INTEGER NOT NULL CHECK (dataset_ordinal >= 1),
                    repeated_data_layout INTEGER NOT NULL,
                    row_count INTEGER NOT NULL DEFAULT 0 CHECK (row_count >= 0),
                    value_count INTEGER NOT NULL DEFAULT 0 CHECK (value_count >= 0),
                    PRIMARY KEY (extraction_id, output_set_id),
                    UNIQUE (extraction_id, dataset_ordinal),
                    FOREIGN KEY (extraction_id) REFERENCES hierarchy_extraction_results (extraction_id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_families (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL,
                    family_kind INTEGER NOT NULL,
                    family_anchor_path TEXT NOT NULL CHECK (length(family_anchor_path) > 0),
                    parent_family_kind INTEGER,
                    parent_family_anchor_path TEXT,
                    family_ordinal INTEGER NOT NULL CHECK (family_ordinal >= 1),
                    display_name TEXT NOT NULL CHECK (length(display_name) > 0),
                    database_row_count INTEGER NOT NULL CHECK (database_row_count >= 1),
                    row_count INTEGER NOT NULL DEFAULT 0 CHECK (row_count >= 0),
                    excluded_row_count INTEGER NOT NULL DEFAULT 0 CHECK (excluded_row_count >= 0),
                    value_count INTEGER NOT NULL DEFAULT 0 CHECK (value_count >= 0),
                    PRIMARY KEY (extraction_id, output_set_id, family_kind, family_anchor_path),
                    UNIQUE (extraction_id, output_set_id, family_ordinal),
                    FOREIGN KEY (extraction_id, output_set_id) REFERENCES hierarchy_output_set_extraction_datasets (extraction_id, output_set_id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_columns (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL,
                    family_kind INTEGER NOT NULL,
                    family_anchor_path TEXT NOT NULL,
                    column_ordinal INTEGER NOT NULL CHECK (column_ordinal >= 1),
                    field_key TEXT NOT NULL,
                    effective_name TEXT NOT NULL,
                    repeat_coordinates_json TEXT NOT NULL,
                    PRIMARY KEY (extraction_id, output_set_id, family_kind, family_anchor_path, field_key, repeat_coordinates_json),
                    UNIQUE (extraction_id, output_set_id, family_kind, family_anchor_path, column_ordinal),
                    FOREIGN KEY (extraction_id, output_set_id, family_kind, family_anchor_path) REFERENCES hierarchy_output_set_extraction_families (extraction_id, output_set_id, family_kind, family_anchor_path) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_rows (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL,
                    family_kind INTEGER NOT NULL,
                    family_anchor_path TEXT NOT NULL,
                    row_ordinal INTEGER NOT NULL CHECK (row_ordinal >= 1),
                    database_row_ordinal INTEGER NOT NULL CHECK (database_row_ordinal >= 1),
                    source_row_ordinal INTEGER NOT NULL CHECK (source_row_ordinal >= 1),
                    anchor_instance_id INTEGER NOT NULL CHECK (anchor_instance_id >= 1),
                    projection_ordinal INTEGER NOT NULL CHECK (projection_ordinal >= 1),
                    record_identity TEXT NOT NULL,
                    source_id TEXT NOT NULL CHECK (length(source_id) = 36),
                    source_set_name TEXT NOT NULL,
                    source_file_name TEXT NOT NULL,
                    full_source_path TEXT NOT NULL,
                    source_kind INTEGER NOT NULL,
                    archive_path TEXT,
                    archive_member_path TEXT,
                    file_modified_utc TEXT,
                    PRIMARY KEY (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal),
                    UNIQUE (extraction_id, output_set_id, family_kind, family_anchor_path, database_row_ordinal),
                    FOREIGN KEY (extraction_id, output_set_id, family_kind, family_anchor_path) REFERENCES hierarchy_output_set_extraction_families (extraction_id, output_set_id, family_kind, family_anchor_path) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_cells (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL,
                    family_kind INTEGER NOT NULL,
                    family_anchor_path TEXT NOT NULL,
                    row_ordinal INTEGER NOT NULL,
                    field_key TEXT NOT NULL,
                    effective_name TEXT NOT NULL,
                    repeat_coordinates_json TEXT NOT NULL,
                    has_conflict INTEGER NOT NULL CHECK (has_conflict IN (0, 1)),
                    PRIMARY KEY (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal, field_key, repeat_coordinates_json),
                    FOREIGN KEY (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal) REFERENCES hierarchy_output_set_extraction_rows (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal) ON DELETE CASCADE,
                    FOREIGN KEY (extraction_id, output_set_id, family_kind, family_anchor_path, field_key, repeat_coordinates_json) REFERENCES hierarchy_output_set_extraction_columns (extraction_id, output_set_id, family_kind, family_anchor_path, field_key, repeat_coordinates_json) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS hierarchy_output_set_extraction_cell_values (
                    extraction_id TEXT NOT NULL,
                    output_set_id TEXT NOT NULL,
                    family_kind INTEGER NOT NULL,
                    family_anchor_path TEXT NOT NULL,
                    row_ordinal INTEGER NOT NULL,
                    field_key TEXT NOT NULL,
                    repeat_coordinates_json TEXT NOT NULL,
                    value_ordinal INTEGER NOT NULL CHECK (value_ordinal >= 1),
                    value TEXT NOT NULL,
                    information_type TEXT NOT NULL,
                    structural_path TEXT NOT NULL,
                    candidate_kind INTEGER NOT NULL,
                    structural_identity TEXT NOT NULL,
                    source_id TEXT NOT NULL CHECK (length(source_id) = 36),
                    lineage_json TEXT NOT NULL,
                    PRIMARY KEY (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal, field_key, repeat_coordinates_json, value_ordinal),
                    FOREIGN KEY (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal, field_key, repeat_coordinates_json) REFERENCES hierarchy_output_set_extraction_cells (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal, field_key, repeat_coordinates_json) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_output_set_extraction_rows_order
                    ON hierarchy_output_set_extraction_rows (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal);
                CREATE INDEX IF NOT EXISTS ix_output_set_extraction_values_order
                    ON hierarchy_output_set_extraction_cell_values (extraction_id, output_set_id, family_kind, family_anchor_path, row_ordinal, field_key, repeat_coordinates_json, value_ordinal);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CopyOutputSetExtractionSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_datasets (
                extraction_id, output_set_id, parent_source_set_id, display_name,
                dataset_ordinal, repeated_data_layout, row_count, value_count)
            SELECT $extractionId, output_set_id, parent_source_set_id, display_name,
                   dataset_ordinal, repeated_data_layout, 0, 0
            FROM hierarchy_output_set_datasets
            WHERE generation_id = $generationId
            ORDER BY dataset_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_families (
                extraction_id, output_set_id, family_kind, family_anchor_path,
                parent_family_kind, parent_family_anchor_path, family_ordinal,
                display_name, database_row_count, row_count, excluded_row_count, value_count)
            SELECT $extractionId, families.output_set_id, families.family_kind,
                   families.family_anchor_path, families.parent_family_kind,
                   families.parent_family_anchor_path, families.family_ordinal,
                   families.display_name,
                   (SELECT COUNT(*) FROM hierarchy_output_set_rows AS rows
                    WHERE rows.generation_id = families.generation_id
                      AND rows.output_set_id = families.output_set_id
                      AND rows.family_kind = families.family_kind
                      AND rows.family_anchor_path = families.family_anchor_path),
                   0,
                   (SELECT COUNT(*) FROM hierarchy_output_set_rows AS rows
                    WHERE rows.generation_id = families.generation_id
                      AND rows.output_set_id = families.output_set_id
                      AND rows.family_kind = families.family_kind
                      AND rows.family_anchor_path = families.family_anchor_path
                      AND rows.is_included = 0),
                   0
            FROM hierarchy_output_set_families AS families
            WHERE families.generation_id = $generationId
            ORDER BY families.output_set_id, families.family_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_columns (
                extraction_id, output_set_id, family_kind, family_anchor_path,
                column_ordinal, field_key, effective_name, repeat_coordinates_json)
            SELECT $extractionId, output_set_id, family_kind, family_anchor_path,
                   column_ordinal, field_key, effective_name, repeat_coordinates_json
            FROM hierarchy_output_set_columns
            WHERE generation_id = $generationId
            ORDER BY output_set_id, family_kind, family_anchor_path, column_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_rows (
                extraction_id, output_set_id, family_kind, family_anchor_path,
                row_ordinal, database_row_ordinal, source_row_ordinal,
                anchor_instance_id, projection_ordinal, record_identity,
                source_id, source_set_name, source_file_name, full_source_path,
                source_kind, archive_path, archive_member_path, file_modified_utc)
            SELECT
                $extractionId,
                rows.output_set_id,
                rows.family_kind,
                rows.family_anchor_path,
                ROW_NUMBER() OVER (
                    PARTITION BY rows.output_set_id, rows.family_kind, rows.family_anchor_path
                    ORDER BY sources.source_ordinal, rows.anchor_instance_id,
                             rows.projection_ordinal, rows.row_ordinal),
                rows.row_ordinal,
                rows.source_row_ordinal,
                rows.anchor_instance_id,
                rows.projection_ordinal,
                rows.record_identity,
                rows.source_id,
                datasets.display_name,
                sources.source_file_name,
                sources.full_source_path,
                sources.source_kind,
                sources.archive_path,
                sources.archive_member_path,
                sources.file_modified_utc
            FROM hierarchy_output_set_rows AS rows
            JOIN hierarchy_output_set_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.output_set_id = rows.output_set_id
            JOIN hierarchy_output_set_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.output_set_id = rows.output_set_id
             AND sources.source_id = rows.source_id
            WHERE rows.generation_id = $generationId AND rows.is_included = 1
            ORDER BY datasets.dataset_ordinal, rows.family_kind, rows.family_anchor_path,
                     sources.source_ordinal, rows.anchor_instance_id,
                     rows.projection_ordinal, rows.row_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_cells (
                extraction_id, output_set_id, family_kind, family_anchor_path,
                row_ordinal, field_key, effective_name,
                repeat_coordinates_json, has_conflict)
            SELECT
                $extractionId,
                result_rows.output_set_id,
                result_rows.family_kind,
                result_rows.family_anchor_path,
                result_rows.row_ordinal,
                cells.field_key,
                columns_table.effective_name,
                cells.repeat_coordinates_json,
                cells.has_conflict
            FROM hierarchy_output_set_extraction_rows AS result_rows
            JOIN hierarchy_output_set_cells AS cells
             ON cells.generation_id = $generationId
             AND cells.output_set_id = result_rows.output_set_id
             AND cells.family_kind = result_rows.family_kind
             AND cells.family_anchor_path = result_rows.family_anchor_path
             AND cells.row_ordinal = result_rows.database_row_ordinal
            JOIN hierarchy_output_set_columns AS columns_table
             ON columns_table.generation_id = cells.generation_id
             AND columns_table.output_set_id = cells.output_set_id
             AND columns_table.family_kind = cells.family_kind
             AND columns_table.family_anchor_path = cells.family_anchor_path
             AND columns_table.field_key = cells.field_key
             AND columns_table.repeat_coordinates_json = cells.repeat_coordinates_json
            WHERE result_rows.extraction_id = $extractionId
            ORDER BY result_rows.output_set_id, result_rows.family_kind,
                     result_rows.family_anchor_path, result_rows.row_ordinal,
                     columns_table.column_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await ExecuteExtractionAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_extraction_cell_values (
                extraction_id, output_set_id, family_kind, family_anchor_path,
                row_ordinal, field_key, repeat_coordinates_json, value_ordinal,
                value, information_type, structural_path, candidate_kind,
                structural_identity, source_id, lineage_json)
            SELECT
                $extractionId,
                result_rows.output_set_id,
                result_rows.family_kind,
                result_rows.family_anchor_path,
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
            FROM hierarchy_output_set_extraction_rows AS result_rows
            JOIN hierarchy_output_set_cell_values AS values_table
             ON values_table.generation_id = $generationId
             AND values_table.output_set_id = result_rows.output_set_id
             AND values_table.family_kind = result_rows.family_kind
             AND values_table.family_anchor_path = result_rows.family_anchor_path
             AND values_table.row_ordinal = result_rows.database_row_ordinal
            WHERE result_rows.extraction_id = $extractionId
            ORDER BY result_rows.output_set_id, result_rows.family_kind,
                     result_rows.family_anchor_path, result_rows.row_ordinal,
                     values_table.field_key, values_table.repeat_coordinates_json,
                     values_table.value_ordinal;
            """, extractionId, generationId, cancellationToken).ConfigureAwait(false);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE hierarchy_output_set_extraction_datasets
                SET row_count = (
                        SELECT COUNT(*) FROM hierarchy_output_set_extraction_rows AS rows
                        WHERE rows.extraction_id = hierarchy_output_set_extraction_datasets.extraction_id
                          AND rows.output_set_id = hierarchy_output_set_extraction_datasets.output_set_id),
                    value_count = (
                        SELECT COUNT(*) FROM hierarchy_output_set_extraction_cell_values AS values_table
                        WHERE values_table.extraction_id = hierarchy_output_set_extraction_datasets.extraction_id
                          AND values_table.output_set_id = hierarchy_output_set_extraction_datasets.output_set_id)
                WHERE extraction_id = $extractionId;
                """;
            update.Parameters.AddWithValue("$extractionId", extractionId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var familyUpdate = connection.CreateCommand();
        familyUpdate.Transaction = transaction;
        familyUpdate.CommandText = """
            UPDATE hierarchy_output_set_extraction_families
            SET row_count = (
                    SELECT COUNT(*) FROM hierarchy_output_set_extraction_rows AS rows
                    WHERE rows.extraction_id = hierarchy_output_set_extraction_families.extraction_id
                      AND rows.output_set_id = hierarchy_output_set_extraction_families.output_set_id
                      AND rows.family_kind = hierarchy_output_set_extraction_families.family_kind
                      AND rows.family_anchor_path = hierarchy_output_set_extraction_families.family_anchor_path),
                value_count = (
                    SELECT COUNT(*) FROM hierarchy_output_set_extraction_cell_values AS values_table
                    WHERE values_table.extraction_id = hierarchy_output_set_extraction_families.extraction_id
                      AND values_table.output_set_id = hierarchy_output_set_extraction_families.output_set_id
                      AND values_table.family_kind = hierarchy_output_set_extraction_families.family_kind
                      AND values_table.family_anchor_path = hierarchy_output_set_extraction_families.family_anchor_path)
            WHERE extraction_id = $extractionId;
            """;
        familyUpdate.Parameters.AddWithValue("$extractionId", extractionId);
        await familyUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateOutputSetExtractionCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken)
    {
        var datasetCount = await ExtractionScalarIntAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM hierarchy_output_set_extraction_datasets WHERE extraction_id = $extractionId;",
            extractionId,
            cancellationToken).ConfigureAwait(false);
        var familyCount = await ExtractionScalarIntAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM hierarchy_output_set_extraction_families WHERE extraction_id = $extractionId;",
            extractionId,
            cancellationToken).ConfigureAwait(false);
        var incompleteRows = await ExtractionScalarIntAsync(
            connection,
            transaction,
            """
                SELECT COUNT(*) FROM hierarchy_output_set_extraction_rows AS rows
                WHERE rows.extraction_id = $extractionId
                  AND rows.family_kind <> $sourceFileProjectionKind
                  AND NOT EXISTS (
                    SELECT 1 FROM hierarchy_output_set_extraction_cells AS cells
                    WHERE cells.extraction_id = rows.extraction_id
                      AND cells.output_set_id = rows.output_set_id
                      AND cells.family_kind = rows.family_kind
                      AND cells.family_anchor_path = rows.family_anchor_path
                      AND cells.row_ordinal = rows.row_ordinal);
            """,
            extractionId,
            cancellationToken,
            ("$sourceFileProjectionKind", (int)RowFamilyKind.SourceFileProjection))
            .ConfigureAwait(false);
        var incompleteCells = await ExtractionScalarIntAsync(
            connection,
            transaction,
            """
                SELECT COUNT(*) FROM hierarchy_output_set_extraction_cells AS cells
                WHERE cells.extraction_id = $extractionId
                  AND NOT EXISTS (
                    SELECT 1 FROM hierarchy_output_set_extraction_cell_values AS values_table
                    WHERE values_table.extraction_id = cells.extraction_id
                      AND values_table.output_set_id = cells.output_set_id
                      AND values_table.family_kind = cells.family_kind
                      AND values_table.family_anchor_path = cells.family_anchor_path
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
                FROM hierarchy_output_set_extraction_cell_values AS values_table
                JOIN hierarchy_output_set_extraction_rows AS rows
                 ON rows.extraction_id = values_table.extraction_id
                 AND rows.output_set_id = values_table.output_set_id
                 AND rows.family_kind = values_table.family_kind
                 AND rows.family_anchor_path = values_table.family_anchor_path
                 AND rows.row_ordinal = values_table.row_ordinal
                WHERE values_table.extraction_id = $extractionId
                  AND values_table.source_id <> rows.source_id;
                """,
            extractionId,
            cancellationToken).ConfigureAwait(false);
        if (datasetCount != databaseGeneration.Datasets.Count
            || familyCount != databaseGeneration.Datasets.Sum(dataset => dataset.RowFamilies.Count)
            || incompleteRows != 0
            || incompleteCells != 0
            || crossedSources != 0)
        {
            throw new StructuredInformationRepositoryException(
                "The OutputSet-backed Extraction candidate is incomplete or crosses dataset boundaries.");
        }
    }

    private static async Task<ExtractionResultSummary?> ReadOutputSetExtractionSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string extractionId,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken)
    {
        var stored = new List<(
            OutputSetId OutputSetId,
            SourceSetId ParentSourceSetId,
            string Name,
            int Ordinal,
            RepeatedDataLayout Layout,
            int RowCount,
            int ValueCount)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT output_set_id, parent_source_set_id, display_name,
                       dataset_ordinal, repeated_data_layout, row_count, value_count
                FROM hierarchy_output_set_extraction_datasets
                WHERE extraction_id = $extractionId
                ORDER BY dataset_ordinal;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stored.Add((
                    OutputSetId.From(Guid.Parse(reader.GetString(0))),
                    SourceSetId.From(Guid.Parse(reader.GetString(1))),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    (RepeatedDataLayout)reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }
        }

        if (stored.Count == 0)
        {
            return null;
        }

        var datasets = new List<ExtractionDatasetSummary>(stored.Count);
        foreach (var dataset in stored)
        {
            var families = await ReadOutputSetExtractionFamiliesAsync(
                    connection,
                    transaction,
                    extractionId,
                    dataset.OutputSetId,
                    dataset.ParentSourceSetId,
                    cancellationToken)
                .ConfigureAwait(false);
            var columns = families.SelectMany(family => family.Columns)
                .Select((column, index) => new DatabaseColumnDefinition(
                    column.Identity,
                    column.EffectiveName,
                    index + 1))
                .ToArray();
            datasets.Add(new ExtractionDatasetSummary(
                dataset.OutputSetId,
                dataset.ParentSourceSetId,
                dataset.Name,
                dataset.Ordinal,
                dataset.Layout,
                dataset.RowCount,
                dataset.ValueCount,
                columns,
                families));
        }

        return new ExtractionResultSummary(
            ParseOperationId(extractionId),
            databaseGeneration,
            datasets);
    }

    private static async Task<IReadOnlyList<ExtractionRowFamilySummary>>
        ReadOutputSetExtractionFamiliesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string extractionId,
            OutputSetId outputSetId,
            SourceSetId parentSourceSetId,
            CancellationToken cancellationToken)
    {
        var stored = new List<(
            RowFamilyId Id,
            RowFamilyId? Parent,
            int Ordinal,
            string Name,
            int DatabaseRowCount,
            int RowCount,
            int ExcludedRowCount,
            int ValueCount)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT family_kind, family_anchor_path, parent_family_kind,
                       parent_family_anchor_path, family_ordinal, display_name,
                       database_row_count, row_count, excluded_row_count, value_count
                FROM hierarchy_output_set_extraction_families
                WHERE extraction_id = $extractionId AND output_set_id = $outputSetId
                ORDER BY family_ordinal;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId);
            command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = new RowFamilyId(
                    parentSourceSetId,
                    (RowFamilyKind)reader.GetInt32(0),
                    reader.GetString(1));
                var parent = reader.IsDBNull(2)
                    ? (RowFamilyId?)null
                    : new RowFamilyId(
                        parentSourceSetId,
                        (RowFamilyKind)reader.GetInt32(2),
                        reader.GetString(3));
                stored.Add((
                    id,
                    parent,
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9)));
            }
        }

        var families = new List<ExtractionRowFamilySummary>(stored.Count);
        foreach (var family in stored)
        {
            families.Add(new ExtractionRowFamilySummary(
                family.Id,
                family.Parent,
                family.Ordinal,
                family.Name,
                family.DatabaseRowCount,
                family.RowCount,
                family.ExcludedRowCount,
                family.ValueCount,
                await ReadOutputSetExtractionColumnsAsync(
                        connection,
                        transaction,
                        extractionId,
                        outputSetId,
                        family.Id,
                        cancellationToken)
                    .ConfigureAwait(false)));
        }

        return families;
    }

    private static async Task<IReadOnlyList<DatabaseColumnDefinition>>
        ReadOutputSetExtractionColumnsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string extractionId,
            OutputSetId outputSetId,
            RowFamilyId rowFamilyId,
            CancellationToken cancellationToken)
    {
        var columns = new List<DatabaseColumnDefinition>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT field_key, effective_name, repeat_coordinates_json, column_ordinal
            FROM hierarchy_output_set_extraction_columns
            WHERE extraction_id = $extractionId AND output_set_id = $outputSetId
              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath
            ORDER BY column_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId);
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
        command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    rowFamilyId,
                    reader.GetString(0),
                    new DatabaseRepeatCoordinatePath(
                        JsonSerializer.Deserialize<int[]>(reader.GetString(2)) ?? [])),
                reader.GetString(1),
                reader.GetInt32(3)));
        }

        return columns;
    }

    public async IAsyncEnumerable<ExtractionResultRow> StreamPublishedExtractionRowsAsync(
        OperationId extractionId,
        OutputSetId outputSetId,
        RowFamilyId rowFamilyId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (extractionId.Value == Guid.Empty
            || extractionId.Value.Version != 7
            || outputSetId.Value == Guid.Empty
            || rowFamilyId.SourceSetId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An Output Set Extraction row read requires valid typed identities.");
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

        var generation = await ReadExtractionDatabaseGenerationAsync(
                connection,
                transaction,
                extractionId.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        var dataset = generation.Datasets.SingleOrDefault(item => item.OutputSetId == outputSetId)
            ?? throw new StructuredInformationRepositoryException(
                "The requested Output Set has no Database basis for this Extraction Result.");
        if (dataset.ParentSourceSetId != rowFamilyId.SourceSetId
            || dataset.RowFamilies.All(family => family.Id != rowFamilyId))
        {
            throw new StructuredInformationRepositoryException(
                "The requested Row Family does not belong to the Output Set.");
        }

        var originFamilies = CreateOutputSetOriginatingFamilyLookup(
            dataset,
            rowFamilyId.SourceSetId);
        var lastRowOrdinal = 0;
        while (true)
        {
            ExtractionRowHeader? row = null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT row_ordinal, database_row_ordinal, source_row_ordinal,
                       anchor_instance_id, projection_ordinal, record_identity,
                       source_id, source_set_name, source_file_name,
                       full_source_path, source_kind, archive_path, archive_member_path,
                       file_modified_utc
                FROM hierarchy_output_set_extraction_rows
                WHERE extraction_id = $extractionId
                  AND output_set_id = $outputSetId
                  AND family_kind = $familyKind
                  AND family_anchor_path = $familyAnchorPath
                  AND row_ordinal > $lastRowOrdinal
                ORDER BY row_ordinal
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$extractionId", extractionId.ToString());
            command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
            command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
            command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
            command.Parameters.AddWithValue("$lastRowOrdinal", lastRowOrdinal);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var sourceId = SourceId.From(Guid.Parse(reader.GetString(6)));
                    row = new ExtractionRowHeader(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        new DatabaseRecordIdentity(
                            rowFamilyId,
                            sourceId,
                            reader.GetInt64(3),
                            reader.GetInt32(4)),
                        reader.GetString(5),
                        new DatabaseSourceMetadata(
                            dataset.ParentSourceSetId,
                            reader.GetString(7),
                            sourceId,
                            reader.GetString(8),
                            reader.GetString(9),
                            (LoadedSourceKind)reader.GetInt32(10),
                            reader.IsDBNull(11) ? null : reader.GetString(11),
                            reader.IsDBNull(12) ? null : reader.GetString(12),
                            reader.IsDBNull(13)
                                ? null
                                : DateTimeOffset.Parse(
                                    reader.GetString(13),
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind)));
                }
            }

            if (row is null)
            {
                break;
            }

            if (!string.Equals(row.RecordIdentity, row.SemanticIdentity.ToString(), StringComparison.Ordinal))
            {
                throw new StructuredInformationRepositoryException(
                    "A stored Output Set Extraction row identity is inconsistent.");
            }

            var cells = await ReadOutputSetExtractionCellsAsync(
                    connection,
                    transaction,
                    extractionId,
                    outputSetId,
                    rowFamilyId,
                    row.Ordinal,
                    row.Source.SourceId,
                    originFamilies,
                    cancellationToken)
                .ConfigureAwait(false);
            yield return new ExtractionResultRow(
                row.Ordinal,
                row.DatabaseRowOrdinal,
                row.SourceRowOrdinal,
                row.SemanticIdentity,
                row.Source,
                cells);
            lastRowOrdinal = row.Ordinal;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ExtractionResultCell>>
        ReadOutputSetExtractionCellsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            OperationId extractionId,
            OutputSetId outputSetId,
            RowFamilyId rowFamilyId,
            int rowOrdinal,
            SourceId sourceId,
            IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> originFamilies,
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
            FROM hierarchy_output_set_extraction_cells AS cells
            JOIN hierarchy_output_set_extraction_columns AS columns_table
              ON columns_table.extraction_id = cells.extraction_id
             AND columns_table.output_set_id = cells.output_set_id
             AND columns_table.family_kind = cells.family_kind
             AND columns_table.family_anchor_path = cells.family_anchor_path
             AND columns_table.field_key = cells.field_key
             AND columns_table.repeat_coordinates_json = cells.repeat_coordinates_json
            JOIN hierarchy_output_set_extraction_cell_values AS values_table
              ON values_table.extraction_id = cells.extraction_id
             AND values_table.output_set_id = cells.output_set_id
             AND values_table.family_kind = cells.family_kind
             AND values_table.family_anchor_path = cells.family_anchor_path
             AND values_table.row_ordinal = cells.row_ordinal
             AND values_table.field_key = cells.field_key
             AND values_table.repeat_coordinates_json = cells.repeat_coordinates_json
            WHERE cells.extraction_id = $extractionId
              AND cells.output_set_id = $outputSetId
              AND cells.family_kind = $familyKind
              AND cells.family_anchor_path = $familyAnchorPath
              AND cells.row_ordinal = $rowOrdinal
            ORDER BY columns_table.column_ordinal, values_table.value_ordinal;
            """;
        command.Parameters.AddWithValue("$extractionId", extractionId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
        command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
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
                "An Output Set Extraction row contains cross-file values.");
        }

        var cells = new List<ExtractionResultCell>();
        foreach (var group in records.GroupBy(record =>
            (record.FieldKey, record.EffectiveName, record.CoordinatesJson, record.HasConflict)))
        {
            var coordinates = new DatabaseRepeatCoordinatePath(
                JsonSerializer.Deserialize<int[]>(group.Key.CoordinatesJson) ?? []);
            var values = group.Select(record =>
            {
                var identity = new DiscoveryInformationIdentity(
                    rowFamilyId.SourceSetId,
                    record.StructuralPath,
                    record.InformationType,
                    record.CandidateKind,
                    record.StructuralIdentity);
                if (!originFamilies.TryGetValue(identity, out var originatingFamily))
                {
                    throw new StructuredInformationRepositoryException(
                        "An Output Set extracted value has no originating Row Family in its Database basis.");
                }

                return new ExtractionResultValue(
                    record.ValueOrdinal,
                    record.Value,
                    identity,
                    record.SourceId,
                    JsonSerializer.Deserialize<DatabaseLineageEvidence>(record.LineageJson)
                        ?? throw new StructuredInformationRepositoryException(
                            "Stored Output Set Extraction lineage is invalid."),
                    coordinates,
                    originatingFamily);
            }).ToArray();
            cells.Add(new ExtractionResultCell(
                new DatabaseColumnIdentity(rowFamilyId, group.Key.FieldKey, coordinates),
                group.Key.EffectiveName,
                group.Key.HasConflict,
                values));
        }

        return cells;
    }

    private static IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId>
        CreateOutputSetOriginatingFamilyLookup(
            DatabaseDatasetSummary dataset,
            SourceSetId parentSourceSetId)
    {
        if (dataset.ParentSourceSetId != parentSourceSetId || dataset.OutputSetId is null)
        {
            throw new StructuredInformationRepositoryException(
                "The Extraction Output Set has no valid Database basis.");
        }

        return dataset.Mappings
            .SelectMany(mapping => mapping.DetailedIdentities.Select(identity =>
                new KeyValuePair<DiscoveryInformationIdentity, RowFamilyId>(
                    identity,
                    mapping.OriginatingRowFamilyId)))
            .GroupBy(pair => pair.Key)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Value).Distinct().Single());
    }
}
