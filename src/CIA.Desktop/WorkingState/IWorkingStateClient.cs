using CIA.Contracts.Operations;
using CIA.Contracts.WorkingState;

namespace CIA.Desktop.WorkingState;

public interface IWorkingStateClient
{
    Task<WorkingStateClientResult> SaveAsync(
        OperationCorrelation correlation,
        string targetPath,
        WorkingStateSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<WorkingStateClientResult> RestoreAsync(
        OperationCorrelation correlation,
        string packagePath,
        CancellationToken cancellationToken = default);
}

public sealed record WorkingStateClientResult(
    bool Accepted,
    OperationCompletion Completion,
    WorkingStateManifest? Manifest,
    string? FailureCode,
    string? FailureDescription);
