using System.IO;
using CIA.Contracts.Discovery;
using CIA.Core.Profiles;
using CIA.Desktop.Discovery;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Profiles;

public sealed record BlacklistProfileItem(
    ProfileId ProfileId,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Path,
    ProfileArtifactFingerprint Fingerprint,
    bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name} (Default)" : Name;
}

public sealed record BlacklistProfileInventory(
    IReadOnlyList<BlacklistProfileItem> Profiles,
    IReadOnlyList<string> Problems,
    ProfileId? DefaultProfileId,
    string? DefaultDesignationProblem)
{
    public static BlacklistProfileInventory Empty { get; } =
        new([], [], DefaultProfileId: null, DefaultDesignationProblem: null);
}

public enum BlacklistSessionDefaultStatus
{
    NotConfigured = 1,
    Resolved = 2,
    Invalid = 3
}

public sealed record BlacklistSessionDefaultSnapshot(
    BlacklistSessionDefaultStatus Status,
    ProfileId? ProfileId,
    ProfileArtifactV1? Profile,
    string Message)
{
    public BlacklistProfileContentV1? Content => Profile?.Content as BlacklistProfileContentV1;

    public static BlacklistSessionDefaultSnapshot NotConfigured(string message) =>
        new(BlacklistSessionDefaultStatus.NotConfigured, null, null, message);

    public static BlacklistSessionDefaultSnapshot Invalid(
        ProfileId? profileId,
        string message) =>
        new(BlacklistSessionDefaultStatus.Invalid, profileId, null, message);

    public static BlacklistSessionDefaultSnapshot Resolved(ProfileArtifactV1 profile) =>
        new(
            BlacklistSessionDefaultStatus.Resolved,
            profile.ProfileId,
            profile,
            $"Default blacklist profile '{profile.Name}' is available for this session.");
}

public sealed record BlacklistProfileOperationResult(
    bool Succeeded,
    string Message,
    BlacklistProfileItem? Profile = null,
    int MatchedCount = 0,
    int UnmatchedCount = 0,
    int ChangedCount = 0,
    bool RequiresReselection = false)
{
    internal static BlacklistProfileOperationResult Success(
        string message,
        BlacklistProfileItem? profile = null,
        int matchedCount = 0,
        int unmatchedCount = 0,
        int changedCount = 0) =>
        new(true, message, profile, matchedCount, unmatchedCount, changedCount);

    internal static BlacklistProfileOperationResult Failure(
        string message,
        bool requiresReselection = false) =>
        new(false, message, RequiresReselection: requiresReselection);
}

public interface IBlacklistProfileCoordinator
{
    BlacklistProfileInventory Inventory { get; }

    BlacklistSessionDefaultSnapshot SessionDefault { get; }

    bool CanCaptureCurrentConfiguration { get; }

    string CaptureReadinessReason { get; }

    bool CanLoadCurrentConfiguration { get; }

    BlacklistProfileInventory RefreshInventory();

    BlacklistSessionDefaultSnapshot ResolveSessionDefault();

    BlacklistProfileOperationResult SaveNew(string name);

    BlacklistProfileOperationResult Load(BlacklistProfileItem selectedProfile);

    BlacklistProfileOperationResult Update(BlacklistProfileItem selectedProfile);

    BlacklistProfileOperationResult Clone(
        BlacklistProfileItem selectedProfile,
        string newName);

    BlacklistProfileOperationResult Delete(BlacklistProfileItem selectedProfile);

    BlacklistProfileOperationResult SetDefault(BlacklistProfileItem selectedProfile);

    BlacklistProfileOperationResult ClearDefault();
}

