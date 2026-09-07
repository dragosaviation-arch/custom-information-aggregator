using CIA.Contracts.Discovery;

namespace CIA.Desktop.Discovery;

public sealed class ActiveDiscoveryConfiguration
{
    private readonly object _stateGate = new();
    private Dictionary<string, DiscoveryInformationDisposition> _items = new(
        StringComparer.Ordinal);
    private Dictionary<string, string> _databaseTagOverrides = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

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
                return new Dictionary<string, string>(
                    _databaseTagOverrides,
                    StringComparer.Ordinal);
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
            _databaseTagOverrides = uniqueIdentities
                .Where(_databaseTagOverrides.ContainsKey)
                .ToDictionary(
                    identity => identity,
                    identity => _databaseTagOverrides[identity],
                    StringComparer.Ordinal);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool SetDatabaseTagOverride(string informationType, string? databaseTagOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        lock (_stateGate)
        {
            if (!_items.ContainsKey(informationType))
            {
                return false;
            }

            var candidate = databaseTagOverride?.Trim();
            string? next = string.IsNullOrWhiteSpace(candidate)
                || string.Equals(candidate, informationType, StringComparison.Ordinal)
                    ? null
                    : candidate;
            _databaseTagOverrides.TryGetValue(informationType, out var current);
            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return false;
            }

            if (next is null)
            {
                _databaseTagOverrides.Remove(informationType);
            }
            else
            {
                _databaseTagOverrides[informationType] = next;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
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

        if (changedCount > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
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
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
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
