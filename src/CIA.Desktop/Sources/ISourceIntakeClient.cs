using CIA.Contracts.Sources;

namespace CIA.Desktop.Sources;

public interface ISourceIntakeClient
{
    Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default);

    Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        IProgress<SourceIntakeProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        return LoadAsync(selectionKind, path, settings, cancellationToken);
    }

    async Task<SourceIntakeClientResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        SourceSetId sourceSetId,
        IProgress<SourceIntakeProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(
            selectionKind,
            path,
            settings,
            progress,
            cancellationToken);
        return result with
        {
            Sources = result.Sources
                .Select(source => source with { SourceSetId = sourceSetId })
                .ToArray()
        };
    }

    Task<SourceRefreshClientResult> RefreshAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default);
}

public sealed record SourceIntakeClientResult(
    bool Accepted,
    IReadOnlyList<LoadedSourceContract> Sources,
    string? FailureCode,
    string? FailureDescription)
{
    public IReadOnlyList<SourceIntakeIssue> Issues { get; init; } = [];
}

public sealed record SourceRefreshClientResult(
    bool Accepted,
    LoadedSourceContract Source,
    string? FailureCode,
    string? FailureDescription);
