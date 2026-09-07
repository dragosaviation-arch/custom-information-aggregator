using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Discovery;

public sealed record DiscoveredSourceContribution(
    SourceId SourceId,
    string SourceName,
    int OccurrenceCount);

public sealed record DiscoveredInformation(
    string InformationType,
    int TotalOccurrenceCount,
    IReadOnlyList<DiscoveredSourceContribution> ContributingSources,
    string SampleValue);

public sealed record DiscoveredOccurrence(
    string InformationType,
    int Ordinal,
    int TotalOccurrenceCount,
    SourceId SourceId,
    string Value);

public sealed record DiscoveryOccurrenceLookup(
    OperationId DiscoveryOperationId,
    string InformationType,
    int GlobalOrdinal,
    int TotalOccurrenceCount,
    LoadedSourceContract Source,
    int LocalOrdinal,
    int ExpectedSourceOccurrenceCount);

public sealed record DiscoverySourceIssue(
    SourceId SourceId,
    string Code,
    string Description);