public sealed class BlacklistProfileCoordinator :
    IBlacklistProfileCoordinator,
    IHostedService
{
    private readonly ProfileArtifactStore _store;
    private readonly DefaultBlacklistProfileDesignationStore _designationStore;
    private readonly ProfileStorePublicationGate _publicationGate;
    private readonly ActiveDiscoveryConfiguration _activeConfiguration;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly ILogger<BlacklistProfileCoordinator> _logger;
    private readonly TimeProvider _timeProvider;

    public BlacklistProfileCoordinator(
        ProfileArtifactStore store,
        DefaultBlacklistProfileDesignationStore designationStore,
        ActiveDiscoveryConfiguration activeConfiguration,
        IApplicationWorkflowCoordinator workflowCoordinator,
        ILogger<BlacklistProfileCoordinator> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(designationStore);
        ArgumentNullException.ThrowIfNull(activeConfiguration);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _designationStore = designationStore;
        _publicationGate = store.PublicationGate;
        if (!string.Equals(
                store.ProfilesDirectory,
                designationStore.ProfilesDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The profile and default-designation stores must use the same Profiles directory.",
                nameof(designationStore));
        }

        _activeConfiguration = activeConfiguration;
        _workflowCoordinator = workflowCoordinator;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BlacklistProfileInventory Inventory { get; private set; } =
        BlacklistProfileInventory.Empty;

    public BlacklistSessionDefaultSnapshot SessionDefault { get; private set; } =
        BlacklistSessionDefaultSnapshot.NotConfigured(
            "No default blacklist profile is configured.");

    public bool CanCaptureCurrentConfiguration =>
        CanUseCurrentDiscoveryConfiguration()
        && TryCreateContent(out _, out _);

    public string CaptureReadinessReason => TryCreateContent(out _, out var problem)
        ? "The current Discovery blacklist can be saved as a reusable profile."
        : problem;

    public bool CanLoadCurrentConfiguration => CanUseCurrentDiscoveryConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshInventory();
        ResolveSessionDefault();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public BlacklistProfileInventory RefreshInventory()
    {
        var artifactInventory = _store.CreateInventory();
        var designation = _designationStore.Read();
        var defaultProfileId = designation.State
            == DefaultBlacklistProfileDesignationState.Valid
                ? designation.ProfileId
                : null;
        var profiles = artifactInventory.Items
            .Where(item => item.State == ProfileArtifactReadState.Valid
                           && item.Artifact?.ProfileKind == ProfileKind.Blacklist
                           && item.Artifact.Content is BlacklistProfileContentV1
                           && item.Fingerprint is not null)
            .Select(item => new BlacklistProfileItem(
                item.Artifact!.ProfileId,
                item.Artifact.Name,
                item.Artifact.CreatedAtUtc,
                item.Artifact.UpdatedAtUtc,
                item.Path,
                item.Fingerprint!.Value,
                item.Artifact.ProfileId == defaultProfileId))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProfileId.Value)
            .ToArray();
        var problems = artifactInventory.Items
            .Where(item => item.State != ProfileArtifactReadState.Valid)
            .Select(item => item.Problem ?? "A profile artifact is unavailable.")
            .ToArray();
        var designationProblem = designation.State is
            DefaultBlacklistProfileDesignationState.MalformedOrInvalid
            or DefaultBlacklistProfileDesignationState.UnsupportedSchema
            or DefaultBlacklistProfileDesignationState.UnsafePath
                ? designation.Problem
                : null;

        Inventory = new BlacklistProfileInventory(
            profiles,
            problems,
            defaultProfileId,
            designationProblem);
        return Inventory;
    }

    public BlacklistSessionDefaultSnapshot ResolveSessionDefault()
    {
        var designation = _designationStore.Read();
        if (designation.State == DefaultBlacklistProfileDesignationState.NotConfigured)
        {
            SessionDefault = BlacklistSessionDefaultSnapshot.NotConfigured(
                "No default blacklist profile is configured.");
            return SessionDefault;
        }

        if (designation.State != DefaultBlacklistProfileDesignationState.Valid
            || designation.ProfileId is not { } profileId)
        {
            return SetInvalidSessionDefault(
                profileId: null,
                designation.Problem
                ?? "The default blacklist designation is invalid.");
        }

        var inspection = _store.FindById(profileId);
        if (inspection.State != ProfileArtifactReadState.Valid
            || inspection.Artifact is null)
        {
            return SetInvalidSessionDefault(
                profileId,
                inspection.Problem
                ?? "The designated default blacklist profile is unavailable.");
        }

        if (inspection.Artifact.ProfileKind != ProfileKind.Blacklist
            || inspection.Artifact.Content is not BlacklistProfileContentV1)
        {
            return SetInvalidSessionDefault(
                profileId,
                "The designated profile is not a compatible blacklist profile.");
        }

        SessionDefault = BlacklistSessionDefaultSnapshot.Resolved(inspection.Artifact);
        return SessionDefault;
    }

    public BlacklistProfileOperationResult SaveNew(string name)
    {
        if (!TryCreateContent(out var content, out var problem))
        {
            return BlacklistProfileOperationResult.Failure(problem);
        }

        var profileId = ProfileId.CreateNew();
        var now = UtcNow();
        var artifact = new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            profileId,
            ProfileKind.Blacklist,
            name,
            now,
            now,
            content);
        var write = _store.Create(
            $"{profileId}{ProfileArtifactStore.FileExtension}",
            artifact);
        if (!write.Succeeded)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                write.Problem ?? "The blacklist profile could not be saved.");
        }

        var profile = RefreshInventory().Profiles.Single(
            item => item.ProfileId == profileId);
        return BlacklistProfileOperationResult.Success(
            $"Saved blacklist profile '{profile.Name}'.",
            profile);
    }

    public BlacklistProfileOperationResult Load(BlacklistProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        if (!CanUseCurrentDiscoveryConfiguration())
        {
            return BlacklistProfileOperationResult.Failure(
                "Run Discovery successfully and wait for the active operation to finish before loading a blacklist profile.");
        }

        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var entries = ((BlacklistProfileContentV1)resolved.Artifact.Content).Entries.ToHashSet();
        var snapshot = _activeConfiguration.Current;
        var currentPortableIdentities = snapshot.Items
            .Select(item => PortableDiscoveryInformationIdentity.From(item.Identity))
            .ToHashSet();
        var matchedCount = entries.Count(currentPortableIdentities.Contains);
        var unmatchedCount = entries.Count - matchedCount;
        var changes = snapshot.Items
            .Select(item => new
            {
                item.Identity,
                item.Disposition,
                ShouldBeBlacklisted = entries.Contains(
                    PortableDiscoveryInformationIdentity.From(item.Identity))
            })
            .Where(item => item.ShouldBeBlacklisted
                ? item.Disposition != DiscoveryInformationDisposition.Blacklisted
                : item.Disposition == DiscoveryInformationDisposition.Blacklisted)
            .ToArray();

        if (changes.Length == 0)
        {
            return BlacklistProfileOperationResult.Success(
                CreateLoadMessage(0, matchedCount, unmatchedCount),
                selectedProfile,
                matchedCount,
                unmatchedCount);
        }

        var workflowResult = _workflowCoordinator.RecordDiscoveryConfigurationChanged();
        if (!workflowResult.Accepted)
        {
            return BlacklistProfileOperationResult.Failure(
                workflowResult.Rejection?.Reason
                ?? "The blacklist profile could not be applied while another operation is active.");
        }

        var blacklist = changes
            .Where(item => item.ShouldBeBlacklisted)
            .Select(item => item.Identity);
        var removeBlacklist = changes
            .Where(item => !item.ShouldBeBlacklisted)
            .Select(item => item.Identity);
        var changedCount = _activeConfiguration.SetBlacklisted(blacklist, isBlacklisted: true)
            + _activeConfiguration.SetBlacklisted(removeBlacklist, isBlacklisted: false);
        return BlacklistProfileOperationResult.Success(
            CreateLoadMessage(changedCount, matchedCount, unmatchedCount),
            selectedProfile,
            matchedCount,
            unmatchedCount,
            changedCount);
    }

    public BlacklistProfileOperationResult Update(BlacklistProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        if (!TryCreateContent(out var content, out var problem))
        {
            return BlacklistProfileOperationResult.Failure(problem);
        }

        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var current = resolved.Artifact;
        if (!ProfileTimestamp.TryAdvance(
                current.UpdatedAtUtc,
                _timeProvider,
                out var updatedAtUtc))
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                "The profile timestamp is already at the maximum supported value and cannot be advanced. Save a new profile instead.");
        }

        var write = _store.Update(
            current with
            {
                UpdatedAtUtc = updatedAtUtc,
                Content = content
            },
            selectedProfile.Fingerprint);
        if (!write.Succeeded)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                write.Problem ?? "The blacklist profile could not be updated.",
                requiresReselection: true);
        }

        var profile = RefreshInventory().Profiles.Single(
            item => item.ProfileId == selectedProfile.ProfileId);
        if (Inventory.DefaultProfileId == profile.ProfileId)
        {
            ResolveSessionDefault();
        }

        return BlacklistProfileOperationResult.Success(
            $"Updated blacklist profile '{profile.Name}'.",
            profile);
    }

    public BlacklistProfileOperationResult Clone(
        BlacklistProfileItem selectedProfile,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        var resolved = ResolveSelectedProfile(selectedProfile);
        if (!resolved.Succeeded || resolved.Artifact is null)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                resolved.Problem!,
                requiresReselection: true);
        }

        var profileId = ProfileId.CreateNew();
        var now = UtcNow();
        var sourceContent = (BlacklistProfileContentV1)resolved.Artifact.Content;
        var clone = new ProfileArtifactV1(
            ProfileArtifactV1.CurrentSchemaVersion,
            profileId,
            ProfileKind.Blacklist,
            newName,
            now,
            now,
            new BlacklistProfileContentV1(sourceContent.Entries.ToArray()));
        var write = _store.Create(
            $"{profileId}{ProfileArtifactStore.FileExtension}",
            clone);
        if (!write.Succeeded)
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                write.Problem ?? "The blacklist profile could not be cloned.");
        }

        var profile = RefreshInventory().Profiles.Single(
            item => item.ProfileId == profileId);
        return BlacklistProfileOperationResult.Success(
            $"Cloned blacklist profile as '{profile.Name}'.",
            profile);
    }

    public BlacklistProfileOperationResult Delete(BlacklistProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        try
        {
            using var publicationLease = _publicationGate.Acquire();
            var resolved = ResolveSelectedProfile(selectedProfile);
            if (!resolved.Succeeded || resolved.Artifact is null)
            {
                RefreshInventory();
                return BlacklistProfileOperationResult.Failure(
                    resolved.Problem!,
                    requiresReselection: true);
            }

            var designation = _designationStore.Read(publicationLease);
            var deletingDefault = designation.State
                == DefaultBlacklistProfileDesignationState.Valid
                && designation.ProfileId == selectedProfile.ProfileId;
            var deleted = _store.Delete(
                selectedProfile.ProfileId,
                selectedProfile.Fingerprint,
                publicationLease);
            if (!deleted.Succeeded)
            {
                RefreshInventory();
                return BlacklistProfileOperationResult.Failure(
                    deleted.Problem ?? "The blacklist profile could not be deleted.",
                    requiresReselection: true);
            }

            if (deletingDefault)
            {
                var cleared = _designationStore.ClearIfMatches(
                    selectedProfile.ProfileId,
                    publicationLease);
                RefreshInventory();
                ResolveSessionDefault();
                if (!cleared.Succeeded)
                {
                    return BlacklistProfileOperationResult.Failure(
                        $"Deleted blacklist profile '{selectedProfile.Name}', but its default designation could not be cleared. {cleared.Message}",
                        requiresReselection: true);
                }
            }
            else
            {
                RefreshInventory();
            }

            return BlacklistProfileOperationResult.Success(
                $"Deleted blacklist profile '{selectedProfile.Name}'.");
        }
        catch (Exception exception) when (IsControlledPublicationFailure(exception))
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                $"The blacklist profile could not be deleted safely ({exception.Message}).",
                requiresReselection: true);
        }
    }

    public BlacklistProfileOperationResult SetDefault(BlacklistProfileItem selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(selectedProfile);
        try
        {
            using var publicationLease = _publicationGate.Acquire();
            var resolved = ResolveSelectedProfile(selectedProfile);
            if (!resolved.Succeeded || resolved.Artifact is null)
            {
                RefreshInventory();
                return BlacklistProfileOperationResult.Failure(
                    resolved.Problem!,
                    requiresReselection: true);
            }

            var write = _designationStore.Set(
                selectedProfile.ProfileId,
                publicationLease);
            if (!write.Succeeded)
            {
                RefreshInventory();
                return BlacklistProfileOperationResult.Failure(write.Message);
            }

            var profile = RefreshInventory().Profiles.Single(
                item => item.ProfileId == selectedProfile.ProfileId);
            SessionDefault = BlacklistSessionDefaultSnapshot.Resolved(resolved.Artifact);
            return BlacklistProfileOperationResult.Success(
                $"'{profile.Name}' is now the default blacklist profile.",
                profile);
        }
        catch (Exception exception) when (IsControlledPublicationFailure(exception))
        {
            RefreshInventory();
            return BlacklistProfileOperationResult.Failure(
                $"The default blacklist profile could not be designated safely ({exception.Message}).",
                requiresReselection: true);
        }
    }

    public BlacklistProfileOperationResult ClearDefault()
    {
        var clear = _designationStore.Clear();
        RefreshInventory();
        if (!clear.Succeeded)
        {
            return BlacklistProfileOperationResult.Failure(clear.Message);
        }

        SessionDefault = BlacklistSessionDefaultSnapshot.NotConfigured(
            "No default blacklist profile is configured.");
        return BlacklistProfileOperationResult.Success(
            "Cleared the default blacklist profile designation.");
    }

    private bool TryCreateContent(
        out BlacklistProfileContentV1 content,
        out string problem)
    {
        content = new BlacklistProfileContentV1([]);
        if (!CanUseCurrentDiscoveryConfiguration())
        {
            problem = "Run Discovery successfully and wait for the active operation to finish before saving a blacklist profile.";
            return false;
        }

        var snapshot = _activeConfiguration.Current;
        if (snapshot.Items.Count == 0)
        {
            problem = "The current Discovery result does not contain information to save.";
            return false;
        }

        var entries = new List<PortableDiscoveryInformationIdentity>();
        foreach (var group in snapshot.Items.GroupBy(
                     item => PortableDiscoveryInformationIdentity.From(item.Identity)))
        {
            var blacklistStates = group
                .Select(item => item.Disposition == DiscoveryInformationDisposition.Blacklisted)
                .Distinct()
                .ToArray();
            if (blacklistStates.Length != 1)
            {
                problem =
                    $"'{group.Key.InformationType}' has conflicting blacklist states across Source Sets. Make those states consistent before saving the reusable profile.";
                return false;
            }

            if (blacklistStates[0])
            {
                entries.Add(group.Key);
            }
        }

        content = new BlacklistProfileContentV1(entries
            .OrderBy(entry => entry.StructuralPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.InformationType, StringComparer.Ordinal)
            .ThenBy(entry => entry.CandidateKind)
            .ThenBy(entry => entry.StructuralIdentity, StringComparer.Ordinal)
            .ToArray());
        problem = string.Empty;
        return true;
    }

    private ResolvedProfile ResolveSelectedProfile(BlacklistProfileItem selectedProfile)
    {
        var inspection = _store.FindById(selectedProfile.ProfileId);
        if (inspection.State != ProfileArtifactReadState.Valid
            || inspection.Artifact is null
            || inspection.Fingerprint is not { } fingerprint)
        {
            return ResolvedProfile.Failure(
                inspection.Problem ?? "The selected profile is no longer available.");
        }

        if (inspection.Artifact.ProfileKind != ProfileKind.Blacklist
            || inspection.Artifact.Content is not BlacklistProfileContentV1)
        {
            return ResolvedProfile.Failure(
                "The selected artifact is not a blacklist profile.");
        }

        if (fingerprint != selectedProfile.Fingerprint)
        {
            return ResolvedProfile.Failure(
                "The selected profile changed after it was listed. Refresh and select it again before continuing.");
        }

        return ResolvedProfile.Success(inspection.Artifact);
    }

    private BlacklistSessionDefaultSnapshot SetInvalidSessionDefault(
        ProfileId? profileId,
        string problem)
    {
        SessionDefault = BlacklistSessionDefaultSnapshot.Invalid(profileId, problem);
        _logger.LogWarning(
            "Default blacklist profile resolution failed: {DefaultBlacklistProblem}",
            problem);
        return SessionDefault;
    }

    private bool CanUseCurrentDiscoveryConfiguration()
    {
        var workflow = _workflowCoordinator.Current;
        return workflow.Discovery == WorkflowArtifactStatus.Current
            && workflow.ActiveOperation is null;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static bool IsControlledPublicationFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException;

    private static string CreateLoadMessage(
        int changedCount,
        int matchedCount,
        int unmatchedCount) =>
        $"Blacklist profile applied: {changedCount} disposition changes, {matchedCount} matched profile entries, {unmatchedCount} unmatched.";

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
