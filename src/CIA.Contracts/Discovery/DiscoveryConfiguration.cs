using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Discovery;

public enum DiscoveryInformationDisposition
{
    Neutral = 0,
    Selected = 1,
    Blacklisted = 2
}

public enum RepeatedDataLayout
{
    AlignRepeatedGroupsByPosition = 0,
    StructuralRows = 1,
    AllCombinations = 2,
    NumberRepeatedValuesIntoColumns = 3
}

public sealed record DiscoveryConfigurationItem
{
    public DiscoveryConfigurationItem(
        string informationType,
        DiscoveryInformationDisposition disposition)
        : this(DiscoveryInformationIdentity.CreateLegacy(informationType), disposition)
    {
    }

    [JsonConstructor]
    public DiscoveryConfigurationItem(
        DiscoveryInformationIdentity identity,
        DiscoveryInformationDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        Identity = identity;
        Disposition = disposition;
    }

    public DiscoveryInformationIdentity Identity { get; }

    [JsonIgnore]
    public string InformationType => Identity.InformationType;

    public DiscoveryInformationDisposition Disposition { get; }
}

public sealed record SourceSetDiscoveryConfiguration
{
    [JsonConstructor]
    public SourceSetDiscoveryConfiguration(
        SourceSetId sourceSetId,
        RepeatedDataLayout repeatedDataLayout)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A Discovery Source Set configuration requires a Source Set ID.",
                nameof(sourceSetId));
        }

        if (!Enum.IsDefined(repeatedDataLayout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatedDataLayout),
                repeatedDataLayout,
                null);
        }

        SourceSetId = sourceSetId;
        RepeatedDataLayout = repeatedDataLayout;
    }

    public SourceSetId SourceSetId { get; }

    public RepeatedDataLayout RepeatedDataLayout { get; }
}

public sealed record DiscoveryConfigurationSnapshot
{
    public DiscoveryConfigurationSnapshot(IReadOnlyList<DiscoveryConfigurationItem> items)
        : this(
            items,
            items
                .Select(item => item.Identity.SourceSetId)
                .Distinct()
                .Select(sourceSetId => new SourceSetDiscoveryConfiguration(
                    sourceSetId,
                    RepeatedDataLayout.AlignRepeatedGroupsByPosition))
                .ToArray())
    {
    }

    [JsonConstructor]
    public DiscoveryConfigurationSnapshot(
        IReadOnlyList<DiscoveryConfigurationItem> items,
        IReadOnlyList<SourceSetDiscoveryConfiguration> sourceSets)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sourceSets);

        var itemArray = items.ToArray();
        var sourceSetArray = sourceSets.ToArray();
        if (itemArray.Any(item => item is null))
        {
            throw new ArgumentException(
                "A Discovery configuration cannot contain null items.",
                nameof(items));
        }

        if (itemArray
            .Select(item => item.Identity)
            .Distinct()
            .Count() != itemArray.Length)
        {
            throw new ArgumentException(
                "Discovery configuration information identities must be unique.",
                nameof(items));
        }

        if (sourceSetArray.Any(sourceSet => sourceSet is null)
            || sourceSetArray.Select(sourceSet => sourceSet.SourceSetId).Distinct().Count()
                != sourceSetArray.Length)
        {
            throw new ArgumentException(
                "Discovery Source Set configurations must be valid and unique.",
                nameof(sourceSets));
        }

        var configuredSourceSets = sourceSetArray
            .Select(sourceSet => sourceSet.SourceSetId)
            .ToHashSet();
        if (itemArray.Any(item => !configuredSourceSets.Contains(item.Identity.SourceSetId)))
        {
            throw new ArgumentException(
                "Every discovered information item requires its Source Set configuration.",
                nameof(sourceSets));
        }

        Items = new ReadOnlyCollection<DiscoveryConfigurationItem>(itemArray);
        SourceSets = new ReadOnlyCollection<SourceSetDiscoveryConfiguration>(sourceSetArray);
    }

    public IReadOnlyList<DiscoveryConfigurationItem> Items { get; }

    public IReadOnlyList<SourceSetDiscoveryConfiguration> SourceSets { get; }

    [JsonIgnore]
    public int SelectedCount => Items.Count(
        item => item.Disposition == DiscoveryInformationDisposition.Selected);

    [JsonIgnore]
    public int BlacklistedCount => Items.Count(
        item => item.Disposition == DiscoveryInformationDisposition.Blacklisted);
}
