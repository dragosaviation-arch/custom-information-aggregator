using CIA.Contracts.Sources;

namespace CIA.Desktop.Sources;

public interface ISourceIntakeClient
{
    Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default);

    Task<SourceRefreshClientResult> RefreshAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default);
}

public sealed record SourceIntakeClientResult(
    bool Accepted,
    IReadOnlyList<LoadedSourceContract> Sources,
    string? FailureCode,
    string? FailureDescription);

public sealed record SourceRefreshClientResult(
    bool Accepted,
    LoadedSourceContract Source,
    string? FailureCode,
    string? FailureDescription);
