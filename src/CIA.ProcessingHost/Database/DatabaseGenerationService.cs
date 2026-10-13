using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Database;

public sealed class DatabaseGenerationService(
    StructuredInformationRepository repository,
    ISourceInterpreter sourceInterpreter,
    DatabaseSourceProjectionService sourceProjection,
    CooperativeOperationCancellation operationCancellation,
    ILogger<DatabaseGenerationService> logger)
{
    private const string PublicationItemId = "database-publication";

    public DatabaseGenerationService(
        StructuredInformationRepository repository,
        ISourceInterpreter sourceInterpreter,
        CIA.Core.Hierarchy.IHierarchyFlatteningEngine hierarchyFlattening,
        CooperativeOperationCancellation operationCancellation,
        ILogger<DatabaseGenerationService> logger)
        : this(
            repository,
            sourceInterpreter,
            new DatabaseSourceProjectionService(hierarchyFlattening),
            operationCancellation,
            logger)
    {
    }

    public async Task<DatabaseGenerationHostResult> BuildAsync(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default,
        IProgress<DatabaseBuildProgressSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(specification);
        var parentDatasets = specification.Datasets
            .GroupBy(dataset => dataset.ParentSourceSetId)
            .Select(group => new
            {
                ParentSourceSetId = group.Key,
                Datasets = group.OrderBy(dataset => dataset.Ordinal).ToArray(),
                Sources = group.First().Sources
            })
            .ToArray();
        var sources = parentDatasets.SelectMany(parent => parent.Sources).ToArray();
        var plans = sources.Select(source => new ProcessingItemPlan(source.SourceId.ToString()))
            .Append(new ProcessingItemPlan(PublicationItemId)).ToArray();
        var operation = operationCancellation.BeginOperation(
            correlation, "DatabaseBuild", "Hierarchy-aware candidate Database generation", plans);
        DatabaseCandidate? candidate = null;
        var sourceContributionFailed = false;
        var completedWorkCount = 0;
        ReportProgress(progress, new DatabaseBuildProgressSnapshot(
            correlation.OperationId,
            "Preparing candidate Database",
            completedWorkCount,
            plans.Length));
        try
        {
            candidate = await repository.CreateDatabaseCandidateAsync(
                correlation, specification, cancellationToken).ConfigureAwait(false);

            foreach (var parent in parentDatasets)
            {
                foreach (var source in parent.Sources)
                {
                    if (!operation.TryStartItem(source.SourceId.ToString(), out var execution))
                    {
                        sourceContributionFailed = !operation.IsCancellationAccepted;
                        break;
                    }

                    var sourceWorkCompleted = false;
                    using (execution)
                    using (var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                               cancellationToken, execution!.CancellationToken))
                    {
                        try
                        {
                            var interpretation = await sourceInterpreter.InterpretAsync(
                                source, sourceCancellation.Token).ConfigureAwait(false);
                            if (interpretation.Status != SourceInterpretationStatus.Usable
                                || interpretation.Source is null)
                            {
                                sourceContributionFailed = true;
                                execution.RecordFailure(
                                    interpretation.Failure?.Code ?? "database-source-interpretation-failed");
                                sourceWorkCompleted = true;
                                continue;
                            }

                            foreach (var dataset in parent.Datasets)
                            {
                                var projection = await sourceProjection.ProjectAsync(
                                    dataset,
                                    source,
                                    interpretation.Source,
                                    cancellationToken: sourceCancellation.Token).ConfigureAwait(false);
                                if (dataset.OutputSetId is { } outputSetId)
                                {
                                    await repository.WriteOutputSetDatabaseCandidateSourceAsync(
                                        candidate,
                                        outputSetId,
                                        source.SourceId,
                                        projection.Rows,
                                        sourceCancellation.Token).ConfigureAwait(false);
                                }
                                else
                                {
                                    await repository.WriteHierarchyDatabaseCandidateSourceAsync(
                                        candidate,
                                        dataset.ParentSourceSetId,
                                        source.SourceId,
                                        projection.Rows,
                                        sourceCancellation.Token).ConfigureAwait(false);
                                }
                            }
                            execution.CommitCompletedResult();
                            sourceWorkCompleted = true;
                        }
                        catch (OperationCanceledException) when (sourceCancellation.IsCancellationRequested)
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
                            sourceContributionFailed = true;
                            logger.LogWarning(exception,
                                "Source {SourceId} could not contribute hierarchy-aware rows to Database candidate {OperationId}",
                                source.SourceId, correlation.OperationId);
                            execution.RecordFailure("database-source-generation-failed");
                            sourceWorkCompleted = true;
                        }
                        finally
                        {
                            if (sourceWorkCompleted)
                            {
                                completedWorkCount++;
                                ReportProgress(progress, new DatabaseBuildProgressSnapshot(
                                    correlation.OperationId,
                                    $"Processed {completedWorkCount} of {sources.Length} sources",
                                    completedWorkCount,
                                    plans.Length));
                            }
                        }
                    }
                }
            }

            if (operation.IsCancellationAccepted)
            {
                await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
                return DatabaseGenerationHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "database-generation-cancelled", "Database generation was cancelled before publication.");
            }

            if (sourceContributionFailed)
            {
                await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
                FailRemaining(operation, plans, "database-source-contribution-incomplete");
                return DatabaseGenerationHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "database-source-contribution-incomplete",
                    "The candidate Database omitted a required source contribution and was not published.");
            }

            if (!operation.TryStartItem(PublicationItemId, out var publicationExecution))
            {
                await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
                FailRemaining(operation, plans, "database-candidate-not-publishable");
                return DatabaseGenerationHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "database-candidate-not-publishable", "The candidate Database could not enter publication.");
            }

            ReportProgress(progress, new DatabaseBuildProgressSnapshot(
                correlation.OperationId,
                "Publishing Database generation",
                completedWorkCount,
                plans.Length));

            using (publicationExecution)
            using (var publicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, publicationExecution!.CancellationToken))
            {
                try
                {
                    var published = await repository.ValidateAndPublishHierarchyDatabaseCandidateAsync(
                        candidate, publicationCancellation.Token,
                        operation.TryEnterNonCancellableCommitBoundary).ConfigureAwait(false);
                    publicationExecution.CommitCompletedResult();
                    completedWorkCount++;
                    ReportProgress(progress, new DatabaseBuildProgressSnapshot(
                        correlation.OperationId,
                        "Published Database generation",
                        completedWorkCount,
                        plans.Length));
                    return DatabaseGenerationHostResult.Accept(published, operation.Complete());
                }
                catch (OperationCanceledException) when (publicationCancellation.IsCancellationRequested)
                {
                    if (!operation.IsCancellationAccepted)
                    {
                        operationCancellation.RequestCancellation(correlation.OperationId);
                    }
                    publicationExecution.StopBeforeCommit();
                    await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
                    return DatabaseGenerationHostResult.Reject(
                        await operation.Completion.ConfigureAwait(false),
                        "database-generation-cancelled", "Database generation was cancelled before publication.");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "Hierarchy-aware Database candidate {OperationId} failed validation or publication",
                        correlation.OperationId);
                    publicationExecution.RecordFailure("database-candidate-validation-failed");
                    await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
                    return DatabaseGenerationHostResult.Reject(
                        operation.CompleteTerminal(OperationOutcome.Failed),
                        "database-candidate-validation-failed",
                        "The candidate Database failed validation and was not published.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.RequestCancellation(correlation.OperationId);
            if (candidate is not null)
            {
                await DiscardWithoutMaskingAsync(candidate).ConfigureAwait(false);
            }
            return DatabaseGenerationHostResult.Reject(
                await operation.Completion.ConfigureAwait(false),
                "database-generation-cancelled", "Database generation was cancelled before publication.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database candidate {OperationId} could not be initialized", correlation.OperationId);
            FailRemaining(operation, plans, "database-candidate-initialization-failed");
            return DatabaseGenerationHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "database-candidate-initialization-failed", "The candidate Database could not be initialized.");
        }
    }

    private static void ReportProgress(
        IProgress<DatabaseBuildProgressSnapshot>? progress,
        DatabaseBuildProgressSnapshot snapshot)
    {
        try
        {
            progress?.Report(snapshot);
        }
        catch (Exception)
        {
            // Progress observers cannot alter the Database-build outcome.
        }
    }

    private static void FailRemaining(
        CooperativeProcessingOperation operation,
        IEnumerable<ProcessingItemPlan> plans,
        string code)
    {
        foreach (var plan in plans)
        {
            if (operation.TryStartItem(plan.ItemId, out var execution))
            {
                using (execution)
                {
                    execution!.RecordFailure(code);
                }
            }
        }
    }

    private async Task DiscardWithoutMaskingAsync(DatabaseCandidate candidate)
    {
        try
        {
            await repository.DiscardDatabaseCandidateAsync(candidate, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
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
        OperationCompletion completion) =>
        new(true, completion, publishedGeneration, Failure: null);

    internal static DatabaseGenerationHostResult Reject(
        OperationCompletion completion,
        string failureCode,
        string failureDescription) =>
        new(false, completion, PublishedGeneration: null, new IpcFailure(failureCode, failureDescription));
}
