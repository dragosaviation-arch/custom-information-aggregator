using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Repository;

public sealed partial class StructuredInformationRepository
{
    private static readonly JsonSerializerOptions DiscoveryJsonOptions =
        CreateDiscoveryJsonOptions();

    public async Task BeginDiscoveryIndexAsync(
        OperationCorrelation correlation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO discovery_results (operation_id, created_utc, result_state)
                VALUES ($operationId, $createdUtc, 1);
                """;
            command.Parameters.AddWithValue("$operationId", correlation.OperationId.ToString());
            command.Parameters.AddWithValue(
                "$createdUtc",
                correlation.InitiatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The Discovery contributor index could not be initialized.", exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task AddDiscoverySourceAsync(
        OperationId operationId,
        LoadedSourceContract source,
        string sourceName,
        int sourceOrder,
        IReadOnlyCollection<DiscoveryIndexContribution> contributions,
        CancellationToken cancellationToken = default)
    {
        await AddDiscoverySourcesAsync(
                operationId,
                [new DiscoveryIndexedSource(source, sourceName, sourceOrder, contributions)],
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AddDiscoverySourcesAsync(
        OperationId operationId,
        IReadOnlyCollection<DiscoveryIndexedSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            return;
        }

        foreach (var indexedSource in sources)
        {
            ArgumentNullException.ThrowIfNull(indexedSource.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(indexedSource.SourceName);
            ArgumentNullException.ThrowIfNull(indexedSource.Contributions);
            if (indexedSource.SourceOrder < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sources));
            }
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var sourceCommand = CreateDiscoverySourceInsertCommand(
                    connection,
                    transaction);
                await using var informationCommand = CreateDiscoveryInformationInsertCommand(
                    connection,
                    transaction);
                await using var contributionCommand = CreateDiscoveryContributionInsertCommand(
                    connection,
                    transaction,
                    operationId);
                sourceCommand.Prepare();
                informationCommand.Prepare();
                contributionCommand.Prepare();

                foreach (var indexedSource in sources)
                {
                    SetDiscoverySourceParameters(sourceCommand, operationId, indexedSource);
                    await sourceCommand.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var contribution in indexedSource.Contributions)
                    {
                        if (contribution.DefinesInformation)
                        {
                            SetDiscoveryContributionParameters(
                                informationCommand,
                                operationId,
                                indexedSource,
                                contribution);
                            await informationCommand.ExecuteNonQueryAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }

                        contributionCommand.Parameters["$informationOrdinal"].Value =
                            contribution.InformationOrdinal;
                        contributionCommand.Parameters["$sourceOrder"].Value =
                            indexedSource.SourceOrder;
                        contributionCommand.Parameters["$occurrenceCount"].Value =
                            contribution.OccurrenceCount;
                        contributionCommand.ExecuteNonQuery();
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
        catch (SqliteException exception)
        {
            throw new StructuredInformationRepositoryException(
                "The Discovery contributor index could not record a source.", exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<IReadOnlyList<DiscoveredInformation>> PublishDiscoveryIndexAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RankDiscoveryInformationAsync(
                    connection,
                    transaction,
                    operationId,
                    cancellationToken).ConfigureAwait(false);
                var information = await ReadDiscoveryInformationAsync(
                    connection,
                    transaction,
                    operationId,
                    cancellationToken).ConfigureAwait(false);
                if (information.Count == 0)
                {
                    throw new StructuredInformationRepositoryException(
                        "A successful Discovery index must contain information.");
                }

                await using (var publish = connection.CreateCommand())
                {
                    publish.Transaction = transaction;
                    publish.CommandText = """
                        UPDATE discovery_results
                        SET result_state = 2
                        WHERE operation_id = $operationId AND result_state = 1;
                        INSERT INTO discovery_publication (singleton_id, operation_id)
                        VALUES (1, $operationId)
                        ON CONFLICT(singleton_id) DO UPDATE SET operation_id = excluded.operation_id;
                        DELETE FROM discovery_results WHERE operation_id <> $operationId;
                        """;
                    publish.Parameters.AddWithValue("$operationId", operationId.ToString());
                    await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return information;
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
                "The Discovery contributor index could not be published.", exception);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task DiscardDiscoveryIndexAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection(allowCreate: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM discovery_results
                WHERE operation_id = $operationId AND result_state = 1;
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<DiscoveryOccurrenceResolution?> ResolveDiscoveryOccurrenceAsync(
        DiscoveryOccurrenceLookup lookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection(allowCreate: false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var details = await ValidateDiscoverySelectionAsync(
            connection,
            lookup.DiscoveryOperationId,
            lookup.DetailedIdentities,
            cancellationToken).ConfigureAwait(false);
        if (details is null
            || details.Sum(detail => (long)detail.TotalOccurrenceCount)
                != lookup.TotalOccurrenceCount)
        {
            return null;
        }

        var localWithinIdentity = lookup.GlobalOrdinal;
        DiscoveryInformationDetail? selected = null;
        foreach (var detail in details)
        {
            if (localWithinIdentity <= detail.TotalOccurrenceCount)
            {
                selected = detail;
                break;
            }

            localWithinIdentity -= detail.TotalOccurrenceCount;
        }

        if (selected is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, source_name, source_contract_json, occurrence_count,
                   cumulative_count
            FROM (
                SELECT s.source_id, s.source_name, s.source_contract_json,
                       c.occurrence_count, s.source_order,
                       SUM(c.occurrence_count) OVER (
                           ORDER BY s.source_order ROWS UNBOUNDED PRECEDING) AS cumulative_count
                FROM discovery_contributions c
                JOIN discovery_sources s
                  ON s.operation_id = c.operation_id AND s.source_order = c.source_order
                WHERE c.operation_id = $operationId
                  AND c.information_ordinal = $informationOrdinal
            ) ordered_sources
            WHERE cumulative_count >= $localOrdinal
            ORDER BY source_order
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$operationId",
            lookup.DiscoveryOperationId.ToString());
        command.Parameters.AddWithValue("$informationOrdinal", selected.InformationOrdinal);
        command.Parameters.AddWithValue("$localOrdinal", localWithinIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var occurrenceCount = reader.GetInt32(3);
        var cumulativeCount = reader.GetInt32(4);
        var source = JsonSerializer.Deserialize<LoadedSourceContract>(
            reader.GetString(2),
            DiscoveryJsonOptions);
        if (source is null || source.SourceId.ToString() != reader.GetString(0))
        {
            throw new StructuredInformationRepositoryException(
                "The Discovery contributor index contains invalid source metadata.");
        }

        return new DiscoveryOccurrenceResolution(
            selected.Identity,
            source,
            reader.GetString(1),
            localWithinIdentity - (cumulativeCount - occurrenceCount),
            occurrenceCount);
    }

    public async Task<DiscoveryContributorPage?> ReadDiscoveryContributorsAsync(
        DiscoveryContributorPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection(allowCreate: false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var details = await ValidateDiscoverySelectionAsync(
            connection,
            query.DiscoveryOperationId,
            query.DetailedIdentities,
            cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            return null;
        }

        var logical = DiscoveryLogicalIdentity.Create(query.DetailedIdentities[0]);
        var summary = await ReadDiscoveryLogicalSummaryAsync(
            connection,
            query.DiscoveryOperationId,
            logical,
            cancellationToken).ConfigureAwait(false);
        if (summary.SourceCount != query.ExpectedSourceCount
            || query.StartIndex >= summary.SourceCount)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_id, s.source_name, SUM(c.occurrence_count) AS occurrence_count,
                   MIN(i.presentation_order) AS first_information_order, s.source_order
            FROM discovery_information i
            JOIN discovery_contributions c
              ON c.operation_id = i.operation_id
             AND c.information_ordinal = i.information_ordinal
            JOIN discovery_sources s
              ON s.operation_id = c.operation_id AND s.source_order = c.source_order
            WHERE i.operation_id = $operationId
              AND i.source_set_id = $sourceSetId
              AND i.candidate_kind = $candidateKind
              AND i.logical_field_identity = $logicalFieldIdentity
            GROUP BY s.source_id, s.source_name, s.source_order
            ORDER BY first_information_order, s.source_order
            LIMIT $pageSize OFFSET $startIndex;
            """;
        AddLogicalParameters(command, query.DiscoveryOperationId, logical);
        command.Parameters.AddWithValue("$pageSize", query.PageSize);
        command.Parameters.AddWithValue("$startIndex", query.StartIndex);
        var sources = new List<DiscoveredSourceContribution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new DiscoveredSourceContribution(
                SourceId.From(Guid.Parse(reader.GetString(0))),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return new DiscoveryContributorPage(
            query.DiscoveryOperationId,
            query.StartIndex,
            summary.SourceCount,
            summary.TotalOccurrenceCount,
            sources);
    }

    private static SqliteCommand CreateDiscoverySourceInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO discovery_sources (
                operation_id, source_order, source_id, source_name, source_contract_json)
            VALUES ($operationId, $sourceOrder, $sourceId, $sourceName, $sourceContractJson);
            """;
        command.Parameters.Add("$operationId", SqliteType.Text);
        command.Parameters.Add("$sourceOrder", SqliteType.Integer);
        command.Parameters.Add("$sourceId", SqliteType.Text);
        command.Parameters.Add("$sourceName", SqliteType.Text);
        command.Parameters.Add("$sourceContractJson", SqliteType.Text);
        return command;
    }

    private static void SetDiscoverySourceParameters(
        SqliteCommand command,
        OperationId operationId,
        DiscoveryIndexedSource indexedSource)
    {
        command.Parameters["$operationId"].Value = operationId.ToString();
        command.Parameters["$sourceOrder"].Value = indexedSource.SourceOrder;
        command.Parameters["$sourceId"].Value = indexedSource.Source.SourceId.ToString();
        command.Parameters["$sourceName"].Value = indexedSource.SourceName;
        command.Parameters["$sourceContractJson"].Value = JsonSerializer.Serialize(
            indexedSource.Source,
            DiscoveryJsonOptions);
    }

    private static SqliteCommand CreateDiscoveryInformationInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO discovery_information (
                operation_id, information_ordinal, source_set_id, structural_path, information_type,
                candidate_kind, structural_identity, logical_field_identity,
                first_source_order, total_occurrence_count, sample_value)
            VALUES (
                $operationId, $informationOrdinal, $sourceSetId, $structuralPath, $informationType,
                $candidateKind, $structuralIdentity, $logicalFieldIdentity,
                $sourceOrder, $occurrenceCount, $sampleValue)
            ON CONFLICT DO NOTHING;
            """;
        AddDiscoveryContributionParameters(command, includeInformationParameters: true);
        return command;
    }

