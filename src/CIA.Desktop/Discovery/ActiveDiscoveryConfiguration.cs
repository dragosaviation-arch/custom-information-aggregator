using CIA.Contracts.Discovery;

namespace CIA.Desktop.Discovery;

public sealed class ActiveDiscoveryConfiguration
{
    private readonly object _stateGate = new();
    private Dictionary<string, DiscoveryInformationDisposition> _items = new(
        StringComparer.Ordinal);

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

    public void Synchronize(IEnumerable<string> informationTypes)
    {
        ArgumentNullException.ThrowIfNull(informationTypes);

        var identities = informationTypes.ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Discovery information identities cannot be empty or whitespace.",
                nameof(informationTypes));
        }

        var uniqueIdentities = identities
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        lock (_stateGate)
        {
            _items = uniqueIdentities.ToDictionary(
                identity => identity,
                identity => _items.GetValueOrDefault(
                    identity,
                    DiscoveryInformationDisposition.Neutral),
                StringComparer.Ordinal);
        }
    }

    public int SetSelection(IEnumerable<string> informationTypes, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(informationTypes);
        var changedCount = 0;

        lock (_stateGate)
        {
            foreach (var identity in informationTypes.Distinct(StringComparer.Ordinal))
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
        }

        return changedCount;
    }

    public bool SetBlacklisted(string informationType, bool isBlacklisted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        lock (_stateGate)
        {
            if (!_items.TryGetValue(informationType, out var current))
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

            _items[informationType] = next;
            return true;
        }
    }

    private DiscoveryConfigurationSnapshot CreateSnapshot()
    {
        return new DiscoveryConfigurationSnapshot(
            _items
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new DiscoveryConfigurationItem(item.Key, item.Value))
                .ToArray());
    }
}
