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

    public async Task<SourceLoadingResult> AddAsync(
        SourceSelectionKind selectionKind,
        string path,
        CancellationToken cancellationToken = default)
    {
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
                SourceLoadSettings.Default,
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
