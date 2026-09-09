using System.IO;
using CIA.Contracts.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Sources;

public sealed class SourceLoadingCoordinator(
    ISourceIntakeClient intakeClient,
    ActiveLoadedSourceSet sourceSet,
    IApplicationWorkflowCoordinator workflowCoordinator)
{
    private const int MaximumSourceSetNameLength = 100;
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
        return await AddAsync(
            selectionKind,
            path,
            settings,
            progress: null,
            cancellationToken);
    }

    public async Task<SourceLoadingResult> AddAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        IProgress<SourceIntakeProgressSnapshot>? progress,
        CancellationToken cancellationToken = default)
    {
        var activeSourceSetId = sourceSet.ActiveSourceSet?.SourceSetId;
        return await AddCoreAsync(
            selectionKind,
            path,
            settings,
            progress,
            activeSourceSetId ?? SourceSetId.CreateNew(),
            createTargetSourceSet: activeSourceSetId is null,
            newSourceSetName: activeSourceSetId is null
                ? sourceSet.GetNextDefaultSourceSetName()
                : null,
            cancellationToken);
    }

    public Task<SourceLoadingResult> AddToSourceSetAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        SourceSetId targetSourceSetId,
        IProgress<SourceIntakeProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return AddCoreAsync(
            selectionKind,
            path,
            settings,
            progress,
            targetSourceSetId,
            createTargetSourceSet: false,
            newSourceSetName: null,
            cancellationToken);
    }

    public Task<SourceLoadingResult> AddToNewSourceSetAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        IProgress<SourceIntakeProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return AddCoreAsync(
            selectionKind,
            path,
            settings,
            progress,
            SourceSetId.CreateNew(),
            createTargetSourceSet: true,
            newSourceSetName: sourceSet.GetNextDefaultSourceSetName(),
            cancellationToken);
    }

    private async Task<SourceLoadingResult> AddCoreAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        IProgress<SourceIntakeProgressSnapshot>? progress,
        SourceSetId targetSourceSetId,
        bool createTargetSourceSet,
        string? newSourceSetName,
        CancellationToken cancellationToken)
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

            if (!createTargetSourceSet && !sourceSet.Contains(targetSourceSetId))
            {
                return SourceLoadingResult.Reject(
                    "source-set-not-found",
                    "The selected Source Set is no longer available.");
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
                targetSourceSetId,
                progress,
                cancellationToken);

            if (!intakeResult.Accepted)
            {
                return SourceLoadingResult.Reject(
                    intakeResult.FailureCode ?? "source-load-rejected",
                    intakeResult.FailureDescription ?? "The selected source could not be loaded.",
                    intakeResult.Issues);
            }

            var distinct = intakeResult.Sources
                .Select(source => source with { SourceSetId = targetSourceSetId })
                .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var additions = distinct
                .Where(source => !sourceSet.Contains(source))
                .ToArray();
            var duplicateCount = distinct.Length - additions.Length;

            if (additions.Length == 0)
            {
                return SourceLoadingResult.Reject(
                    duplicateCount > 0 ? "duplicate-path" : "no-supported-sources",
                    duplicateCount > 0
                        ? "All selected source paths are already loaded."
                        : "The selected folder contains no supported sources under the current load settings.",
                    intakeResult.Issues);
            }

            var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(
                hasValidSourceSelection: true);

            if (!workflowResult.Accepted)
            {
                return SourceLoadingResult.Reject(
                    "workflow-rejected",
                    workflowResult.Rejection?.Reason ?? "The workflow rejected the source-selection change.",
                    intakeResult.Issues);
            }

            if (createTargetSourceSet)
            {
                sourceSet.CreateSourceSet(
                    targetSourceSetId,
                    newSourceSetName ?? sourceSet.GetNextDefaultSourceSetName());
            }
            else
            {
                sourceSet.Activate(targetSourceSetId);
            }

            sourceSet.AddRange(additions);
            return SourceLoadingResult.Accept(
                additions.Length,
                duplicateCount,
                targetSourceSetId,
                intakeResult.Issues);
        }
        finally
        {
            _gate.Release();
        }
    }

    public SourceSetMutationResult CreateSourceSet(string? name = null)
    {
        if (workflowCoordinator.Current.ActiveOperation is not null)
        {
            return SourceSetMutationResult.Reject(
                "conflicting-operation",
                "Source Sets cannot be changed while an operation is active.");
        }

        if (sourceSet.SourceSets.Count == 0)
        {
            return SourceSetMutationResult.Reject(
                "source-set-unavailable",
                "Load a source to create Set 1 before adding another Source Set.");
        }

        var sourceSetName = string.IsNullOrWhiteSpace(name)
            ? sourceSet.GetNextDefaultSourceSetName()
            : name.Trim();
        if (sourceSetName.Length > MaximumSourceSetNameLength)
        {
            return SourceSetMutationResult.Reject(
                "invalid-source-set-name",
                $"Source Set names cannot exceed {MaximumSourceSetNameLength} characters.");
        }

        if (sourceSet.SourceSets.Any(candidate =>
            string.Equals(candidate.Name, sourceSetName, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceSetMutationResult.Reject(
                "duplicate-source-set-name",
                "Source Set names must be unique in the active session.");
        }

        var created = sourceSet.CreateSourceSet(SourceSetId.CreateNew(), sourceSetName);
        return SourceSetMutationResult.Accept(created, changedCount: 1);
    }

    public SourceSetMutationResult ActivateSourceSet(SourceSetId sourceSetId)
    {
        return sourceSet.Activate(sourceSetId)
            ? SourceSetMutationResult.Accept(sourceSet.ActiveSourceSet!, changedCount: 0)
            : SourceSetMutationResult.Reject(
                "source-set-not-found",
                "The selected Source Set is no longer available.");
    }

    public SourceSetMutationResult RenameSourceSet(SourceSetId sourceSetId, string name)
    {
        if (workflowCoordinator.Current.ActiveOperation is not null)
        {
            return SourceSetMutationResult.Reject(
                "conflicting-operation",
                "Source Sets cannot be changed while an operation is active.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return SourceSetMutationResult.Reject(
                "invalid-source-set-name",
                "A Source Set name is required.");
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > MaximumSourceSetNameLength)
        {
            return SourceSetMutationResult.Reject(
                "invalid-source-set-name",
                $"Source Set names cannot exceed {MaximumSourceSetNameLength} characters.");
        }

        if (sourceSet.SourceSets.Any(candidate =>
            candidate.SourceSetId != sourceSetId
            && string.Equals(candidate.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return SourceSetMutationResult.Reject(
                "duplicate-source-set-name",
                "Source Set names must be unique in the active session.");
        }

        var target = sourceSet.SourceSets.FirstOrDefault(candidate =>
            candidate.SourceSetId == sourceSetId);
        if (target is null)
        {
            return SourceSetMutationResult.Reject(
                "source-set-not-found",
                "The selected Source Set is no longer available.");
        }

        if (string.Equals(target.Name, normalizedName, StringComparison.Ordinal))
        {
            return SourceSetMutationResult.Accept(target, changedCount: 0);
        }

        sourceSet.Rename(sourceSetId, normalizedName);
        return SourceSetMutationResult.Accept(target, changedCount: 1);
    }

    public SourceSetMutationResult ReassignSources(
        IEnumerable<LoadedSourceItem> sources,
        SourceSetId targetSourceSetId)
    {
        return ReassignSourcesCore(
            sources,
            targetSourceSetId,
            createTargetSourceSet: false,
            newSourceSetName: null);
    }

    public SourceSetMutationResult ReassignSourcesToNewSet(
        IEnumerable<LoadedSourceItem> sources)
    {
        return ReassignSourcesCore(
            sources,
            SourceSetId.CreateNew(),
            createTargetSourceSet: true,
            newSourceSetName: sourceSet.GetNextDefaultSourceSetName());
    }

    private SourceSetMutationResult ReassignSourcesCore(
        IEnumerable<LoadedSourceItem> sources,
        SourceSetId targetSourceSetId,
        bool createTargetSourceSet,
        string? newSourceSetName)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (workflowCoordinator.Current.ActiveOperation is not null)
        {
            return SourceSetMutationResult.Reject(
                "conflicting-operation",
                "Source membership cannot change while an operation is active.");
        }

        var targets = sources
            .Where(sourceSet.Contains)
            .Distinct()
            .Where(source => source.SourceSetId != targetSourceSetId)
            .ToArray();
        if (targets.Length == 0)
        {
            var unchangedTarget = sourceSet.SourceSets.FirstOrDefault(candidate =>
                candidate.SourceSetId == targetSourceSetId);
            return unchangedTarget is null
                ? SourceSetMutationResult.Reject(
                    "source-set-not-found",
                    "The selected Source Set is no longer available.")
                : SourceSetMutationResult.Accept(unchangedTarget, changedCount: 0);
        }

        SourceSetDefinition? destination = null;
        if (!createTargetSourceSet)
        {
            destination = sourceSet.SourceSets.FirstOrDefault(candidate =>
                candidate.SourceSetId == targetSourceSetId);
            if (destination is null)
            {
                return SourceSetMutationResult.Reject(
                    "source-set-not-found",
                    "The selected Source Set is no longer available.");
            }
        }

        var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(
            sourceSet.Items.Any(source =>
                source.IsIncluded && source.Status == LoadedSourceStatus.Ready));
        if (!workflowResult.Accepted)
        {
            return SourceSetMutationResult.Reject(
                "workflow-rejected",
                workflowResult.Rejection?.Reason
                    ?? "The workflow rejected the Source Set membership change.");
        }

        destination ??= sourceSet.CreateSourceSet(
            targetSourceSetId,
            newSourceSetName ?? sourceSet.GetNextDefaultSourceSetName());
        sourceSet.Reassign(targets, destination);
        return SourceSetMutationResult.Accept(destination, targets.Length);
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
            source => (targets.Contains(source) ? isIncluded : source.IsIncluded)
                      && source.Status == LoadedSourceStatus.Ready);
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

    public SourceRemovalResult Remove(IEnumerable<LoadedSourceItem> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (workflowCoordinator.Current.ActiveOperation is not null)
        {
            return SourceRemovalResult.Reject(
                "conflicting-operation",
                "Sources cannot be removed while an operation is active.");
        }

        var targets = sources
            .Where(sourceSet.Contains)
            .Distinct()
            .ToHashSet();

        if (targets.Count == 0)
        {
            return SourceRemovalResult.Accept(removedCount: 0);
        }

        var hasValidSourceSelection = sourceSet.Items.Any(
            source => !targets.Contains(source)
                      && source.IsIncluded
                      && source.Status == LoadedSourceStatus.Ready);
        var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(
            hasValidSourceSelection);

        if (!workflowResult.Accepted)
        {
            return SourceRemovalResult.Reject(
                "workflow-rejected",
                workflowResult.Rejection?.Reason
                    ?? "The workflow rejected the source removal.");
        }

        sourceSet.RemoveRange(targets);
        return SourceRemovalResult.Accept(targets.Count);
    }

    public async Task<SourceRefreshResult> RefreshAsync(
        LoadedSourceItem source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!sourceSet.Contains(source))
            {
                return SourceRefreshResult.Reject(
                    "source-not-loaded",
                    "The source is no longer part of the active session.");
            }

            if (workflowCoordinator.Current.ActiveOperation is not null)
            {
                return SourceRefreshResult.Reject(
                    "conflicting-operation",
                    "Sources cannot be refreshed while an operation is active.");
            }

            var refreshRequest = new LoadedSourceContract(
                source.SourceId,
                source.SourceSetId,
                source.Path,
                source.IsIncluded,
                source.Status,
                source.Kind)
            {
                ArchiveProvenance = source.ArchiveProvenance
            };
            var intakeResult = await intakeClient.RefreshAsync(
                refreshRequest,
                cancellationToken);

            if (!intakeResult.Accepted)
            {
                var failureDescription = intakeResult.FailureDescription
                    ?? "The source could not be refreshed.";
                var workflowFailureResult = workflowCoordinator.RecordSourceSelectionChanged(
                    HasValidSourceSelectionExcept(source));

                if (!workflowFailureResult.Accepted)
                {
                    return SourceRefreshResult.Reject(
                        "workflow-rejected",
                        workflowFailureResult.Rejection?.Reason
                            ?? "The workflow rejected the source refresh.");
                }

                var failedStatus = intakeResult.Source.SourceId == source.SourceId
                    && intakeResult.Source.Status != LoadedSourceStatus.Ready
                    ? intakeResult.Source.Status
                    : MapFailureStatus(intakeResult.FailureCode);
                source.ApplyRefreshFailure(failedStatus, failureDescription);
                return SourceRefreshResult.Reject(
                    intakeResult.FailureCode ?? "source-refresh-rejected",
                    failureDescription,
                    sourceUpdated: true);
            }

            var refreshed = intakeResult.Source;

            if (refreshed.SourceId != source.SourceId || refreshed.Kind != source.Kind)
            {
                const string failureDescription =
                    "The Processing Host did not return the requested source during refresh.";
                var workflowFailureResult = workflowCoordinator.RecordSourceSelectionChanged(
                    HasValidSourceSelectionExcept(source));

                if (!workflowFailureResult.Accepted)
                {
                    return SourceRefreshResult.Reject(
                        "workflow-rejected",
                        workflowFailureResult.Rejection?.Reason
                            ?? "The workflow rejected the source refresh.");
                }

                source.ApplyRefreshFailure(
                    LoadedSourceStatus.FailedValidation,
                    failureDescription);
                return SourceRefreshResult.Reject(
                    "invalid-refresh-response",
                    failureDescription,
                    sourceUpdated: true);
            }

            var retainedIdentity = new LoadedSourceContract(
                source.SourceId,
                source.SourceSetId,
                refreshed.Path,
                source.IsIncluded,
                refreshed.Status,
                source.Kind)
            {
                ArchiveProvenance = refreshed.ArchiveProvenance
            };
            var hasValidSourceSelection = HasValidSourceSelectionExcept(source)
                || retainedIdentity.IsIncluded
                && retainedIdentity.Status == LoadedSourceStatus.Ready;
            var workflowResult = workflowCoordinator.RecordSourceSelectionChanged(
                hasValidSourceSelection);

            if (!workflowResult.Accepted)
            {
                return SourceRefreshResult.Reject(
                    "workflow-rejected",
                    workflowResult.Rejection?.Reason
                        ?? "The workflow rejected the source refresh.");
            }

            source.ApplyRefresh(retainedIdentity);
            return SourceRefreshResult.Accept();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool HasValidSourceSelectionExcept(LoadedSourceItem excluded)
    {
        return sourceSet.Items.Any(source =>
            !ReferenceEquals(source, excluded)
            && source.IsIncluded
            && source.Status == LoadedSourceStatus.Ready);
    }

    private static LoadedSourceStatus MapFailureStatus(string? failureCode)
    {
        return failureCode switch
        {
            "unsupported-source" or "unsupported-archive" or "unsupported-input"
                => LoadedSourceStatus.Unsupported,
            "malformed-xml" or "source-validation-failed"
                => LoadedSourceStatus.FailedValidation,
            _ => LoadedSourceStatus.Unavailable
        };
    }
}

public sealed record SourceLoadingResult(
    bool Accepted,
    int AddedCount,
    int DuplicateCount,
    string? FailureCode,
    string? FailureDescription)
{
    public SourceSetId? SourceSetId { get; init; }

    public IReadOnlyList<SourceIntakeIssue> Issues { get; init; } = [];

    internal static SourceLoadingResult Accept(
        int addedCount,
        int duplicateCount,
        SourceSetId sourceSetId,
        IReadOnlyList<SourceIntakeIssue>? issues = null)
    {
        return new SourceLoadingResult(true, addedCount, duplicateCount, null, null)
        {
            SourceSetId = sourceSetId,
            Issues = issues ?? []
        };
    }

    internal static SourceLoadingResult Reject(
        string code,
        string description,
        IReadOnlyList<SourceIntakeIssue>? issues = null)
    {
        return new SourceLoadingResult(false, 0, 0, code, description)
        {
            Issues = issues ?? []
        };
    }
}

public sealed record SourceSetMutationResult(
    bool Accepted,
    SourceSetDefinition? SourceSet,
    int ChangedCount,
    string? FailureCode,
    string? FailureDescription)
{
    internal static SourceSetMutationResult Accept(
        SourceSetDefinition sourceSet,
        int changedCount)
    {
        return new SourceSetMutationResult(true, sourceSet, changedCount, null, null);
    }

    internal static SourceSetMutationResult Reject(string code, string description)
    {
        return new SourceSetMutationResult(false, null, 0, code, description);
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

public sealed record SourceRemovalResult(
    bool Accepted,
    int RemovedCount,
    string? FailureCode,
    string? FailureDescription)
{
    internal static SourceRemovalResult Accept(int removedCount)
    {
        return new SourceRemovalResult(true, removedCount, null, null);
    }

    internal static SourceRemovalResult Reject(string code, string description)
    {
        return new SourceRemovalResult(false, 0, code, description);
    }
}

public sealed record SourceRefreshResult(
    bool Accepted,
    bool SourceUpdated,
    string? FailureCode,
    string? FailureDescription)
{
    internal static SourceRefreshResult Accept()
    {
        return new SourceRefreshResult(true, true, null, null);
    }

    internal static SourceRefreshResult Reject(
        string code,
        string description,
        bool sourceUpdated = false)
    {
        return new SourceRefreshResult(false, sourceUpdated, code, description);
    }
}
