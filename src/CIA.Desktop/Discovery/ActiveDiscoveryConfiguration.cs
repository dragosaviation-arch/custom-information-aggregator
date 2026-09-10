using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Desktop.Discovery;

public sealed class ActiveDiscoveryConfiguration
{
    private readonly object _stateGate = new();
    private Dictionary<DiscoveryInformationIdentity, DiscoveryInformationDisposition> _items = [];
    private Dictionary<DiscoveryInformationIdentity, string> _databaseTagOverrides = [];
    private Dictionary<SourceSetId, RepeatedDataLayout> _repeatedDataLayouts = [];
    private List<SourceSetId> _sourceSetOrder = [];

    public DiscoveryConfigurationSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return CreateSnapshot();
            }
        }
    }

    public IReadOnlyDictionary<string, string> DatabaseTagOverrides
    {
        get
        {
            lock (_stateGate)
            {
                return _items.Keys
                    .GroupBy(identity => identity.InformationType, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        group.Key,
                        Values = group.Select(identity =>
                                _databaseTagOverrides.GetValueOrDefault(identity))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()
                    })
                    .Where(group => group.Values.Length == 1 && group.Values[0] is not null)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Values[0]!,
                        StringComparer.Ordinal);
            }
        }
    }

    public IReadOnlyDictionary<DiscoveryInformationIdentity, string> DatabaseTagOverridesByIdentity
    {
        get
        {
            lock (_stateGate)
            {
                return new Dictionary<DiscoveryInformationIdentity, string>(_databaseTagOverrides);
            }
        }
    }

    public IReadOnlyDictionary<SourceSetId, RepeatedDataLayout> RepeatedDataLayouts
    {
        get
        {
            lock (_stateGate)
            {
                return new Dictionary<SourceSetId, RepeatedDataLayout>(_repeatedDataLayouts);
            }
        }
    }

    public void Synchronize(IEnumerable<string> informationTypes)
    {
        ArgumentNullException.ThrowIfNull(informationTypes);
        Synchronize(informationTypes.Select(CreateLegacyIdentity));
    }

    public void Synchronize(IEnumerable<DiscoveryInformationIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var uniqueIdentities = identities
            .Select(identity => identity ?? throw new ArgumentException(
                "Discovery information identities cannot contain null values.",
                nameof(identities)))
            .Distinct()
            .ToArray();

        lock (_stateGate)
        {
            _items = uniqueIdentities.ToDictionary(
                identity => identity,
                identity => _items.GetValueOrDefault(
                    identity,
                    DiscoveryInformationDisposition.Neutral));
            _databaseTagOverrides = uniqueIdentities
                .Where(_databaseTagOverrides.ContainsKey)
                .ToDictionary(identity => identity, identity => _databaseTagOverrides[identity]);
            SynchronizeSourceSetsCore(uniqueIdentities.Select(identity => identity.SourceSetId));
        }
    }

    public void SynchronizeSourceSets(IEnumerable<SourceSetId> sourceSetIds)
    {
        ArgumentNullException.ThrowIfNull(sourceSetIds);
        lock (_stateGate)
        {
            SynchronizeSourceSetsCore(sourceSetIds);
        }
    }

    public bool SetDatabaseTagOverride(string informationType, string? databaseTagOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        lock (_stateGate)
        {
            var matches = _items.Keys.Where(identity => string.Equals(
                identity.InformationType, informationType, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1
                && SetDatabaseTagOverrideCore(matches[0], databaseTagOverride);
        }
    }

    public bool SetDatabaseTagOverride(
        DiscoveryInformationIdentity identity,
        string? databaseTagOverride)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_stateGate)
        {
            return SetDatabaseTagOverrideCore(identity, databaseTagOverride);
        }
    }

    public int SetSelection(IEnumerable<string> informationTypes, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(informationTypes);
        lock (_stateGate)
        {
            var names = informationTypes.ToHashSet(StringComparer.Ordinal);
            return SetSelectionCore(
                _items.Keys.Where(identity => names.Contains(identity.InformationType)),
                isSelected);
        }
    }

    public int SetSelection(
        IEnumerable<DiscoveryInformationIdentity> identities,
        bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (_stateGate)
        {
            return SetSelectionCore(identities, isSelected);
        }
    }

    public bool SetBlacklisted(string informationType, bool isBlacklisted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        lock (_stateGate)
        {
            var matches = _items.Keys.Where(identity => string.Equals(
                identity.InformationType, informationType, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 && SetBlacklistedCore(matches[0], isBlacklisted);
        }
    }

    public bool SetBlacklisted(
        DiscoveryInformationIdentity identity,
        bool isBlacklisted)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_stateGate)
        {
            return SetBlacklistedCore(identity, isBlacklisted);
        }
    }

    public bool SetRepeatedDataLayout(SourceSetId sourceSetId, RepeatedDataLayout layout)
    {
        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }

        lock (_stateGate)
        {
            if (!_repeatedDataLayouts.TryGetValue(sourceSetId, out var current)
                || current == layout)
            {
                return false;
            }

            _repeatedDataLayouts[sourceSetId] = layout;
            return true;
        }
    }

    private bool SetDatabaseTagOverrideCore(
        DiscoveryInformationIdentity identity,
        string? databaseTagOverride)
    {
        if (!_items.ContainsKey(identity))
        {
            return false;
        }

        var candidate = databaseTagOverride?.Trim();
        string? next = string.IsNullOrWhiteSpace(candidate)
            || string.Equals(candidate, identity.InformationType, StringComparison.Ordinal)
                ? null
                : candidate;
        _databaseTagOverrides.TryGetValue(identity, out var current);
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return false;
        }

        if (next is null)
        {
            _databaseTagOverrides.Remove(identity);
        }
        else
        {
            _databaseTagOverrides[identity] = next;
        }

        return true;
    }

    private int SetSelectionCore(
        IEnumerable<DiscoveryInformationIdentity> identities,
        bool isSelected)
    {
        var changedCount = 0;
        foreach (var identity in identities.Distinct())
        {
            if (!_items.TryGetValue(identity, out var current)
                || (isSelected && current == DiscoveryInformationDisposition.Blacklisted))
            {
                continue;
            }

            var next = isSelected
                ? DiscoveryInformationDisposition.Selected
                : DiscoveryInformationDisposition.Neutral;
            if (current == next || (!isSelected && current != DiscoveryInformationDisposition.Selected))
            {
                continue;
            }

            _items[identity] = next;
            changedCount++;
        }

        return changedCount;
    }

    private bool SetBlacklistedCore(
        DiscoveryInformationIdentity identity,
        bool isBlacklisted)
    {
        if (!_items.TryGetValue(identity, out var current))
        {
            return false;
        }

        var next = isBlacklisted
            ? DiscoveryInformationDisposition.Blacklisted
            : DiscoveryInformationDisposition.Neutral;
        if (current == next || (!isBlacklisted && current != DiscoveryInformationDisposition.Blacklisted))
        {
            return false;
        }

        _items[identity] = next;
        return true;
    }

    private void SynchronizeSourceSetsCore(IEnumerable<SourceSetId> sourceSetIds)
    {
        var unique = sourceSetIds.Distinct().ToArray();
        _sourceSetOrder = _sourceSetOrder
            .Where(unique.Contains)
            .Concat(unique.Where(sourceSetId => !_sourceSetOrder.Contains(sourceSetId)))
            .ToList();
        _repeatedDataLayouts = unique.ToDictionary(
            sourceSetId => sourceSetId,
            sourceSetId => _repeatedDataLayouts.GetValueOrDefault(
                sourceSetId,
                RepeatedDataLayout.AlignRepeatedGroupsByPosition));
    }

    private DiscoveryConfigurationSnapshot CreateSnapshot()
    {
        return new DiscoveryConfigurationSnapshot(
            _items
                .OrderBy(item => _sourceSetOrder.IndexOf(item.Key.SourceSetId))
                .ThenBy(item => item.Key.StructuralIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Key.CandidateKind)
                .ThenBy(item => item.Key.InformationType, StringComparer.Ordinal)
                .Select(item => new DiscoveryConfigurationItem(item.Key, item.Value))
                .ToArray(),
            _sourceSetOrder
                .Where(_repeatedDataLayouts.ContainsKey)
                .Select(sourceSetId => new SourceSetDiscoveryConfiguration(
                    sourceSetId,
                    _repeatedDataLayouts[sourceSetId]))
                .ToArray());
    }

    private static DiscoveryInformationIdentity CreateLegacyIdentity(string informationType)
    {
        return new DiscoveryConfigurationItem(
            informationType,
            DiscoveryInformationDisposition.Neutral).Identity;
    }
}
