using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Desktop.Discovery;

public interface IDiscoveryClient
{
    Task<DiscoveryClientResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        CancellationToken cancellationToken = default);

    Task<DiscoveryClientResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        IProgress<DiscoveryProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(correlation, sources, cancellationToken);
    }

    Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
        DiscoveryOccurrenceLookup lookup,
        CancellationToken cancellationToken = default);

    Task<DiscoveryContributorClientResult> GetContributorsAsync(
        DiscoveryContributorPageQuery query,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DiscoveryContributorClientResult(
            false,
            Page: null,
            "discovery-contributors-unavailable",
            "Discovery contributor inspection is unavailable."));
    }
}

public sealed record DiscoveryClientResult(
    bool Accepted,
    IReadOnlyList<DiscoveredInformation> Information,
    IReadOnlyList<DiscoverySourceIssue> Issues,
    OperationCompletion Completion,
    string? FailureCode,
    string? FailureDescription)
{
    public string? FailureTechnicalDetail { get; init; }
}

public sealed record DiscoveryOccurrenceClientResult(
    bool Accepted,
    DiscoveredOccurrence? Occurrence,
    string? FailureCode,
    string? FailureDescription);

public sealed record DiscoveryContributorClientResult(
    bool Accepted,
    DiscoveryContributorPage? Page,
    string? FailureCode,
    string? FailureDescription);
