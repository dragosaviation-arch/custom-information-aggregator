using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.Discovery;

public sealed class DiscoveryService(ISourceInterpreter sourceInterpreter)
{
    private PublishedDiscovery? _publishedDiscovery;

    public async Task<DiscoveryHostResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
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

        var interpretedSources = new List<InterpretedSourceDocument>();
        var sourceNames = new Dictionary<SourceId, string>();
        var itemStatuses = new List<OperationItemStatus>(sources.Count);
        var issues = new List<DiscoverySourceIssue>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceName = GetSourceName(source);
            sourceNames.Add(source.SourceId, sourceName);
            var interpretation = await sourceInterpreter
                .InterpretAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (interpretation.Status == SourceInterpretationStatus.Usable)
            {
                interpretedSources.Add(interpretation.Source!);
                itemStatuses.Add(OperationItemStatus.ProcessedSuccessfully(
                    source.SourceId.ToString()));
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
        }

        if (interpretedSources.Count == 0)
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

        var occurrences = CreateOccurrences(interpretedSources);
        var information = Aggregate(occurrences, sourceNames);
        var completion = OperationCompletion.FromCompletedItems(correlation, itemStatuses);
        _publishedDiscovery = PublishedDiscovery.Create(correlation.OperationId, occurrences);
        return DiscoveryHostResult.Accept(
            information,
            issues,
            completion);
    }

    public DiscoveryOccurrenceHostResult GetOccurrence(
        OperationId discoveryOperationId,
        string informationType,
        int ordinal)
    {
        if (discoveryOperationId == default)
        {
            throw new ArgumentException(
                "A Discovery occurrence request requires an Operation ID.",
                nameof(discoveryOperationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        var publishedDiscovery = _publishedDiscovery;
        if (publishedDiscovery is null
            || publishedDiscovery.OperationId != discoveryOperationId)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "discovery-result-unavailable",
                "The requested Discovery result is not available in the Processing Host.");
        }

        if (!publishedDiscovery.Occurrences.TryGetValue(informationType, out var occurrences))
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "discovery-information-unavailable",
                "The requested information identity is not available in the Discovery result.");
        }

        if (ordinal < 1 || ordinal > occurrences.Count)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "occurrence-ordinal-out-of-range",
                "The requested occurrence ordinal is outside the available range.");
        }

        var occurrence = occurrences[ordinal - 1];
        return DiscoveryOccurrenceHostResult.Accept(
            new DiscoveredOccurrence(
                occurrence.InformationType,
                ordinal,
                occurrences.Count,
                occurrence.SourceId,
                occurrence.Value));
    }

    private static IReadOnlyList<DiscoveredOccurrenceValue> CreateOccurrences(
        IEnumerable<InterpretedSourceDocument> sources)
    {
        return sources
            .SelectMany(source => source.Values.Select(value => new DiscoveredOccurrenceValue(
                value.InformationType,
                value.Content,
                source.OriginatingSourceId)))
            .ToArray();
    }

    private static IReadOnlyList<DiscoveredInformation> Aggregate(
        IEnumerable<DiscoveredOccurrenceValue> occurrences,
        IReadOnlyDictionary<SourceId, string> sourceNames)
    {
        return occurrences
            .GroupBy(occurrence => occurrence.InformationType, StringComparer.Ordinal)
            .Select(group => new DiscoveredInformation(
                group.Key,
                group.Count(),
                group.GroupBy(occurrence => occurrence.SourceId)
                    .Select(sourceGroup => new DiscoveredSourceContribution(
                        sourceGroup.Key,
                        sourceNames[sourceGroup.Key],
                        sourceGroup.Count()))
                    .OrderBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.SourceId.ToString(), StringComparer.Ordinal)
                    .ToArray(),
                group.First().Value))
            .OrderBy(information => information.InformationType, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetSourceName(LoadedSourceContract source)
    {
        var name = source.ArchiveProvenance?.ArchiveMemberPath;
        return string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(source.Path)
            : name;
    }

    private sealed record DiscoveredOccurrenceValue(
        string InformationType,
        string Value,
        SourceId SourceId);

    private sealed record PublishedDiscovery(
        OperationId OperationId,
        IReadOnlyDictionary<string, IReadOnlyList<DiscoveredOccurrenceValue>> Occurrences)
    {
        public static PublishedDiscovery Create(
            OperationId operationId,
            IEnumerable<DiscoveredOccurrenceValue> occurrences)
        {
            return new PublishedDiscovery(
                operationId,
                occurrences
                    .GroupBy(occurrence => occurrence.InformationType, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<DiscoveredOccurrenceValue>)group.ToArray(),
                        StringComparer.Ordinal));
        }
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
