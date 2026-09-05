using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.Discovery;

public sealed class DiscoveryService(ISourceInterpreter sourceInterpreter)
{
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

        var completion = OperationCompletion.FromCompletedItems(correlation, itemStatuses);
        return DiscoveryHostResult.Accept(
            Aggregate(interpretedSources, sourceNames),
            issues,
            completion);
    }

    private static IReadOnlyList<DiscoveredInformation> Aggregate(
        IEnumerable<InterpretedSourceDocument> sources,
        IReadOnlyDictionary<SourceId, string> sourceNames)
    {
        return sources
            .SelectMany(source => source.Values.Select(value => new DiscoveryOccurrence(
                value.InformationType,
                value.Content,
                source.OriginatingSourceId)))
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

    private sealed record DiscoveryOccurrence(
        string InformationType,
        string Value,
        SourceId SourceId);
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
