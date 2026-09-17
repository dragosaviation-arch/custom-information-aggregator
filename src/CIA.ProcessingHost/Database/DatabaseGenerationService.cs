using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Hierarchy;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Database;

public sealed class DatabaseGenerationService(
    StructuredInformationRepository repository,
    ISourceInterpreter sourceInterpreter,
    IHierarchyFlatteningEngine hierarchyFlattening,
    CooperativeOperationCancellation operationCancellation,
    ILogger<DatabaseGenerationService> logger)
{
    private const string PublicationItemId = "database-publication";

    public async Task<DatabaseGenerationHostResult> BuildAsync(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(specification);
        var sources = specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray();
        var plans = sources.Select(source => new ProcessingItemPlan(source.SourceId.ToString()))
            .Append(new ProcessingItemPlan(PublicationItemId)).ToArray();
        var operation = operationCancellation.BeginOperation(
            correlation, "DatabaseBuild", "Hierarchy-aware candidate Database generation", plans);
        DatabaseCandidate? candidate = null;
        var sourceContributionFailed = false;
        try
        {
            candidate = await repository.CreateDatabaseCandidateAsync(
                correlation, specification, cancellationToken).ConfigureAwait(false);

            foreach (var dataset in specification.Datasets)
            {
                var selectedIdentities = dataset.Fields.SelectMany(field => field.DetailedIdentities).ToArray();
                foreach (var source in dataset.Sources)
                {
                    if (!operation.TryStartItem(source.SourceId.ToString(), out var execution))
                    {
                        sourceContributionFailed = !operation.IsCancellationAccepted;
                        break;
                    }

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
                                continue;
                            }

                            var input = HierarchySourceInput.FromInterpretedSource(
                                dataset.SourceSetId, interpretation.Source, selectedIdentities);
                            var flattened = await hierarchyFlattening.FlattenAsync(
                                new HierarchyFlatteningRequest(
                                    dataset.SourceSetId, dataset.RepeatedDataLayout, [input]),
                                sourceCancellation.Token).ConfigureAwait(false);
                            if (flattened.Rows.Any(row => row.SourceId != source.SourceId))
                            {
                                throw new InvalidOperationException(
                                    "Hierarchy flattening returned rows outside the source boundary.");
                            }

                            var metadata = CreateSourceMetadata(dataset, source);
                            var rows = flattened.Rows.Select(row => CreateDatabaseRow(
                                dataset, source, metadata, row)).ToArray();
                            await repository.WriteHierarchyDatabaseCandidateSourceAsync(
                                candidate, dataset.SourceSetId, source.SourceId, rows,
                                sourceCancellation.Token).ConfigureAwait(false);
                            execution.CommitCompletedResult();
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

    private static DatabaseReviewRow CreateDatabaseRow(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source,
        DatabaseSourceMetadata metadata,
        FlattenedHierarchyRow row)
    {
        var mapped = row.Cells.Select(cell => new
        {
            Cell = cell,
            Mapping = DatabaseTagMapper.FindMapping(dataset.Fields, cell.DetailedIdentity),
            Coordinates = new DatabaseRepeatCoordinatePath(cell.ColumnIdentity.RepeatCoordinates.Coordinates)
        })
            .GroupBy(item => new DatabaseColumnIdentity(
                dataset.SourceSetId, item.Mapping.FieldKey, item.Coordinates));
        var cells = mapped.Select(group =>
        {
            var values = group.Select(item => new DatabaseReviewValue(
                item.Cell.Value,
                item.Cell.DetailedIdentity,
                item.Cell.SourceId,
                new DatabaseLineageEvidence(
                    item.Cell.Lineage.StructuralPath,
                    item.Cell.Lineage.NodeInstanceId,
                    item.Cell.Lineage.ParentInstanceId,
                    item.Cell.Lineage.AncestorInstanceIds,
                    item.Cell.Lineage.TraversalOrder,
                    item.Cell.Lineage.ElementPath.Select(element => new DatabaseSourceElementEvidence(
                        element.LocalName, element.NamespaceUri, element.QualifiedName,
                        element.InstanceId, element.SiblingPosition)).ToArray()),
                group.Key.RepeatCoordinates)).ToArray();
            var conflict = values.Select(value => value.Value)
                .Distinct(StringComparer.Ordinal).Skip(1).Any();
            return new DatabaseReviewCell(group.Key, conflict, values);
        }).ToArray();
        return new DatabaseReviewRow(
            row.Ordinal, true, $"{source.SourceId}:{row.Ordinal}", metadata, cells);
    }

    private static DatabaseSourceMetadata CreateSourceMetadata(
        DatabaseDatasetBuildSpecification dataset,
        LoadedSourceContract source)
    {
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
        return new DatabaseSourceMetadata(
            dataset.SourceSetId, dataset.DisplayName, source.SourceId,
            Path.GetFileName(fullPath), fullPath, source.Kind,
            source.ArchiveProvenance?.OriginalArchivePath,
            source.ArchiveProvenance?.ArchiveMemberPath, modifiedUtc);
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
