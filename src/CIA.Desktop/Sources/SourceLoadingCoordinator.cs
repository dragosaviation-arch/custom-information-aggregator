using System.IO;
using CIA.Contracts.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Sources;

public sealed class SourceLoadingCoordinator(
    ISourceIntakeClient intakeClient,
    ActiveLoadedSourceSet sourceSet,
    IApplicationWorkflowCoordinator workflowCoordinator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SourceLoadingResult> AddAsync(
        SourceSelectionKind selectionKind,
        string path,
        CancellationToken cancellationToken = default)
    {
        return AddAsync(
            selectionKind,
            path,
            SourceLoadSettings.Default,
            cancellationToken);
    }

    public async Task<SourceLoadingResult> AddAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (workflowCoordinator.Current.ActiveOperation is not null)
            {
                return SourceLoadingResult.Reject(
                    "conflicting-operation",
                    "Sources cannot be changed while an operation is active.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return SourceLoadingResult.Reject(
                    "invalid-path",
                    "The selected source path is not valid.");
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return SourceLoadingResult.Reject(
                    "invalid-path",
                    "The selected source path is not valid.");
            }

            if (selectionKind != SourceSelectionKind.Folder && sourceSet.Contains(fullPath))
            {
                return SourceLoadingResult.Reject(
                    "duplicate-path",
                    "The selected source path is already loaded.");
            }

            var intakeResult = await intakeClient.LoadAsync(
                selectionKind,
                fullPath,
                settings,
                cancellationToken);

            if (!intakeResult.Accepted)
            {
                return SourceLoadingResult.Reject(
                    intakeResult.FailureCode ?? "source-load-rejected",
                    intakeResult.FailureDescription ?? "The selected source could not be loaded.");
            }

            var distinct = intakeResult.Sources
                .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var additions = distinct
                .Where(source => !sourceSet.Contains(source.Path))
                .ToArray();
            var duplicateCount = distinct.Length - additions.Length;

            if (additions.Length == 0)
            {
                return SourceLoadingResult.Reject(
                    duplicateCount > 0 ? "duplicate-path" : "no-supported-sources",
                    duplicateCount > 0
                        ? "All selected source paths are already loaded."
                        : "The selected folder contains no supported sources under the current load settings.");
            }

            var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(
                hasValidSourceSelection: true);

            if (!workflowResult.Accepted)
            {
                return SourceLoadingResult.Reject(
                    "workflow-rejected",
                    workflowResult.Rejection?.Reason ?? "The workflow rejected the source-selection change.");
            }

            sourceSet.AddRange(additions);
            return SourceLoadingResult.Accept(additions.Length, duplicateCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    public SourceInclusionResult SetInclusion(
        IEnumerable<LoadedSourceItem> sources,
        bool isIncluded)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (workflowCoordinator.Current.ActiveOperation is not null)
        {
            return SourceInclusionResult.Reject(
                "conflicting-operation",
                "Source inclusion cannot change while an operation is active.");
        }

        var targets = sources
            .Where(sourceSet.Contains)
            .Distinct()
            .Where(source => source.IsIncluded != isIncluded)
            .ToHashSet();

        if (targets.Count == 0)
        {
            return SourceInclusionResult.Accept(changedCount: 0);
        }

        var hasIncludedSource = sourceSet.Items.Any(
            source => targets.Contains(source) ? isIncluded : source.IsIncluded);
        var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(hasIncludedSource);

        if (!workflowResult.Accepted)
        {
            return SourceInclusionResult.Reject(
                "workflow-rejected",
                workflowResult.Rejection?.Reason
                    ?? "The workflow rejected the source-inclusion change.");
        }

        foreach (var source in targets)
        {
            source.IsIncluded = isIncluded;
        }

        return SourceInclusionResult.Accept(targets.Count);
    }
}

public sealed record SourceLoadingResult(
    bool Accepted,
    int AddedCount,
    int DuplicateCount,
    string? FailureCode,
    string? FailureDescription)
{
    internal static SourceLoadingResult Accept(int addedCount, int duplicateCount)
    {
        return new SourceLoadingResult(true, addedCount, duplicateCount, null, null);
    }

    internal static SourceLoadingResult Reject(string code, string description)
    {
        return new SourceLoadingResult(false, 0, 0, code, description);
    }
}

public sealed record SourceInclusionResult(
    bool Accepted,
    int ChangedCount,
    string? FailureCode,
    string? FailureDescription)
{
    internal static SourceInclusionResult Accept(int changedCount)
    {
        return new SourceInclusionResult(true, changedCount, null, null);
    }

    internal static SourceInclusionResult Reject(string code, string description)
    {
        return new SourceInclusionResult(false, 0, code, description);
    }
}
