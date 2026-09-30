using CIA.Contracts.Discovery;
using CIA.Core.Profiles;
using CIA.Desktop.Discovery;

namespace CIA.Desktop.Profiles;

public enum ReusableProfileActivationOrigin
{
    ExplicitLoad = 1,
    StartupDefault = 2
}

public sealed record ActiveInformationSelectionProfileState(
    ProfileId ProfileId,
    InformationSelectionProfileContentV1 Content,
    ReusableProfileActivationOrigin Origin);

public sealed record ActiveBlacklistProfileState(
    ProfileId ProfileId,
    BlacklistProfileContentV1 Content,
    ReusableProfileActivationOrigin Origin);

public sealed record ReusableProfileSessionSnapshot(
    ActiveInformationSelectionProfileState? InformationSelection,
    ActiveBlacklistProfileState? Blacklist);

public sealed record ReusableProfileApplicationResult(
    int BlacklistChanges,
    int InformationSelectionChanges)
{
    public int TotalChanges => BlacklistChanges + InformationSelectionChanges;
}

public sealed class ReusableProfileSessionState
{
    private readonly object _stateGate = new();
    private ActiveInformationSelectionProfileState? _informationSelection;
    private ActiveBlacklistProfileState? _blacklist;

    public ReusableProfileSessionSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return new ReusableProfileSessionSnapshot(
                    _informationSelection,
                    _blacklist);
            }
        }
    }

    public void ActivateInformationSelection(
        ProfileId profileId,
        InformationSelectionProfileContentV1 content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = ProfileId.From(profileId.Value);
        var copiedContent = Copy(content);
        lock (_stateGate)
        {
            _informationSelection = new ActiveInformationSelectionProfileState(
                profileId,
                copiedContent,
                ReusableProfileActivationOrigin.ExplicitLoad);
        }
    }

    public void ActivateBlacklist(
        ProfileId profileId,
        BlacklistProfileContentV1 content,
        ReusableProfileActivationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = ProfileId.From(profileId.Value);
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }

        var copiedContent = Copy(content);
        lock (_stateGate)
        {
            _blacklist = new ActiveBlacklistProfileState(profileId, copiedContent, origin);
        }
    }

    public void SeedStartupDefaultBlacklist(
        ProfileId? profileId,
        BlacklistProfileContentV1? content)
    {
        if ((profileId is null) != (content is null))
        {
            throw new ArgumentException(
                "A startup default requires both a Profile ID and blacklist content.");
        }

        lock (_stateGate)
        {
            if (_blacklist?.Origin == ReusableProfileActivationOrigin.ExplicitLoad)
            {
                return;
            }

            _blacklist = profileId is { } resolvedProfileId && content is not null
                ? new ActiveBlacklistProfileState(
                    resolvedProfileId,
                    Copy(content),
                    ReusableProfileActivationOrigin.StartupDefault)
                : null;
        }
    }

    public ReusableProfileApplicationResult ApplyTo(
        ActiveDiscoveryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ReusableProfileSessionSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = new ReusableProfileSessionSnapshot(
                _informationSelection,
                _blacklist);
        }

        var blacklistChanges = ApplyBlacklist(configuration, snapshot.Blacklist);
        var informationSelectionChanges = ApplyInformationSelection(
            configuration,
            snapshot.InformationSelection);
        return new ReusableProfileApplicationResult(
            blacklistChanges,
            informationSelectionChanges);
    }

    private static int ApplyBlacklist(
        ActiveDiscoveryConfiguration configuration,
        ActiveBlacklistProfileState? activeProfile)
    {
        if (activeProfile is null)
        {
            return 0;
        }

        var entries = activeProfile.Content.Entries.ToHashSet();
        var items = configuration.Current.Items;
        var blacklist = items
            .Where(item => entries.Contains(
                PortableDiscoveryInformationIdentity.From(item.Identity)))
            .Select(item => item.Identity);
        var removeBlacklist = items
            .Where(item => item.Disposition == DiscoveryInformationDisposition.Blacklisted
                           && !entries.Contains(
                               PortableDiscoveryInformationIdentity.From(item.Identity)))
            .Select(item => item.Identity);
        return configuration.SetBlacklisted(blacklist, isBlacklisted: true)
            + configuration.SetBlacklisted(removeBlacklist, isBlacklisted: false);
    }

    private static int ApplyInformationSelection(
        ActiveDiscoveryConfiguration configuration,
        ActiveInformationSelectionProfileState? activeProfile)
    {
        if (activeProfile is null)
        {
            return 0;
        }

        var entries = activeProfile.Content.Entries.ToDictionary(entry => entry.Identity);
        var changes = configuration.Current.Items
            .Where(item => item.Disposition != DiscoveryInformationDisposition.Blacklisted)
            .Select(item => new
            {
                item.Identity,
                Entry = entries.GetValueOrDefault(
                    PortableDiscoveryInformationIdentity.From(item.Identity))
            })
            .Where(item => item.Entry is not null)
            .ToArray();
        var selected = changes
            .Where(item => item.Entry!.Membership == InformationSelectionMembership.Selected)
            .Select(item => item.Identity);
        var excluded = changes
            .Where(item => item.Entry!.Membership == InformationSelectionMembership.Excluded)
            .Select(item => item.Identity);
        return configuration.SetSelection(selected, isSelected: true)
            + configuration.SetSelection(excluded, isSelected: false);
    }

    private static InformationSelectionProfileContentV1 Copy(
        InformationSelectionProfileContentV1 content) =>
        new(content.Entries.ToArray());

    private static BlacklistProfileContentV1 Copy(BlacklistProfileContentV1 content) =>
        new(content.Entries.ToArray());
}
