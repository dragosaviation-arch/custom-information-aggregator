using CIA.Contracts.Discovery;
using CIA.Core.Profiles;
using CIA.Desktop.Discovery;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Profiles;

public sealed record InformationSelectionProfileItem(
    ProfileId ProfileId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Path,
    ProfileArtifactFingerprint Fingerprint)
{
    public string DisplayName => Name;
}

public sealed record InformationSelectionProfileInventory(
    IReadOnlyList<InformationSelectionProfileItem> Profiles,
    IReadOnlyList<string> Problems)
{
    public static InformationSelectionProfileInventory Empty { get; } = new([], []);
}

public sealed record InformationSelectionProfileOperationResult(
    bool Succeeded,
    string Message,
    InformationSelectionProfileItem? Profile = null,
    int MatchedCount = 0,
    int UnmatchedCount = 0,
    int SkippedBlacklistedCount = 0,
    int ChangedCount = 0,
    bool RequiresReselection = false)
{
    internal static InformationSelectionProfileOperationResult Success(
        string message,
        InformationSelectionProfileItem? profile = null,
        int matchedCount = 0,
        int unmatchedCount = 0,
        int skippedBlacklistedCount = 0,
        int changedCount = 0) =>
        new(
            true,
            message,
            profile,
            matchedCount,
            unmatchedCount,
            skippedBlacklistedCount,
            changedCount);

    internal static InformationSelectionProfileOperationResult Failure(
        string message,
        bool requiresReselection = false) =>
        new(false, message, RequiresReselection: requiresReselection);
}

public interface IInformationSelectionProfileCoordinator
{
    InformationSelectionProfileInventory Inventory { get; }

    bool CanCaptureCurrentConfiguration { get; }

    string CaptureReadinessReason { get; }

    bool CanLoadCurrentConfiguration { get; }

    InformationSelectionProfileInventory RefreshInventory();

    InformationSelectionProfileOperationResult SaveNew(string name);

    InformationSelectionProfileOperationResult Load(
        InformationSelectionProfileItem selectedProfile);

    InformationSelectionProfileOperationResult Update(
        InformationSelectionProfileItem selectedProfile);

    InformationSelectionProfileOperationResult Delete(
        InformationSelectionProfileItem selectedProfile);
}

