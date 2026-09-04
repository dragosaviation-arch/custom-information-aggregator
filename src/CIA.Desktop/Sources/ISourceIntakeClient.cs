using CIA.Contracts.Sources;

namespace CIA.Desktop.Sources;

public interface ISourceIntakeClient
{
    Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record SourceIntakeClientResult(
    bool Accepted,
    IReadOnlyList<LoadedSourceContract> Sources,
    string? FailureCode,
    string? FailureDescription);
