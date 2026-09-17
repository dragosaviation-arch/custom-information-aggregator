using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.Tests;

// Preserves pre-SPR-138 flat-generation fixtures without keeping a flat build path
// in production composition or contracts.
internal sealed class LegacyDatabaseTestBuilder(
    StructuredInformationRepository repository,
    ISourceValueBatchReader sourceValueReader,
    CooperativeOperationCancellation operationCancellation)
{
    private const string PublicationItemId = "database-publication";

    public async Task<DatabaseGenerationHostResult> BuildAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping,
        CancellationToken cancellationToken = default)
    {
        var plans = sources.Select(source => new ProcessingItemPlan(source.SourceId.ToString()))
            .Append(new ProcessingItemPlan(PublicationItemId)).ToArray();
        var operation = operationCancellation.BeginOperation(
            correlation, "DatabaseBuild", "Legacy test Database generation", plans);
        DatabaseCandidate? candidate = null;
        try
        {
            candidate = await repository.CreateDatabaseCandidateAsync(
                correlation, mapping, sources.Select(source => source.SourceId).ToArray(), cancellationToken);
            var selected = mapping.Columns.SelectMany(column => column.SourceInformationTypes)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (!operation.TryStartItem(source.SourceId.ToString(), out var execution))
                {
                    break;
                }

                using (execution)
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                           cancellationToken, execution!.CancellationToken))
                {
                    SourceValueBatchReadResult? readResult = null;
                    try
                    {
                        var committed = await repository.WriteDatabaseCandidateSourceAsync(
                            candidate, source.SourceId,
                            async (writer, token) =>
                            {
                                readResult = await sourceValueReader.ReadSelectedAsync(
                                    source, selected,
                                    values => writer.AddBatchAsync(values.Select(value =>
                                        DatabaseTagMapper.MapValue(
                                            mapping, value.InformationType, value.Content, source.SourceId)!)
                                        .ToArray(), token), token);
                                return readResult.Accepted;
                            }, linked.Token);
                        if (committed)
                        {
                            execution.CommitCompletedResult();
                        }
                        else
                        {
                            execution.RecordFailure(readResult?.Failure?.Code ?? "database-source-generation-failed");
                        }
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        if (!operation.IsCancellationAccepted)
                        {
                            operationCancellation.RequestCancellation(correlation.OperationId);
                        }
                        execution.StopBeforeCommit();
                        break;
                    }
                }
            }

            if (operation.IsCancellationAccepted)
            {
                await DiscardAsync(candidate);
                return Reject(await operation.Completion, "database-generation-cancelled");
            }

            if (!operation.TryStartItem(PublicationItemId, out var publicationExecution))
            {
                await DiscardAsync(candidate);
                return Reject(operation.CompleteTerminal(OperationOutcome.Failed),
                    "database-candidate-not-publishable");
            }

            using (publicationExecution)
            {
                var published = await repository.ValidateAndPublishDatabaseCandidateAsync(
                    candidate, cancellationToken, operation.TryEnterNonCancellableCommitBoundary);
                publicationExecution!.CommitCompletedResult();
                return new DatabaseGenerationHostResult(
                    true, operation.Complete(), published, Failure: null);
            }
        }
        catch (OperationCanceledException)
        {
            operationCancellation.RequestCancellation(correlation.OperationId);
            if (candidate is not null)
            {
                await DiscardAsync(candidate);
            }
            return Reject(await operation.Completion, "database-generation-cancelled");
        }
        catch
        {
            if (candidate is not null)
            {
                await DiscardAsync(candidate);
            }
            foreach (var plan in plans)
            {
                if (operation.TryStartItem(plan.ItemId, out var execution))
                {
                    using (execution)
                    {
                        execution!.RecordFailure("database-candidate-validation-failed");
                    }
                }
            }
            return Reject(operation.CompleteTerminal(OperationOutcome.Failed),
                "database-candidate-validation-failed");
        }
    }

    private static DatabaseGenerationHostResult Reject(
        OperationCompletion completion,
        string code) =>
        new(false, completion, PublishedGeneration: null,
            new IpcFailure(code, "The test candidate was not published."));

    private Task DiscardAsync(DatabaseCandidate candidate) =>
        repository.DiscardDatabaseCandidateAsync(candidate, CancellationToken.None);
}
