using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.Discovery;

public sealed class DiscoveryService
{
    private const int DiscoveryIndexBatchSize = 1_024;
    private readonly ISourceInterpreter _sourceInterpreter;
    private readonly ISourceOccurrenceReader _sourceOccurrenceReader;
    private readonly StructuredInformationRepository _repository;

    public DiscoveryService(
        ISourceInterpreter sourceInterpreter,
        ISourceOccurrenceReader sourceOccurrenceReader,
        StructuredInformationRepository repository)
    {
        ArgumentNullException.ThrowIfNull(sourceInterpreter);
        ArgumentNullException.ThrowIfNull(sourceOccurrenceReader);
        ArgumentNullException.ThrowIfNull(repository);

        _sourceInterpreter = sourceInterpreter;
        _sourceOccurrenceReader = sourceOccurrenceReader;
        _repository = repository;
    }

    public async Task<DiscoveryHostResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(correlation, sources, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DiscoveryHostResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        IProgress<DiscoveryProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            throw new ArgumentException(
                "Discovery requires at least one active source.",
                nameof(sources));
        }

        var itemStatuses = new List<OperationItemStatus>(sources.Count);
        var issues = new List<DiscoverySourceIssue>();
        var usableSourceCount = 0;
        var completedSourceCount = 0;
        var indexBatch = new List<DiscoveryIndexedSource>(DiscoveryIndexBatchSize);
        var discoveredIdentities = new Dictionary<DiscoveryInformationIdentity, int>();
        await _repository.BeginDiscoveryIndexAsync(correlation, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceName = GetSourceName(source);
                var interpretation = await _sourceInterpreter
                    .InterpretAsync(source, cancellationToken)
                    .ConfigureAwait(false);

                if (interpretation.Status == SourceInterpretationStatus.Usable)
                {
                    indexBatch.Add(new DiscoveryIndexedSource(
                        source,
                        sourceName,
                        usableSourceCount,
                        CreateContributions(
                            interpretation.Source!,
                            source.SourceSetId,
                            discoveredIdentities)));
                    usableSourceCount++;
                    if (indexBatch.Count == DiscoveryIndexBatchSize)
                    {
                        await FlushIndexBatchAsync(
                                correlation.OperationId,
                                indexBatch,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    itemStatuses.Add(OperationItemStatus.ProcessedSuccessfully(
                        source.SourceId.ToString()));
                    ReportProgress(progress, ++completedSourceCount, sources.Count);
                    continue;
                }

                var failure = interpretation.Failure!;
                itemStatuses.Add(OperationItemStatus.Failed(
                    source.SourceId.ToString(),
                    failure.Code));
                issues.Add(new DiscoverySourceIssue(
                    source.SourceId,
                    failure.Code,
                    failure.Description));
                ReportProgress(progress, ++completedSourceCount, sources.Count);
            }

            if (usableSourceCount == 0)
            {
                return DiscoveryHostResult.Reject(
                    OperationCompletion.FromTerminalOutcome(
                        correlation,
                        OperationOutcome.Failed,
                        itemStatuses),
                    issues,
                    "discovery-no-usable-sources",
                    "Discovery could not interpret any source in the active source set.");
            }

            await FlushIndexBatchAsync(
                    correlation.OperationId,
                    indexBatch,
                    cancellationToken)
                .ConfigureAwait(false);

            var information = await _repository.PublishDiscoveryIndexAsync(
                    correlation.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            var completion = OperationCompletion.FromCompletedItems(correlation, itemStatuses);
            return DiscoveryHostResult.Accept(
                information,
                issues,
                completion);
        }
        finally
        {
            await _repository.DiscardDiscoveryIndexAsync(
                    correlation.OperationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static void ReportProgress(
        IProgress<DiscoveryProgressSnapshot>? progress,
        int completedSourceCount,
        int totalSourceCount)
    {
        try
        {
            progress?.Report(new DiscoveryProgressSnapshot(completedSourceCount, totalSourceCount));
        }
        catch (Exception)
        {
            // Progress observers cannot alter the Discovery outcome.
        }
    }

    public async Task<DiscoveryOccurrenceHostResult> GetOccurrenceAsync(
        DiscoveryOccurrenceLookup lookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (lookup.DiscoveryOperationId == default)
        {
            throw new ArgumentException(
                "A Discovery occurrence request requires an Operation ID.",
                nameof(lookup));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(lookup.InformationType);

        if (lookup.GlobalOrdinal < 1
            || lookup.TotalOccurrenceCount < lookup.GlobalOrdinal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lookup),
                "Discovery occurrence lookup ordinals are invalid.");
        }

        var resolution = await _repository.ResolveDiscoveryOccurrenceAsync(
                lookup,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution is null)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "discovery-preview-out-of-date",
                "The requested Discovery result is no longer published or its identity has changed.");
        }

        var read = await _sourceOccurrenceReader
            .ReadAsync(
                resolution.Source,
                resolution.Identity.InformationType,
                resolution.Identity.StructuralPath,
                resolution.Identity.CandidateKind,
                resolution.Identity.StructuralIdentity,
                resolution.LocalOrdinal,
                cancellationToken)
            .ConfigureAwait(false);
        if (!read.Accepted)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                read.Failure!.Code,
                read.Failure.Description);
        }

