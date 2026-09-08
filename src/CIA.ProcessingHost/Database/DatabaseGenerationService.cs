using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Database;

public sealed class DatabaseGenerationService(
    StructuredInformationRepository repository,
    ISourceValueBatchReader sourceValueReader,
    CooperativeOperationCancellation operationCancellation,
    ILogger<DatabaseGenerationService> logger)
{
    private const string PublicationItemId = "database-publication";

    public async Task<DatabaseGenerationHostResult> BuildAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(mapping);

        if (sources.Count == 0 || mapping.Columns.Count == 0)
        {
            throw new ArgumentException(
                "Database generation requires sources and a selected mapped schema.");
        }

        var plans = sources
            .Select(source => new ProcessingItemPlan(source.SourceId.ToString()))
            .Append(new ProcessingItemPlan(PublicationItemId))
            .ToArray();
        var operation = operationCancellation.BeginOperation(
            correlation,
            "DatabaseBuild",
            "Candidate Database generation",
            plans);
        DatabaseCandidate? candidate = null;

        try
        {
            candidate = await repository.CreateDatabaseCandidateAsync(
                    correlation,
                    mapping,
                    sources.Select(source => source.SourceId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.RequestCancellation(correlation.OperationId);
            return DatabaseGenerationHostResult.Reject(
                await operation.Completion.ConfigureAwait(false),
                "database-generation-cancelled",
                "Database generation was cancelled before publication.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Database candidate {OperationId} could not be initialized",
                correlation.OperationId);
            FailRemainingItems(operation, plans, "database-candidate-initialization-failed");
            return DatabaseGenerationHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "database-candidate-initialization-failed",
                "The candidate Database could not be initialized.");
        }

        var selectedInformationTypes = mapping.Columns
            .SelectMany(column => column.SourceInformationTypes)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (!operation.TryStartItem(source.SourceId.ToString(), out var execution))
            {
                break;
            }

            using (execution)
            using (var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       execution!.CancellationToken))
            {
                SourceValueBatchReadResult? readResult = null;
                try
                {
                    var committed = await repository.WriteDatabaseCandidateSourceAsync(
                            candidate,
                            source.SourceId,
                            async (writer, writeCancellation) =>
                            {
                                readResult = await sourceValueReader.ReadSelectedAsync(
                                        source,
                                        selectedInformationTypes,
                                        async values =>
                                        {
                                            var mappedValues = values
                                                .Select(value => DatabaseTagMapper.MapValue(
                                                    mapping,
                                                    value.InformationType,
                                                    value.Content,
                                                    source.SourceId)!)
                                                .ToArray();
                                            await writer.AddBatchAsync(
                                                    mappedValues,
                                                    writeCancellation)
                                                .ConfigureAwait(false);
                                        },
                                        writeCancellation)
                                    .ConfigureAwait(false);
                                return readResult.Accepted;
                            },
                            sourceCancellation.Token)
                        .ConfigureAwait(false);

                    if (committed)
                    {
                        execution.CommitCompletedResult();
                    }
                    else
                    {
                        execution.RecordFailure(
                            readResult?.Failure?.Code ?? "database-source-generation-failed");
                    }
                }
                catch (OperationCanceledException)
                    when (sourceCancellation.IsCancellationRequested)
                {
                    if (!operation.IsCancellationAccepted)
                    {
                        operationCancellation.RequestCancellation(correlation.OperationId);
                    }

                    execution.StopBeforeCommit();
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Source {SourceId} could not contribute to Database candidate {OperationId}",
                        source.SourceId,
                        correlation.OperationId);
                    execution.RecordFailure("database-source-generation-failed");
                }
            }
        }

        if (operation.IsCancellationAccepted)
        {
            await DiscardCandidateWithoutMaskingAsync(candidate).ConfigureAwait(false);
            return DatabaseGenerationHostResult.Reject(
                await operation.Completion.ConfigureAwait(false),
                "database-generation-cancelled",
                "Database generation was cancelled before publication.");
        }

        if (!operation.TryStartItem(PublicationItemId, out var publicationExecution))
        {
            await DiscardCandidateWithoutMaskingAsync(candidate).ConfigureAwait(false);
            FailRemainingItems(operation, plans, "database-candidate-not-publishable");
            return DatabaseGenerationHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "database-candidate-not-publishable",
                "The candidate Database could not enter the publication boundary.");
        }

        using (publicationExecution)
        using (var publicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   publicationExecution!.CancellationToken))
        {
            try
            {
                var published = await repository.ValidateAndPublishDatabaseCandidateAsync(
                        candidate,
                        publicationCancellation.Token,
                        operation.TryEnterNonCancellableCommitBoundary)
                    .ConfigureAwait(false);
                publicationExecution.CommitCompletedResult();
                var completion = operation.Complete();
                return DatabaseGenerationHostResult.Accept(published, completion);
            }
            catch (OperationCanceledException)
                when (publicationCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    operationCancellation.RequestCancellation(correlation.OperationId);
                }

                publicationExecution.StopBeforeCommit();
                await DiscardCandidateWithoutMaskingAsync(candidate).ConfigureAwait(false);
                return DatabaseGenerationHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "database-generation-cancelled",
                    "Database generation was cancelled before publication.");
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Database candidate {OperationId} failed validation or publication",
                    correlation.OperationId);
                publicationExecution.RecordFailure("database-candidate-validation-failed");
                await DiscardCandidateWithoutMaskingAsync(candidate).ConfigureAwait(false);
                return DatabaseGenerationHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "database-candidate-validation-failed",
                    "The candidate Database failed validation and was not published.");
            }
        }
    }

    private static void FailRemainingItems(
        CooperativeProcessingOperation operation,
        IEnumerable<ProcessingItemPlan> plans,
        string failureCode)
    {
        foreach (var plan in plans)
        {
            if (operation.TryStartItem(plan.ItemId, out var execution))
            {
                using (execution)
                {
                    execution!.RecordFailure(failureCode);
                }
            }
        }
    }

    private async Task DiscardCandidateWithoutMaskingAsync(DatabaseCandidate candidate)
    {
        try
        {
            await repository.DiscardDatabaseCandidateAsync(candidate, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Discarded Database candidate {OperationId} could not be cleaned immediately",
                candidate.Correlation.OperationId);
        }
    }
}

public sealed record DatabaseGenerationHostResult(
    bool Accepted,
    OperationCompletion Completion,
    DatabaseGenerationSummary? PublishedGeneration,
    IpcFailure? Failure)
{
    internal static DatabaseGenerationHostResult Accept(
        DatabaseGenerationSummary publishedGeneration,
        OperationCompletion completion)
    {
        return new DatabaseGenerationHostResult(
            true,
            completion,
            publishedGeneration,
            Failure: null);
    }

    internal static DatabaseGenerationHostResult Reject(
        OperationCompletion completion,
        string failureCode,
        string failureDescription)
    {
        return new DatabaseGenerationHostResult(
            false,
            completion,
            PublishedGeneration: null,
            new IpcFailure(failureCode, failureDescription));
    }
}
