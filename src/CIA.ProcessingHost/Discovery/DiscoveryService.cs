using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.Discovery;

public sealed class DiscoveryService
{
    private readonly ISourceInterpreter _sourceInterpreter;
    private readonly ISourceOccurrenceReader _sourceOccurrenceReader;

    public DiscoveryService(
        ISourceInterpreter sourceInterpreter,
        ISourceOccurrenceReader sourceOccurrenceReader)
    {
        ArgumentNullException.ThrowIfNull(sourceInterpreter);
        ArgumentNullException.ThrowIfNull(sourceOccurrenceReader);

        _sourceInterpreter = sourceInterpreter;
        _sourceOccurrenceReader = sourceOccurrenceReader;
    }

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

        var aggregation = new DiscoveryAggregation();
        var itemStatuses = new List<OperationItemStatus>(sources.Count);
        var issues = new List<DiscoverySourceIssue>();
        var usableSourceCount = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceName = GetSourceName(source);
            var interpretation = await _sourceInterpreter
                .InterpretAsync(source, cancellationToken)
                .ConfigureAwait(false);

            if (interpretation.Status == SourceInterpretationStatus.Usable)
            {
                aggregation.AddSource(
                    interpretation.Source!,
                    sourceName,
                    usableSourceCount);
                usableSourceCount++;
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

        var information = aggregation.CreateInformation();
        var completion = OperationCompletion.FromCompletedItems(correlation, itemStatuses);
        return DiscoveryHostResult.Accept(
            information,
            issues,
            completion);
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
        ArgumentNullException.ThrowIfNull(lookup.Source);

        if (lookup.GlobalOrdinal < 1
            || lookup.TotalOccurrenceCount < lookup.GlobalOrdinal
            || lookup.LocalOrdinal < 1
            || lookup.ExpectedSourceOccurrenceCount < lookup.LocalOrdinal
            || lookup.ExpectedSourceOccurrenceCount > lookup.TotalOccurrenceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lookup),
                "Discovery occurrence lookup ordinals are invalid.");
        }

        var read = await _sourceOccurrenceReader
            .ReadAsync(
                lookup.Source,
                lookup.InformationType,
                lookup.LocalOrdinal,
                cancellationToken)
            .ConfigureAwait(false);
        if (!read.Accepted)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                read.Failure!.Code,
                read.Failure.Description);
        }

        if (read.ActualOccurrenceCount != lookup.ExpectedSourceOccurrenceCount
            || read.Value is null)
        {
            return DiscoveryOccurrenceHostResult.Reject(
                "discovery-preview-out-of-date",
                "The source occurrence count no longer matches the published Discovery result.");
        }

        return DiscoveryOccurrenceHostResult.Accept(
            new DiscoveredOccurrence(
                lookup.InformationType,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                lookup.Source.SourceId,
                read.Value));
    }

    private sealed class DiscoveryAggregation
    {
        private readonly Dictionary<string, InformationAggregation> _information = new(
            StringComparer.Ordinal);

        public void AddSource(
            InterpretedSourceDocument source,
            string sourceName,
            int sourceOrder)
        {
            foreach (var value in source.Values)
            {
                if (!_information.TryGetValue(value.InformationType, out var aggregate))
                {
                    aggregate = new InformationAggregation(
                        value.InformationType,
                        value.Content);
                    _information.Add(value.InformationType, aggregate);
                }

                aggregate.AddOccurrence(
                    source.OriginatingSourceId,
                    sourceName,
                    sourceOrder);
            }
        }

        public IReadOnlyList<DiscoveredInformation> CreateInformation()
        {
            return _information.Values
                .OrderBy(information => information.InformationType, StringComparer.Ordinal)
                .Select(information => information.CreateContract())
                .ToArray();
        }
    }

    private sealed class InformationAggregation
    {
        private readonly Dictionary<SourceId, SourceContributionAggregation> _sources = [];

        public InformationAggregation(string informationType, string sampleValue)
        {
            InformationType = informationType;
            SampleValue = sampleValue;
        }

        public string InformationType { get; }

        private string SampleValue { get; }

        private int TotalOccurrenceCount { get; set; }

        public void AddOccurrence(SourceId sourceId, string sourceName, int sourceOrder)
        {
            TotalOccurrenceCount++;
            if (!_sources.TryGetValue(sourceId, out var source))
            {
                source = new SourceContributionAggregation(sourceId, sourceName, sourceOrder);
                _sources.Add(sourceId, source);
            }

            source.AddOccurrence();
        }

        public DiscoveredInformation CreateContract()
        {
            return new DiscoveredInformation(
                InformationType,
                TotalOccurrenceCount,
                _sources.Values
                    .OrderBy(source => source.SourceOrder)
                    .Select(source => source.CreateContract())
                    .ToArray(),
                SampleValue);
        }
    }

    private sealed class SourceContributionAggregation
    {
        public SourceContributionAggregation(SourceId sourceId, string sourceName, int sourceOrder)
        {
            SourceId = sourceId;
            SourceName = sourceName;
            SourceOrder = sourceOrder;
        }

        public SourceId SourceId { get; }

        public string SourceName { get; }

        public int SourceOrder { get; }

        private int OccurrenceCount { get; set; }

        public void AddOccurrence()
        {
            OccurrenceCount++;
        }

        public DiscoveredSourceContribution CreateContract()
        {
            return new DiscoveredSourceContribution(SourceId, SourceName, OccurrenceCount);
        }
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
