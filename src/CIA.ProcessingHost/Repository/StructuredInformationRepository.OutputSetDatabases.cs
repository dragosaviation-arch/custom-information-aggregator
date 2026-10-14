using System.Globalization;
using System.Text.Json;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    private static async Task InsertOutputSetDatasetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        OutputSetId outputSetId,
        DatabaseDatasetBuildSpecification dataset,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO hierarchy_output_set_datasets (
                generation_id, output_set_id, parent_source_set_id,
                display_name, dataset_ordinal, repeated_data_layout)
            VALUES ($generationId, $outputSetId, $parentSourceSetId,
                $displayName, $ordinal, $layout);
            """, cancellationToken,
            ("$generationId", generationId),
            ("$outputSetId", outputSetId.ToString()),
            ("$parentSourceSetId", dataset.ParentSourceSetId.ToString()),
            ("$displayName", dataset.DisplayName),
            ("$ordinal", dataset.Ordinal),
            ("$layout", (int)dataset.RepeatedDataLayout)).ConfigureAwait(false);

        for (var familyIndex = 0; familyIndex < dataset.RowFamilies.Count; familyIndex++)
        {
            var family = dataset.RowFamilies[familyIndex];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO hierarchy_output_set_families (
                    generation_id, output_set_id, family_kind, family_anchor_path,
                    parent_family_kind, parent_family_anchor_path, family_ordinal, display_name)
                VALUES ($generationId, $outputSetId, $kind, $anchorPath,
                    $parentKind, $parentAnchorPath, $ordinal, $displayName);
                """, cancellationToken,
                ("$generationId", generationId),
                ("$outputSetId", outputSetId.ToString()),
                ("$kind", (int)family.Id.Kind),
                ("$anchorPath", family.Id.AnchorPath),
                ("$parentKind", family.ParentRowFamilyId is { } parent ? (int)parent.Kind : null),
                ("$parentAnchorPath", family.ParentRowFamilyId?.AnchorPath),
                ("$ordinal", familyIndex + 1),
                ("$displayName", family.Id.Kind == RowFamilyKind.Root ? "Root" : family.Id.AnchorPath))
                .ConfigureAwait(false);
        }

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
                INSERT INTO hierarchy_output_set_sources (
                    generation_id, output_set_id, source_id, source_ordinal,
                    source_file_name, full_source_path, source_kind, archive_path,
                    archive_member_path, file_modified_utc)
                VALUES ($generationId, $outputSetId, $sourceId, $sourceOrdinal,
                    $fileName, $path, $kind, $archivePath, $archiveMember, $modifiedUtc);
                """, cancellationToken,
                ("$generationId", generationId),
                ("$outputSetId", outputSetId.ToString()),
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

        foreach (var family in dataset.RowFamilies)
        {
            var familyFields = dataset.Fields.Where(field => field.RowFamilyId == family.Id).ToArray();
            for (var fieldIndex = 0; fieldIndex < familyFields.Length; fieldIndex++)
            {
                var field = familyFields[fieldIndex];
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO hierarchy_output_set_mappings (
                        generation_id, output_set_id, family_kind, family_anchor_path,
                        mapping_ordinal, field_key, effective_name,
                        is_explicit_override, detailed_identities_json)
                    VALUES ($generationId, $outputSetId, $familyKind, $familyAnchorPath,
                        $ordinal, $fieldKey, $effectiveName, $isOverride, $details);
                    """, cancellationToken,
                    ("$generationId", generationId),
                    ("$outputSetId", outputSetId.ToString()),
                    ("$familyKind", (int)family.Id.Kind),
                    ("$familyAnchorPath", family.Id.AnchorPath),
                    ("$ordinal", fieldIndex + 1),
                    ("$fieldKey", field.FieldKey),
                    ("$effectiveName", field.EffectiveName),
                    ("$isOverride", field.IsExplicitOverride ? 1 : 0),
                    ("$details", JsonSerializer.Serialize(field.DetailedIdentities)))
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task WriteOutputSetDatabaseCandidateSourceAsync(
        DatabaseCandidate candidate,
        OutputSetId outputSetId,
        SourceId sourceId,
        IReadOnlyList<DatabaseReviewRow> sourceRows,
        IReadOnlyList<DatabaseColumnIdentity>? sourceColumns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceRows);
        if (candidate.Specification is null)
        {
            throw new ArgumentException("The candidate is not hierarchy-aware.", nameof(candidate));
        }

        var dataset = candidate.Specification.Datasets.SingleOrDefault(
            item => item.OutputSetId == outputSetId)
            ?? throw new ArgumentException("The candidate does not contain the Output Set.", nameof(outputSetId));
        var allowedFamilies = dataset.RowFamilies.Select(family => family.Id).ToHashSet();
        var allowedFields = dataset.Fields.Select(field => (field.RowFamilyId, field.FieldKey)).ToHashSet();
        var columns = (sourceColumns ?? sourceRows.SelectMany(row => row.Cells)
                .Select(cell => cell.ColumnIdentity).ToArray()).ToArray();
        if (!dataset.Sources.Any(source => source.SourceId == sourceId)
            || columns.Distinct().Count() != columns.Length
            || columns.Any(column => !allowedFamilies.Contains(column.RowFamilyId)
                || !allowedFields.Contains((column.RowFamilyId, column.FieldKey)))
            || sourceRows.Any(row => row.Source.SourceId != sourceId
                || row.Source.SourceSetId != dataset.ParentSourceSetId
                || row.SemanticIdentity is null
                || !allowedFamilies.Contains(row.SemanticIdentity.RowFamilyId)
                || row.Cells.Any(cell =>
                    cell.ColumnIdentity.RowFamilyId != row.SemanticIdentity.RowFamilyId
                    || !allowedFields.Contains((
                        cell.ColumnIdentity.RowFamilyId,
                        cell.ColumnIdentity.FieldKey))
                    || cell.Values.Any(value => value.SourceId != sourceId))))
        {
            throw new ArgumentException(
                "Output Set rows crossed their approved source, family, or field projection boundary.",
                nameof(sourceRows));
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
                foreach (var column in columns)
                {
                    await EnsureOutputSetColumnAsync(
                        connection,
                        transaction,
                        generationId,
                        outputSetId,
                        column,
                        JsonSerializer.Serialize(column.RepeatCoordinates.Coordinates),
                        cancellationToken).ConfigureAwait(false);
                }

                var nextRowOrdinals = new Dictionary<RowFamilyId, int>();
                foreach (var sourceRow in sourceRows
                             .OrderBy(row => row.SemanticIdentity!.RowFamilyId)
                             .ThenBy(row => row.Ordinal))
                {
                    var identity = sourceRow.SemanticIdentity!;
                    if (!nextRowOrdinals.TryGetValue(identity.RowFamilyId, out var rowOrdinal))
                    {
                        rowOrdinal = await ScalarIntAsync(connection, transaction, """
                            SELECT COALESCE(MAX(row_ordinal), 0) + 1
                            FROM hierarchy_output_set_rows
                            WHERE generation_id = $generationId AND output_set_id = $outputSetId
                              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath;
                            """, cancellationToken,
                            ("$generationId", generationId),
                            ("$outputSetId", outputSetId.ToString()),
                            ("$familyKind", (int)identity.RowFamilyId.Kind),
                            ("$familyAnchorPath", identity.RowFamilyId.AnchorPath)).ConfigureAwait(false);
                    }

                    nextRowOrdinals[identity.RowFamilyId] = rowOrdinal + 1;
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO hierarchy_output_set_rows (
                            generation_id, output_set_id, family_kind, family_anchor_path,
                            row_ordinal, source_id, source_row_ordinal, anchor_instance_id,
                            projection_ordinal, record_identity, is_included)
                        VALUES ($generationId, $outputSetId, $familyKind, $familyAnchorPath,
                            $rowOrdinal, $sourceId, $sourceRowOrdinal, $anchorInstanceId,
                            $projectionOrdinal, $recordIdentity, 1);
                        """, cancellationToken,
                        ("$generationId", generationId),
                        ("$outputSetId", outputSetId.ToString()),
                        ("$familyKind", (int)identity.RowFamilyId.Kind),
                        ("$familyAnchorPath", identity.RowFamilyId.AnchorPath),
                        ("$rowOrdinal", rowOrdinal),
                        ("$sourceId", sourceId.ToString()),
                        ("$sourceRowOrdinal", sourceRow.Ordinal),
                        ("$anchorInstanceId", identity.AnchorInstanceId),
                        ("$projectionOrdinal", identity.ProjectionOrdinal),
                        ("$recordIdentity", sourceRow.RecordIdentity)).ConfigureAwait(false);

                    foreach (var cell in sourceRow.Cells)
                    {
                        var coordinatesJson = JsonSerializer.Serialize(
                            cell.ColumnIdentity.RepeatCoordinates.Coordinates);
                        await EnsureOutputSetColumnAsync(
                            connection,
                            transaction,
                            generationId,
                            outputSetId,
                            cell.ColumnIdentity,
                            coordinatesJson,
                            cancellationToken).ConfigureAwait(false);
                        await ExecuteAsync(connection, transaction, """
                            INSERT INTO hierarchy_output_set_cells (
                                generation_id, output_set_id, family_kind, family_anchor_path,
                                row_ordinal, field_key, repeat_coordinates_json, has_conflict)
                            VALUES ($generationId, $outputSetId, $familyKind, $familyAnchorPath,
                                $rowOrdinal, $fieldKey, $coordinates, $hasConflict);
                            """, cancellationToken,
                            ("$generationId", generationId),
                            ("$outputSetId", outputSetId.ToString()),
                            ("$familyKind", (int)identity.RowFamilyId.Kind),
                            ("$familyAnchorPath", identity.RowFamilyId.AnchorPath),
                            ("$rowOrdinal", rowOrdinal),
                            ("$fieldKey", cell.ColumnIdentity.FieldKey),
                            ("$coordinates", coordinatesJson),
                            ("$hasConflict", cell.HasConflict ? 1 : 0)).ConfigureAwait(false);

                        for (var valueIndex = 0; valueIndex < cell.Values.Count; valueIndex++)
                        {
                            var value = cell.Values[valueIndex];
                            await ExecuteAsync(connection, transaction, """
                                INSERT INTO hierarchy_output_set_cell_values (
                                    generation_id, output_set_id, family_kind, family_anchor_path,
                                    row_ordinal, field_key, repeat_coordinates_json, value_ordinal,
                                    value, information_type, structural_path, candidate_kind,
                                    structural_identity, source_id, lineage_json)
                                VALUES ($generationId, $outputSetId, $familyKind, $familyAnchorPath,
                                    $rowOrdinal, $fieldKey, $coordinates, $valueOrdinal,
                                    $value, $informationType, $structuralPath, $kind,
                                    $structuralIdentity, $sourceId, $lineage);
                                """, cancellationToken,
                                ("$generationId", generationId),
                                ("$outputSetId", outputSetId.ToString()),
                                ("$familyKind", (int)identity.RowFamilyId.Kind),
                                ("$familyAnchorPath", identity.RowFamilyId.AnchorPath),
                                ("$rowOrdinal", rowOrdinal),
                                ("$fieldKey", cell.ColumnIdentity.FieldKey),
                                ("$coordinates", coordinatesJson),
                                ("$valueOrdinal", valueIndex + 1),
                                ("$value", value.Value),
                                ("$informationType", value.DetailedIdentity.InformationType),
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

    private static async Task EnsureOutputSetColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        OutputSetId outputSetId,
        DatabaseColumnIdentity identity,
        string coordinatesJson,
        CancellationToken cancellationToken)
    {
        await using var nameCommand = connection.CreateCommand();
        nameCommand.Transaction = transaction;
        nameCommand.CommandText = """
            SELECT effective_name FROM hierarchy_output_set_mappings
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath
              AND field_key = $fieldKey;
            """;
        nameCommand.Parameters.AddWithValue("$generationId", generationId);
        nameCommand.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        nameCommand.Parameters.AddWithValue("$familyKind", (int)identity.RowFamilyId.Kind);
        nameCommand.Parameters.AddWithValue("$familyAnchorPath", identity.RowFamilyId.AnchorPath);
        nameCommand.Parameters.AddWithValue("$fieldKey", identity.FieldKey);
        var effectiveName = Convert.ToString(
            await nameCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture)
            ?? throw new StructuredInformationRepositoryException(
                "A flattened cell is outside its approved Output Set projection.");
        var nextOrdinal = await ScalarIntAsync(connection, transaction, """
            SELECT COALESCE(MAX(column_ordinal), 0) + 1
            FROM hierarchy_output_set_columns
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath;
            """, cancellationToken,
            ("$generationId", generationId),
            ("$outputSetId", outputSetId.ToString()),
            ("$familyKind", (int)identity.RowFamilyId.Kind),
            ("$familyAnchorPath", identity.RowFamilyId.AnchorPath)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO hierarchy_output_set_columns (
                generation_id, output_set_id, family_kind, family_anchor_path,
                column_ordinal, field_key, effective_name, repeat_coordinates_json)
            VALUES ($generationId, $outputSetId, $familyKind, $familyAnchorPath,
                $ordinal, $fieldKey, $name, $coordinates);
            """, cancellationToken,
            ("$generationId", generationId),
            ("$outputSetId", outputSetId.ToString()),
            ("$familyKind", (int)identity.RowFamilyId.Kind),
            ("$familyAnchorPath", identity.RowFamilyId.AnchorPath),
            ("$ordinal", nextOrdinal),
            ("$fieldKey", identity.FieldKey),
            ("$name", effectiveName),
            ("$coordinates", coordinatesJson)).ConfigureAwait(false);
    }

    private static async Task<DatabaseGenerationSummary?> ReadOutputSetSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationId generationId,
        CancellationToken cancellationToken)
    {
        var basic = new List<(
            OutputSetId OutputSetId,
            SourceSetId ParentSourceSetId,
            string Name,
            int Ordinal,
            RepeatedDataLayout Layout)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT output_set_id, parent_source_set_id, display_name,
                       dataset_ordinal, repeated_data_layout
                FROM hierarchy_output_set_datasets
                WHERE generation_id = $generationId
                ORDER BY dataset_ordinal;
                """;
            command.Parameters.AddWithValue("$generationId", generationId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                basic.Add((
                    OutputSetId.From(Guid.Parse(reader.GetString(0))),
                    SourceSetId.From(Guid.Parse(reader.GetString(1))),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    (RepeatedDataLayout)reader.GetInt32(4)));
            }
        }

        if (basic.Count == 0)
        {
            return null;
        }

        var datasets = new List<DatabaseDatasetSummary>(basic.Count);
        foreach (var dataset in basic)
        {
            var mappings = await ReadOutputSetMappingsAsync(
                connection,
                transaction,
                generationId,
                dataset.OutputSetId,
                dataset.ParentSourceSetId,
                cancellationToken).ConfigureAwait(false);
            var definitions = await ReadOutputSetFamilyDefinitionsAsync(
                connection,
                transaction,
                generationId,
                dataset.OutputSetId,
                dataset.ParentSourceSetId,
                cancellationToken).ConfigureAwait(false);
            var families = new List<DatabaseRowFamilySummary>();
            foreach (var definition in definitions)
            {
                var columns = await ReadOutputSetColumnsAsync(
                    connection,
                    transaction,
                    generationId,
                    dataset.OutputSetId,
                    definition.Id,
                    cancellationToken).ConfigureAwait(false);
                var rowCount = await CountOutputSetFamilyAsync(
                    connection,
                    transaction,
                    "hierarchy_output_set_rows",
                    generationId,
                    dataset.OutputSetId,
                    definition.Id,
                    cancellationToken).ConfigureAwait(false);
                var includedRowCount = await CountOutputSetFamilyAsync(
                    connection,
                    transaction,
                    "hierarchy_output_set_rows",
                    generationId,
                    dataset.OutputSetId,
                    definition.Id,
                    cancellationToken,
                    " AND is_included = 1").ConfigureAwait(false);
                var valueCount = await CountOutputSetFamilyAsync(
                    connection,
                    transaction,
                    "hierarchy_output_set_cell_values",
                    generationId,
                    dataset.OutputSetId,
                    definition.Id,
                    cancellationToken).ConfigureAwait(false);
                var familyMappings = mappings.Where(mapping => mapping.RowFamilyId == definition.Id).ToArray();
                if (rowCount > 0 && valueCount > 0 && columns.Count > 0)
                {
                    families.Add(new DatabaseRowFamilySummary(
                        definition.Id,
                        definition.ParentRowFamilyId,
                        families.Count + 1,
                        definition.DisplayName,
                        rowCount,
                        includedRowCount,
                        valueCount,
                        columns,
                        familyMappings));
                }
            }

            if (families.Count > 0)
            {
                var columns = families.SelectMany(family => family.Columns)
                    .Select((column, index) => new DatabaseColumnDefinition(
                        column.Identity,
                        column.EffectiveName,
                        index + 1))
                    .ToArray();
                datasets.Add(new DatabaseDatasetSummary(
                    dataset.OutputSetId,
                    dataset.ParentSourceSetId,
                    dataset.Name,
                    dataset.Ordinal,
                    dataset.Layout,
                    families.Sum(family => family.RowCount),
                    families.Sum(family => family.ValueCount),
                    columns,
                    mappings,
                    families));
            }
        }

        return datasets.Count == 0 ? null : new DatabaseGenerationSummary(generationId, datasets);
    }

    private static async Task<IReadOnlyList<StoredRowFamily>> ReadOutputSetFamilyDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationId generationId,
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        CancellationToken cancellationToken)
    {
        var result = new List<StoredRowFamily>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT family_kind, family_anchor_path, parent_family_kind,
                   parent_family_anchor_path, display_name
            FROM hierarchy_output_set_families
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
            ORDER BY family_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = new RowFamilyId(
                parentSourceSetId,
                (RowFamilyKind)reader.GetInt32(0),
                reader.GetString(1));
            RowFamilyId? parent = reader.IsDBNull(2)
                ? null
                : new RowFamilyId(
                    parentSourceSetId,
                    (RowFamilyKind)reader.GetInt32(2),
                    reader.GetString(3));
            result.Add(new StoredRowFamily(new RowFamilyDefinition(id, parent), reader.GetString(4)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<DatabaseFieldMapping>> ReadOutputSetMappingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationId generationId,
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseFieldMapping>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT family_kind, family_anchor_path, field_key, effective_name,
                   is_explicit_override, detailed_identities_json
            FROM hierarchy_output_set_mappings
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
            ORDER BY family_kind, family_anchor_path, mapping_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var details = JsonSerializer.Deserialize<DiscoveryInformationIdentity[]>(reader.GetString(5))
                ?? throw new StructuredInformationRepositoryException(
                    "Stored Output Set mapping identities are invalid.");
            var rowFamilyId = new RowFamilyId(
                parentSourceSetId,
                (RowFamilyKind)reader.GetInt32(0),
                reader.GetString(1));
            result.Add(new DatabaseFieldMapping(
                rowFamilyId,
                reader.GetString(3),
                reader.GetInt32(4) != 0,
                details,
                ParseOriginatingRowFamilyId(parentSourceSetId, rowFamilyId, reader.GetString(2))));
        }

        return result;
    }

    private static RowFamilyId ParseOriginatingRowFamilyId(
        SourceSetId sourceSetId,
        RowFamilyId storedFamilyId,
        string fieldKey)
    {
        if (storedFamilyId.Kind != RowFamilyKind.SourceFileProjection)
        {
            return storedFamilyId;
        }

        var parts = fieldKey.Split(':');
        if (parts.Length != 5
            || !string.Equals(parts[0], "source-file", StringComparison.Ordinal)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var kindValue)
            || !Enum.IsDefined(typeof(RowFamilyKind), kindValue))
        {
            throw new StructuredInformationRepositoryException(
                "Stored source-file projection field identity is invalid.");
        }

        try
        {
            return new RowFamilyId(
                sourceSetId,
                (RowFamilyKind)kindValue,
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new StructuredInformationRepositoryException(
                "Stored source-file projection Row Family origin is invalid.",
                exception);
        }
    }

    private static async Task<IReadOnlyList<DatabaseColumnDefinition>> ReadOutputSetColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationId generationId,
        OutputSetId outputSetId,
        RowFamilyId rowFamilyId,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseColumnDefinition>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT field_key, effective_name, repeat_coordinates_json, column_ordinal
            FROM hierarchy_output_set_columns
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath
            ORDER BY column_ordinal;
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
        command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var coordinates = JsonSerializer.Deserialize<int[]>(reader.GetString(2)) ?? [];
            result.Add(new DatabaseColumnDefinition(
                new DatabaseColumnIdentity(
                    rowFamilyId,
                    reader.GetString(0),
                    new DatabaseRepeatCoordinatePath(coordinates)),
                reader.GetString(1),
                reader.GetInt32(3)));
        }

        return result;
    }

    private static async Task<DatabaseReviewPage> ReadOutputSetDatabasePageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseReviewQuery query,
        OutputSetId outputSetId,
        DatabaseDatasetSummary dataset,
        DatabaseRowFamilySummary rowFamily,
        CancellationToken cancellationToken)
    {
        var inclusionPredicate = query.InclusionFilter switch
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
                    SELECT 1 FROM hierarchy_output_set_cell_values AS search_values
                    WHERE search_values.generation_id = rows.generation_id
                      AND search_values.output_set_id = rows.output_set_id
                      AND search_values.family_kind = rows.family_kind
                      AND search_values.family_anchor_path = rows.family_anchor_path
                      AND search_values.row_ordinal = rows.row_ordinal
                      AND (
                        search_values.value LIKE $search ESCAPE '\'
                        OR search_values.information_type LIKE $search ESCAPE '\'
                        OR search_values.structural_path LIKE $search ESCAPE '\'
                        OR ($candidateKind >= 0 AND search_values.candidate_kind = $candidateKind)
                        OR search_values.structural_identity LIKE $search ESCAPE '\'
                        OR search_values.lineage_json LIKE $search ESCAPE '\')))
            """;
        var where = "WHERE rows.generation_id = $generationId AND rows.output_set_id = $outputSetId"
            + " AND rows.family_kind = $familyKind AND rows.family_anchor_path = $familyAnchorPath"
            + inclusionPredicate + searchPredicate;

        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = $"""
            SELECT COUNT(*) FROM hierarchy_output_set_rows AS rows
            JOIN hierarchy_output_set_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.output_set_id = rows.output_set_id
             AND sources.source_id = rows.source_id
            JOIN hierarchy_output_set_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.output_set_id = rows.output_set_id
            {where};
            """;
        ConfigureOutputSetReviewQuery(countCommand, query, outputSetId, rowFamily.Id);
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
                   sources.file_modified_utc, rows.anchor_instance_id, rows.projection_ordinal
            FROM hierarchy_output_set_rows AS rows
            JOIN hierarchy_output_set_sources AS sources
              ON sources.generation_id = rows.generation_id
             AND sources.output_set_id = rows.output_set_id
             AND sources.source_id = rows.source_id
            JOIN hierarchy_output_set_datasets AS datasets
              ON datasets.generation_id = rows.generation_id
             AND datasets.output_set_id = rows.output_set_id
            {where}
            ORDER BY rows.row_ordinal
            LIMIT $limit OFFSET $offset;
            """;
        ConfigureOutputSetReviewQuery(rowCommand, query, outputSetId, rowFamily.Id);
        rowCommand.Parameters.AddWithValue("$limit", query.RowCount);
        rowCommand.Parameters.AddWithValue("$offset", query.StartRowOrdinal - 1);
        await using (var reader = await rowCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceId = SourceId.From(Guid.Parse(reader.GetString(3)));
                var metadata = new DatabaseSourceMetadata(
                    dataset.ParentSourceSetId,
                    dataset.DisplayName,
                    sourceId,
                    reader.GetString(4),
                    reader.GetString(5),
                    (LoadedSourceKind)reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : DateTimeOffset.Parse(
                        reader.GetString(9),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind));
                var semanticIdentity = new DatabaseRecordIdentity(
                    rowFamily.Id,
                    sourceId,
                    reader.GetInt64(10),
                    reader.GetInt32(11));
                if (!string.Equals(reader.GetString(2), semanticIdentity.ToString(), StringComparison.Ordinal))
                {
                    throw new StructuredInformationRepositoryException(
                        "A stored Output Set record identity is inconsistent with its typed components.");
                }

                rows.Add(new DatabaseReviewRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1) != 0,
                    semanticIdentity,
                    metadata,
                    []));
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            rows[index] = new DatabaseReviewRow(
                row.Ordinal,
                row.IsIncluded,
                row.SemanticIdentity!,
                row.Source,
                await ReadOutputSetCellsAsync(
                    connection,
                    transaction,
                    query.GenerationId,
                    outputSetId,
                    dataset,
                    rowFamily,
                    row.Ordinal,
                    row.Source.SourceId,
                    cancellationToken).ConfigureAwait(false));
        }

        return new DatabaseReviewPage(
            query.GenerationId,
            dataset,
            rowFamily,
            query.StartRowOrdinal,
            query.RowCount,
            totalRows,
            rows);
    }

    private static async Task<IReadOnlyList<DatabaseReviewCell>> ReadOutputSetCellsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationId generationId,
        OutputSetId outputSetId,
        DatabaseDatasetSummary dataset,
        DatabaseRowFamilySummary rowFamily,
        int rowOrdinal,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseReviewCell>();
        foreach (var column in rowFamily.Columns)
        {
            var coordinates = JsonSerializer.Serialize(column.Identity.RepeatCoordinates.Coordinates);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT cell.has_conflict, values_table.value, values_table.information_type,
                       values_table.structural_path, values_table.candidate_kind,
                       values_table.structural_identity, values_table.lineage_json
                FROM hierarchy_output_set_cells AS cell
                JOIN hierarchy_output_set_cell_values AS values_table
                 ON values_table.generation_id = cell.generation_id
                 AND values_table.output_set_id = cell.output_set_id
                 AND values_table.family_kind = cell.family_kind
                 AND values_table.family_anchor_path = cell.family_anchor_path
                 AND values_table.row_ordinal = cell.row_ordinal
                 AND values_table.field_key = cell.field_key
                 AND values_table.repeat_coordinates_json = cell.repeat_coordinates_json
                WHERE cell.generation_id = $generationId AND cell.output_set_id = $outputSetId
                  AND cell.family_kind = $familyKind AND cell.family_anchor_path = $familyAnchorPath
                  AND cell.row_ordinal = $rowOrdinal AND cell.field_key = $fieldKey
                  AND cell.repeat_coordinates_json = $coordinates
                ORDER BY values_table.value_ordinal;
                """;
            command.Parameters.AddWithValue("$generationId", generationId.ToString());
            command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
            command.Parameters.AddWithValue("$familyKind", (int)rowFamily.Id.Kind);
            command.Parameters.AddWithValue("$familyAnchorPath", rowFamily.Id.AnchorPath);
            command.Parameters.AddWithValue("$rowOrdinal", rowOrdinal);
            command.Parameters.AddWithValue("$fieldKey", column.Identity.FieldKey);
            command.Parameters.AddWithValue("$coordinates", coordinates);
            var values = new List<DatabaseReviewValue>();
            var conflict = false;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                conflict = reader.GetInt32(0) != 0;
                var identity = new DiscoveryInformationIdentity(
                    dataset.ParentSourceSetId,
                    reader.GetString(3),
                    reader.GetString(2),
                    (SourceValueCandidateKind)reader.GetInt32(4),
                    reader.GetString(5));
                var lineage = JsonSerializer.Deserialize<DatabaseLineageEvidence>(reader.GetString(6))
                    ?? throw new StructuredInformationRepositoryException(
                        "Stored Output Set Database lineage is invalid.");
                values.Add(new DatabaseReviewValue(
                    reader.GetString(1),
                    identity,
                    sourceId,
                    lineage,
                    column.Identity.RepeatCoordinates));
            }

            if (values.Count > 0)
            {
                result.Add(new DatabaseReviewCell(column.Identity, conflict, values));
            }
        }

        return result;
    }

    private static void ConfigureOutputSetReviewQuery(
        SqliteCommand command,
        DatabaseReviewQuery query,
        OutputSetId outputSetId,
        RowFamilyId rowFamilyId)
    {
        command.Parameters.AddWithValue("$generationId", query.GenerationId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
        command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
        if (query.SearchText is null)
        {
            return;
        }

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

    private static async Task<int> CountOutputSetFamilyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        OperationId generationId,
        OutputSetId outputSetId,
        RowFamilyId rowFamilyId,
        CancellationToken cancellationToken,
        string additionalPredicate = "")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*) FROM {table}
            WHERE generation_id = $generationId AND output_set_id = $outputSetId
              AND family_kind = $familyKind AND family_anchor_path = $familyAnchorPath
              {additionalPredicate};
            """;
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        command.Parameters.AddWithValue("$outputSetId", outputSetId.ToString());
        command.Parameters.AddWithValue("$familyKind", (int)rowFamilyId.Kind);
        command.Parameters.AddWithValue("$familyAnchorPath", rowFamilyId.AnchorPath);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }
}