    private static SqliteCommand CreateDiscoveryContributionInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationId operationId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO discovery_contributions (
                operation_id, information_ordinal, source_order, occurrence_count)
            VALUES ($operationId, $informationOrdinal, $sourceOrder, $occurrenceCount);
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        command.Parameters.Add("$informationOrdinal", SqliteType.Integer);
        command.Parameters.Add("$sourceOrder", SqliteType.Integer);
        command.Parameters.Add("$occurrenceCount", SqliteType.Integer);
        return command;
    }

    private static void AddDiscoveryContributionParameters(
        SqliteCommand command,
        bool includeInformationParameters)
    {
        command.Parameters.Add("$operationId", SqliteType.Text);
        if (includeInformationParameters)
        {
            command.Parameters.Add("$informationOrdinal", SqliteType.Integer);
            command.Parameters.Add("$occurrenceCount", SqliteType.Integer);
            command.Parameters.Add("$sourceSetId", SqliteType.Text);
            command.Parameters.Add("$structuralPath", SqliteType.Text);
            command.Parameters.Add("$informationType", SqliteType.Text);
            command.Parameters.Add("$candidateKind", SqliteType.Integer);
            command.Parameters.Add("$structuralIdentity", SqliteType.Text);
            command.Parameters.Add("$logicalFieldIdentity", SqliteType.Text);
            command.Parameters.Add("$sourceOrder", SqliteType.Integer);
            command.Parameters.Add("$sampleValue", SqliteType.Text);
        }
    }

    private static void SetDiscoveryContributionParameters(
        SqliteCommand command,
        OperationId operationId,
        DiscoveryIndexedSource indexedSource,
        DiscoveryIndexContribution contribution)
    {
        command.Parameters["$operationId"].Value = operationId.ToString();
        command.Parameters["$informationOrdinal"].Value = contribution.InformationOrdinal;
        command.Parameters["$occurrenceCount"].Value = contribution.OccurrenceCount;
        if (command.Parameters.Contains("$logicalFieldIdentity"))
        {
            command.Parameters["$sourceSetId"].Value = contribution.Identity.SourceSetId.ToString();
            command.Parameters["$structuralPath"].Value = contribution.Identity.StructuralPath;
            command.Parameters["$informationType"].Value = contribution.Identity.InformationType;
            command.Parameters["$candidateKind"].Value =
                (int)contribution.Identity.CandidateKind;
            command.Parameters["$structuralIdentity"].Value =
                contribution.Identity.StructuralIdentity;
            command.Parameters["$logicalFieldIdentity"].Value =
                DiscoveryLogicalIdentity.Create(contribution.Identity).CanonicalFieldIdentity;
            command.Parameters["$sourceOrder"].Value = indexedSource.SourceOrder;
            command.Parameters["$sampleValue"].Value = contribution.SampleValue;
        }
    }

    private static async Task RankDiscoveryInformationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE discovery_information
            SET total_occurrence_count = (
                SELECT SUM(c.occurrence_count)
                FROM discovery_contributions c
                WHERE c.operation_id = discovery_information.operation_id
                  AND c.information_ordinal = discovery_information.information_ordinal)
            WHERE operation_id = $operationId;

            WITH ranked AS (
                SELECT rowid,
                       ROW_NUMBER() OVER (
                           ORDER BY first_source_order, structural_identity,
                                    candidate_kind, information_type) - 1 AS ordinal
                FROM discovery_information
                WHERE operation_id = $operationId
            )
            UPDATE discovery_information
            SET presentation_order = (
                SELECT ordinal FROM ranked
                WHERE ranked.rowid = discovery_information.rowid)
            WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DiscoveredInformation>> ReadDiscoveryInformationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationId operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH logical_counts AS (
                SELECT li.source_set_id, li.candidate_kind, li.logical_field_identity,
                       COUNT(DISTINCT c.source_order) AS source_count
                FROM discovery_information li
                JOIN discovery_contributions c
                  ON c.operation_id = li.operation_id
                 AND c.information_ordinal = li.information_ordinal
                WHERE li.operation_id = $operationId
                GROUP BY li.source_set_id, li.candidate_kind, li.logical_field_identity
            )
            SELECT i.source_set_id, i.structural_path, i.information_type,
                   i.candidate_kind, i.structural_identity,
                   i.total_occurrence_count, i.sample_value, lc.source_count
            FROM discovery_information i
            JOIN logical_counts lc
              ON lc.source_set_id = i.source_set_id
             AND lc.candidate_kind = i.candidate_kind
             AND lc.logical_field_identity = i.logical_field_identity
            WHERE i.operation_id = $operationId
            ORDER BY i.presentation_order;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        var information = new List<DiscoveredInformation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            information.Add(new DiscoveredInformation(
                ReadIdentity(reader),
                reader.GetInt32(5),
                reader.GetInt32(7),
                reader.GetString(6)));
        }

        return information;
    }

    private static async Task<IReadOnlyList<DiscoveryInformationDetail>?>
        ValidateDiscoverySelectionAsync(
            SqliteConnection connection,
            OperationId operationId,
            IReadOnlyList<DiscoveryInformationIdentity> identities,
            CancellationToken cancellationToken)
    {
        if (identities.Count == 0)
        {
            return null;
        }

        var logical = DiscoveryLogicalIdentity.Create(identities[0]);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.source_set_id, i.structural_path, i.information_type,
                   i.candidate_kind, i.structural_identity, i.total_occurrence_count,
                   i.information_ordinal
            FROM discovery_information i
            JOIN discovery_publication p ON p.operation_id = i.operation_id
            WHERE p.singleton_id = 1
              AND i.operation_id = $operationId
              AND i.source_set_id = $sourceSetId
              AND i.candidate_kind = $candidateKind
              AND i.logical_field_identity = $logicalFieldIdentity
            ORDER BY i.presentation_order;
            """;
        AddLogicalParameters(command, operationId, logical);
        var published = new List<DiscoveryInformationDetail>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            published.Add(new DiscoveryInformationDetail(
                ReadIdentity(reader),
                reader.GetInt32(5),
                reader.GetInt32(6)));
        }

        return published.Select(item => item.Identity).SequenceEqual(identities)
            ? published
            : null;
    }

    private static async Task<(int SourceCount, int TotalOccurrenceCount)>
        ReadDiscoveryLogicalSummaryAsync(
            SqliteConnection connection,
            OperationId operationId,
            DiscoveryLogicalIdentity logical,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(DISTINCT c.source_order)
                 FROM discovery_information i
                 JOIN discovery_contributions c
                   ON c.operation_id = i.operation_id
                  AND c.information_ordinal = i.information_ordinal
                 JOIN discovery_publication p ON p.operation_id = i.operation_id
                 WHERE p.singleton_id = 1
                   AND i.operation_id = $operationId
                   AND i.source_set_id = $sourceSetId
                   AND i.candidate_kind = $candidateKind
                   AND i.logical_field_identity = $logicalFieldIdentity),
                (SELECT SUM(i.total_occurrence_count)
                 FROM discovery_information i
                 JOIN discovery_publication p ON p.operation_id = i.operation_id
                 WHERE p.singleton_id = 1
                   AND i.operation_id = $operationId
                   AND i.source_set_id = $sourceSetId
                   AND i.candidate_kind = $candidateKind
                   AND i.logical_field_identity = $logicalFieldIdentity);
            """;
        AddLogicalParameters(command, operationId, logical);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(1))
        {
            return (0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static DiscoveryInformationIdentity ReadIdentity(SqliteDataReader reader)
    {
        return new DiscoveryInformationIdentity(
            SourceSetId.From(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            (SourceValueCandidateKind)reader.GetInt32(3),
            reader.GetString(4));
    }

    private static void AddIdentityParameters(
        SqliteCommand command,
        OperationId operationId,
        DiscoveryInformationIdentity identity)
    {
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", identity.SourceSetId.ToString());
        command.Parameters.AddWithValue("$structuralPath", identity.StructuralPath);
        command.Parameters.AddWithValue("$informationType", identity.InformationType);
        command.Parameters.AddWithValue("$candidateKind", (int)identity.CandidateKind);
        command.Parameters.AddWithValue("$structuralIdentity", identity.StructuralIdentity);
    }

    private static void AddLogicalParameters(
        SqliteCommand command,
        OperationId operationId,
        DiscoveryLogicalIdentity logical)
    {
        command.Parameters.AddWithValue("$operationId", operationId.ToString());
        command.Parameters.AddWithValue("$sourceSetId", logical.SourceSetId.ToString());
        command.Parameters.AddWithValue("$candidateKind", (int)logical.CandidateKind);
        command.Parameters.AddWithValue("$logicalFieldIdentity", logical.CanonicalFieldIdentity);
    }

    private static JsonSerializerOptions CreateDiscoveryJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record DiscoveryInformationDetail(
        DiscoveryInformationIdentity Identity,
        int TotalOccurrenceCount,
        int InformationOrdinal);
}

public sealed record DiscoveryIndexContribution(
    int InformationOrdinal,
    DiscoveryInformationIdentity Identity,
    int OccurrenceCount,
    string SampleValue,
    bool DefinesInformation);

public sealed record DiscoveryIndexedSource(
    LoadedSourceContract Source,
    string SourceName,
    int SourceOrder,
    IReadOnlyCollection<DiscoveryIndexContribution> Contributions);

public sealed record DiscoveryOccurrenceResolution(
    DiscoveryInformationIdentity Identity,
    LoadedSourceContract Source,
    string SourceName,
    int LocalOrdinal,
    int ExpectedSourceOccurrenceCount);
