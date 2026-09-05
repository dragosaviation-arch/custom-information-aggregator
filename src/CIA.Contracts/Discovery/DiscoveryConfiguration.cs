using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Discovery;

public enum DiscoveryInformationDisposition
{
    Neutral = 0,
    Selected = 1,
    Blacklisted = 2
}

public sealed record DiscoveryConfigurationItem
{
    [JsonConstructor]
    public DiscoveryConfigurationItem(
        string informationType,
        DiscoveryInformationDisposition disposition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        InformationType = informationType;
        Disposition = disposition;
    }

    public string InformationType { get; }

    public DiscoveryInformationDisposition Disposition { get; }
}

public sealed record DiscoveryConfigurationSnapshot
{
    [JsonConstructor]
    public DiscoveryConfigurationSnapshot(IReadOnlyList<DiscoveryConfigurationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemArray = items.ToArray();
        if (itemArray.Any(item => item is null))
        {
            throw new ArgumentException(
                "A Discovery configuration cannot contain null items.",
                nameof(items));
        }

        if (itemArray
            .Select(item => item.InformationType)
            .Distinct(StringComparer.Ordinal)
            .Count() != itemArray.Length)
        {
            throw new ArgumentException(
                "Discovery configuration information identities must be unique.",
                nameof(items));
        }

        Items = new ReadOnlyCollection<DiscoveryConfigurationItem>(itemArray);
    }

    public IReadOnlyList<DiscoveryConfigurationItem> Items { get; }

    [JsonIgnore]
    public int SelectedCount => Items.Count(
        item => item.Disposition == DiscoveryInformationDisposition.Selected);

    [JsonIgnore]
    public int BlacklistedCount => Items.Count(
        item => item.Disposition == DiscoveryInformationDisposition.Blacklisted);
}
