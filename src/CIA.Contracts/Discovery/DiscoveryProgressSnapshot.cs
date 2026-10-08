namespace CIA.Contracts.Discovery;

public sealed record DiscoveryProgressSnapshot(
    int CompletedSourceCount,
    int TotalSourceCount);