        if (read.ActualOccurrenceCount != resolution.ExpectedSourceOccurrenceCount
            || read.Value is null)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "discovery-preview-out-of-date",
                "The source occurrence count no longer matches the published Discovery result.");
        }

        return DiscoveryOccurrenceHostResult.Accept(
            new DiscoveredOccurrence(
                resolution.Identity,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                resolution.Source.SourceId,
                resolution.SourceName,
                read.Value));
    }

    private async Task FlushIndexBatchAsync(
        OperationId operationId,
        List<DiscoveryIndexedSource> indexBatch,
        CancellationToken cancellationToken)
    {
        if (indexBatch.Count == 0)
        {
            return;
        }

        await _repository.AddDiscoverySourcesAsync(
                operationId,
                indexBatch,
                cancellationToken)
            .ConfigureAwait(false);
        indexBatch.Clear();
    }

    public async Task<DiscoveryContributorHostResult> GetContributorsAsync(
        DiscoveryContributorPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await _repository.ReadDiscoveryContributorsAsync(query, cancellationToken)
            .ConfigureAwait(false);
        return page is null
            ? DiscoveryContributorHostResult.Reject(
                "discovery-contributors-out-of-date",
                "The requested Discovery contributor index is no longer current.")
            : DiscoveryContributorHostResult.Accept(page);
    }

    private static IReadOnlyCollection<DiscoveryIndexContribution> CreateContributions(
        InterpretedSourceDocument source,
        SourceSetId sourceSetId,
        Dictionary<DiscoveryInformationIdentity, int> discoveredIdentities)
    {
        var contributions = new Dictionary<DiscoveryInformationIdentity, SourceContribution>();
        foreach (var value in source.Values)
        {
            var identity = new DiscoveryInformationIdentity(
                sourceSetId,
                value.Lineage?.StructuralPath ?? $"/{value.InformationType}",
                value.InformationType,
                value.CandidateKind,
                value.StructuralIdentity);
            if (!contributions.TryGetValue(identity, out var contribution))
            {
                contribution = new SourceContribution(
                    identity,
                    DiscoverySampleValueFormatter.Format(value.Content));
                contributions.Add(identity, contribution);
            }

            contribution.OccurrenceCount++;
        }

        return contributions.Values.Select(contribution =>
        {
            var definesInformation = !discoveredIdentities.TryGetValue(
                contribution.Identity,
                out var informationOrdinal);
            if (definesInformation)
            {
                informationOrdinal = discoveredIdentities.Count;
                discoveredIdentities.Add(contribution.Identity, informationOrdinal);
            }

            return new DiscoveryIndexContribution(
                informationOrdinal,
                contribution.Identity,
                contribution.OccurrenceCount,
                contribution.SampleValue,
                definesInformation);
        }).ToArray();
    }

    private sealed class SourceContribution(
        DiscoveryInformationIdentity identity,
        string sampleValue)
    {
        public DiscoveryInformationIdentity Identity { get; } = identity;
        public string SampleValue { get; } = sampleValue;
        public int OccurrenceCount { get; set; }
    }

    private static string GetSourceName(LoadedSourceContract source)
    {
        var name = source.ArchiveProvenance?.ArchiveMemberPath;
        return string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(source.Path)
            : name;
    }
}

public sealed record DiscoveryHostResult(
    bool Accepted,
    IReadOnlyList<DiscoveredInformation> Information,
    IReadOnlyList<DiscoverySourceIssue> Issues,
    OperationCompletion Completion,
    IpcFailure? Failure)
{
    internal static DiscoveryHostResult Accept(
        IReadOnlyList<DiscoveredInformation> information,
        IReadOnlyList<DiscoverySourceIssue> issues,
        OperationCompletion completion)
    {
        return new DiscoveryHostResult(true, information, issues, completion, Failure: null);
    }

    internal static DiscoveryHostResult Reject(
        OperationCompletion completion,
        IReadOnlyList<DiscoverySourceIssue> issues,
        string code,
        string description)
    {
        return new DiscoveryHostResult(
            false,
            Array.Empty<DiscoveredInformation>(),
            issues,
            completion,
            new IpcFailure(code, description));
    }
}

public sealed record DiscoveryOccurrenceHostResult(
    bool Accepted,
    DiscoveredOccurrence? Occurrence,
    IpcFailure? Failure)
{
    internal static DiscoveryOccurrenceHostResult Accept(DiscoveredOccurrence occurrence)
    {
        return new DiscoveryOccurrenceHostResult(true, occurrence, Failure: null);
    }

    internal static DiscoveryOccurrenceHostResult Reject(string code, string description)
    {
        return new DiscoveryOccurrenceHostResult(
            false,
            Occurrence: null,
            new IpcFailure(code, description));
    }
}

public sealed record DiscoveryContributorHostResult(
    bool Accepted,
    DiscoveryContributorPage? Page,
    IpcFailure? Failure)
{
    internal static DiscoveryContributorHostResult Accept(DiscoveryContributorPage page)
    {
        return new DiscoveryContributorHostResult(true, page, Failure: null);
    }

    internal static DiscoveryContributorHostResult Reject(string code, string description)
    {
        return new DiscoveryContributorHostResult(
            false,
            Page: null,
            new IpcFailure(code, description));
    }
}
