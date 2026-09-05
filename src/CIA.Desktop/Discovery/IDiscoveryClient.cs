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
}

public sealed record DiscoveryClientResult(
    bool Accepted,
    IReadOnlyList<DiscoveredInformation> Information,
    IReadOnlyList<DiscoverySourceIssue> Issues,
    OperationCompletion Completion,
    string? FailureCode,
    string? FailureDescription);