public sealed class InformationSelectionProfileCoordinator :
    IInformationSelectionProfileCoordinator
{
    private readonly ProfileArtifactStore _store;
    private readonly ActiveDiscoveryConfiguration _activeConfiguration;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly TimeProvider _timeProvider;

    public InformationSelectionProfileCoordinator(
        ProfileArtifactStore store,
        ActiveDiscoveryConfiguration activeConfiguration,
        IApplicationWorkflowCoordinator workflowCoordinator,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeConfiguration);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _store = store;
        _activeConfiguration = activeConfiguration;
        _workflowCoordinator = workflowCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public InformationSelectionProfileInventory Inventory { get; private set; } =
        InformationSelectionProfileInventory.Empty;

    public bool CanCaptureCurrentConfiguration =>
        CanUseCurrentDiscoveryConfiguration()
        && TryCreateContent(out _, out _);

    public string CaptureReadinessReason => TryCreateContent(out _, out var problem)
        ? "The current Discovery selection can be saved as a reusable profile."
        : problem;

    public bool CanLoadCurrentConfiguration => CanUseCurrentDiscoveryConfiguration();

    public InformationSelectionProfileInventory RefreshInventory()
    {
        var inventory = _store.CreateInventory();
        var profiles = inventory.Items
            .Where(item => item.State == ProfileArtifactReadState.Valid
                           && item.Artifact?.ProfileKind == ProfileKind.InformationSelection
                           && item.Fingerprint is not null)
            .Select(item => new InformationSelectionProfileItem(
                item.Artifact!.ProfileId,
                item.Artifact.Name,
                item.Artifact.CreatedAtUtc,
                item.Artifact.UpdatedAtUtc,
                item.Path,
                item.Fingerprint!.Value))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProfileId.Value)
            .ToArray();
        var problems = inventory.Items
            .Where(item => item.State != ProfileArtifactReadState.Valid)
            .Select(item => item.Problem ?? "A profile artifact is unavailable.")
            .ToArray();

        Inventory = new InformationSelectionProfileInventory(profiles, problems);
        return Inventory;
    }

    public InformationSelectionProfileOperationResult SaveNew(string name)
    {
        if (!TryCreateContent(out var content, out var problem))
        {
            return InformationSelectionProfileOperationResult.Failure(problem);
        }

        var profileId = ProfileId.CreateNew();
        var now = UtcNow();
        var artifact = new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            profileId,
            ProfileKind.InformationSelection,
            name,
            now,
            now,
            content);
        var write = _store.Create($"{profileId}{ProfileArtifactStore.FileExtension}", artifact);
        if (!write.Succeeded)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                write.Problem ?? "The information-selection profile could not be saved.");
        }

        var refreshed = RefreshInventory();
        var profile = refreshed.Profiles.Single(item => item.ProfileId == profileId);
        return InformationSelectionProfileOperationResult.Success(
            $"Saved information-selection profile '{profile.Name}'.",
            profile);
    }

    public InformationSelectionProfileOperationResult Load(
        InformationSelectionProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        if (!CanUseCurrentDiscoveryConfiguration())
        {
            return InformationSelectionProfileOperationResult.Failure(
                "Run Discovery successfully and wait for the active operation to finish before loading a profile.");
        }

        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var content = (InformationSelectionProfileContentV1)resolved.Artifact.Content;
        var entries = content.Entries.ToDictionary(entry => entry.Identity);
        var snapshot = _activeConfiguration.Current;
        var currentPortableIdentities = snapshot.Items
            .Select(item => PortableDiscoveryInformationIdentity.From(item.Identity))
            .ToHashSet();
        var matchedCount = entries.Keys.Count(currentPortableIdentities.Contains);
        var unmatchedCount = entries.Count - matchedCount;
        var skippedBlacklistedCount = snapshot.Items.Count(item =>
            item.Disposition == DiscoveryInformationDisposition.Blacklisted
            && entries.ContainsKey(PortableDiscoveryInformationIdentity.From(item.Identity)));
        var changes = snapshot.Items
            .Where(item => item.Disposition != DiscoveryInformationDisposition.Blacklisted)
            .Select(item => new
            {
                Item = item,
                Entry = entries.GetValueOrDefault(
                    PortableDiscoveryInformationIdentity.From(item.Identity))
            })
            .Where(item => item.Entry is not null)
            .Select(item => new
            {
                item.Item.Identity,
                IsSelected = item.Entry!.Membership == InformationSelectionMembership.Selected,
                item.Item.Disposition
            })
            .Where(item => item.IsSelected
                ? item.Disposition != DiscoveryInformationDisposition.Selected
                : item.Disposition == DiscoveryInformationDisposition.Selected)
            .ToArray();

        if (changes.Length == 0)
        {
            return InformationSelectionProfileOperationResult.Success(
                CreateLoadMessage(
                    changedCount: 0,
                    matchedCount,
                    unmatchedCount,
                    skippedBlacklistedCount),
                selectedProfile,
                matchedCount,
                unmatchedCount,
                skippedBlacklistedCount);
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted)
        {
            return InformationSelectionProfileOperationResult.Failure(
                workflowResult.Rejection?.Reason
                ?? "The profile could not be applied while another operation is active.");
        }

        var selected = changes.Where(item => item.IsSelected).Select(item => item.Identity);
        var excluded = changes.Where(item => !item.IsSelected).Select(item => item.Identity);
        var changedCount = _activeConfiguration.SetSelection(selected, isSelected: true)
            + _activeConfiguration.SetSelection(excluded, isSelected: false);

        return InformationSelectionProfileOperationResult.Success(
            CreateLoadMessage(
                changedCount,
                matchedCount,
                unmatchedCount,
                skippedBlacklistedCount),
            selectedProfile,
            matchedCount,
            unmatchedCount,
            skippedBlacklistedCount,
            changedCount);
    }

    public InformationSelectionProfileOperationResult Update(
        InformationSelectionProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        if (!TryCreateContent(out var content, out var problem))
        {
            return InformationSelectionProfileOperationResult.Failure(problem);
        }

        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var current = resolved.Artifact;
        var currentUtc = UtcNow();
        DateTimeOffset updatedAtUtc;
        if (currentUtc > current.UpdatedAtUtc)
        {
            updatedAtUtc = currentUtc;
        }
        else if (current.UpdatedAtUtc == DateTimeOffset.MaxValue)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                "The profile timestamp is already at the maximum supported value and cannot be advanced. Save a new profile instead.");
        }
        else
        {
            updatedAtUtc = current.UpdatedAtUtc.AddTicks(1);
        }

        var replacement = current with
        {
            UpdatedAtUtc = updatedAtUtc,
            Content = content
        };
        var write = _store.Update(replacement, selectedProfile.Fingerprint);
        if (!write.Succeeded)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                write.Problem ?? "The information-selection profile could not be updated.",
                requiresReselection: true);
        }

        var refreshed = RefreshInventory();
        var profile = refreshed.Profiles.Single(
            item => item.ProfileId == selectedProfile.ProfileId);
        return InformationSelectionProfileOperationResult.Success(
            $"Updated information-selection profile '{profile.Name}'.",
            profile);
    }

    public InformationSelectionProfileOperationResult Delete(
        InformationSelectionProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var deleted = _store.Delete(selectedProfile.ProfileId, selectedProfile.Fingerprint);
        if (!deleted.Succeeded)
        {
            RefreshInventory();
            return InformationSelectionProfileOperationResult.Failure(
                deleted.Problem ?? "The information-selection profile could not be deleted.",
                requiresReselection: true);
        }

        RefreshInventory();
        return InformationSelectionProfileOperationResult.Success(
            $"Deleted information-selection profile '{selectedProfile.Name}'.");
    }

    private bool TryCreateContent(
        out InformationSelectionProfileContentV1 content,
        out string problem)
    {
        content = new InformationSelectionProfileContentV1([]);
        if (!CanUseCurrentDiscoveryConfiguration())
        {
            problem = "Run Discovery successfully and wait for the active operation to finish before saving a profile.";
            return false;
        }

        var snapshot = _activeConfiguration.Current;
        if (snapshot.Items.Count == 0)
        {
            problem = "The current Discovery result does not contain information to save.";
            return false;
        }

        var entries = new List<InformationSelectionProfileEntryV1>();
        foreach (var group in snapshot.Items
                     .Where(item => item.Disposition != DiscoveryInformationDisposition.Blacklisted)
                     .GroupBy(item => PortableDiscoveryInformationIdentity.From(item.Identity)))
        {
            var memberships = group
                .Select(item => item.Disposition == DiscoveryInformationDisposition.Selected
                    ? InformationSelectionMembership.Selected
                    : InformationSelectionMembership.Excluded)
                .Distinct()
                .ToArray();
            if (memberships.Length != 1)
            {
                problem =
                    $"'{group.Key.InformationType}' has conflicting selection states across Source Sets. Make those states consistent before saving the reusable profile.";
                return false;
            }

            entries.Add(new InformationSelectionProfileEntryV1(group.Key, memberships[0]));
        }

        content = new InformationSelectionProfileContentV1(entries
            .OrderBy(entry => entry.Identity.StructuralPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.Identity.InformationType, StringComparer.Ordinal)
            .ThenBy(entry => entry.Identity.CandidateKind)
            .ThenBy(entry => entry.Identity.StructuralIdentity, StringComparer.Ordinal)
            .ToArray());
        problem = string.Empty;
        return true;
    }

    private ResolvedProfile ResolveSelectedProfile(
        InformationSelectionProfileItem selectedProfile)
    {
        var inspection = _store.FindById(selectedProfile.ProfileId);
        if (inspection.State != ProfileArtifactReadState.Valid
            || inspection.Artifact is null
            || inspection.Fingerprint is not { } fingerprint)
        {
            return ResolvedProfile.Failure(
                inspection.Problem ?? "The selected profile is no longer available.");
        }

        if (inspection.Artifact.ProfileKind != ProfileKind.InformationSelection
            || inspection.Artifact.Content is not InformationSelectionProfileContentV1)
        {
            return ResolvedProfile.Failure(
                "The selected artifact is not an information-selection profile.");
        }

        if (fingerprint != selectedProfile.Fingerprint)
        {
            return ResolvedProfile.Failure(
                "The selected profile changed after it was listed. Refresh and select it again before continuing.");
        }

        return ResolvedProfile.Success(inspection.Artifact);
    }

    private bool CanUseCurrentDiscoveryConfiguration()
    {
        var workflow = _workflowCoordinator.Current;
        return workflow.Discovery == WorkflowArtifactStatus.Current
            && workflow.ActiveOperation is null;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static string CreateLoadMessage(
        int changedCount,
        int matchedCount,
        int unmatchedCount,
        int skippedBlacklistedCount) =>
        $"Profile applied: {changedCount} selection changes, {matchedCount} matched profile entries, {unmatchedCount} unmatched, {skippedBlacklistedCount} blacklisted matches skipped.";

    private sealed record ResolvedProfile(
        bool Succeeded,
        ProfileArtifactV1? Artifact,
        string? Problem)
    {
        public static ResolvedProfile Success(ProfileArtifactV1 artifact) =>
            new(true, artifact, Problem: null);

        public static ResolvedProfile Failure(string problem) =>
            new(false, Artifact: null, problem);
    }
}
