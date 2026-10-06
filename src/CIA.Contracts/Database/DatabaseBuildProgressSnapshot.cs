using CIA.Contracts.Operations;

namespace CIA.Contracts.Database;

public sealed record DatabaseBuildProgressSnapshot(
    OperationId OperationId,
    string Stage,
    int CompletedWorkCount,
    int TotalWorkCount);
