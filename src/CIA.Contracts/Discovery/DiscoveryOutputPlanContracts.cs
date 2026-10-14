using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Discovery;

[JsonConverter(typeof(OutputSetIdJsonConverter))]
public readonly record struct OutputSetId
{
    private OutputSetId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static OutputSetId CreateNew() => new(Guid.CreateVersion7());

    public static OutputSetId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Output Set IDs must be non-empty UUID values.", nameof(value));
        }

        return new OutputSetId(value);
    }

    public override string ToString() => Value.ToString("D");
}

public sealed record DiscoveryOutputModelKey
{
    [JsonConstructor]
    public DiscoveryOutputModelKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DiscoveryOutputSetDefinition
{
    public const int MaximumNameLength = 100;

    [JsonConstructor]
    public DiscoveryOutputSetDefinition(
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        DiscoveryOutputModelKey modelKey,
        string name,
        int setNumber,
        int modelOrder,
        bool isEnabled,
        IReadOnlyList<RowFamilyId> rowFamilyIds)
    {
        if (outputSetId.Value == Guid.Empty)
        {
            throw new ArgumentException("An Output Set requires an identity.", nameof(outputSetId));
        }

        if (parentSourceSetId.Value == Guid.Empty)
        {
            throw new ArgumentException("An Output Set requires a parent Source Set.", nameof(parentSourceSetId));
        }

        ArgumentNullException.ThrowIfNull(modelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"Output Set names cannot exceed {MaximumNameLength} characters.");
        }

        if (setNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(setNumber));
        }

        if (modelOrder < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(modelOrder));
        }

        ArgumentNullException.ThrowIfNull(rowFamilyIds);
        var families = rowFamilyIds.ToArray();
        if (families.Length == 0
            || families.Any(family => family.SourceSetId != parentSourceSetId)
            || families.Distinct().Count() != families.Length)
        {
            throw new ArgumentException(
                "An Output Set requires unique Row Families from its parent Source Set.",
                nameof(rowFamilyIds));
        }

        OutputSetId = outputSetId;
        ParentSourceSetId = parentSourceSetId;
        ModelKey = modelKey;
        Name = normalizedName;
        SetNumber = setNumber;
        ModelOrder = modelOrder;
        IsEnabled = isEnabled;
        RowFamilyIds = new ReadOnlyCollection<RowFamilyId>(families);
    }

    public OutputSetId OutputSetId { get; }

    public SourceSetId ParentSourceSetId { get; }

    public DiscoveryOutputModelKey ModelKey { get; }

    public string Name { get; }

    public int SetNumber { get; }

    public int ModelOrder { get; }

    public bool IsEnabled { get; }

    public IReadOnlyList<RowFamilyId> RowFamilyIds { get; }
}

public sealed record DiscoveryParentOutputPlan
{
    [JsonConstructor]
    public DiscoveryParentOutputPlan(
        SourceSetId parentSourceSetId,
        IReadOnlyList<DiscoveryOutputSetDefinition> outputSets)
    {
        if (parentSourceSetId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A parent output plan requires a Source Set identity.",
                nameof(parentSourceSetId));
        }

        ArgumentNullException.ThrowIfNull(outputSets);
        var sets = outputSets.ToArray();
        if (sets.Any(set => set is null || set.ParentSourceSetId != parentSourceSetId)
            || sets.Select(set => set.ModelOrder).Distinct().Count() != sets.Length)
        {
            throw new ArgumentException(
                "A parent output plan requires unique ordered Output Sets from its Source Set.",
                nameof(outputSets));
        }

        ParentSourceSetId = parentSourceSetId;
        OutputSets = new ReadOnlyCollection<DiscoveryOutputSetDefinition>(
            sets.OrderBy(set => set.ModelOrder).ToArray());
    }

    public SourceSetId ParentSourceSetId { get; }

    public IReadOnlyList<DiscoveryOutputSetDefinition> OutputSets { get; }
}

public sealed record DiscoveryOutputPlanSnapshot
{
    [JsonConstructor]
    public DiscoveryOutputPlanSnapshot(IReadOnlyList<DiscoveryParentOutputPlan> parentPlans)
    {
        ArgumentNullException.ThrowIfNull(parentPlans);
        var plans = parentPlans.ToArray();
        if (plans.Any(plan => plan is null))
        {
            throw new ArgumentException(
                "A Discovery output plan cannot contain null parent plans.",
                nameof(parentPlans));
        }

        var sets = plans.SelectMany(plan => plan.OutputSets).ToArray();
        if (plans.Select(plan => plan.ParentSourceSetId).Distinct().Count() != plans.Length
            || sets.Select(set => set.OutputSetId).Distinct().Count() != sets.Length
            || sets.Select(set => set.SetNumber).Distinct().Count() != sets.Length
            || sets.Select(set => (set.ParentSourceSetId, set.ModelKey)).Distinct().Count()
                != sets.Length)
        {
            throw new ArgumentException(
                "A Discovery output plan requires unique identities, numbers and parent-local models.",
                nameof(parentPlans));
        }

        ParentPlans = new ReadOnlyCollection<DiscoveryParentOutputPlan>(plans);
        OutputSets = new ReadOnlyCollection<DiscoveryOutputSetDefinition>(
            sets.OrderBy(set => set.SetNumber).ToArray());
    }

    public IReadOnlyList<DiscoveryParentOutputPlan> ParentPlans { get; }

    [JsonIgnore]
    public IReadOnlyList<DiscoveryOutputSetDefinition> OutputSets { get; }

    public static DiscoveryOutputPlanSnapshot Empty { get; } = new([]);
}

internal sealed class OutputSetIdJsonConverter : JsonConverter<OutputSetId>
{
    public override OutputSetId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("An Output Set ID must be a UUID string.");
        }

        try
        {
            return OutputSetId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("An Output Set ID must be a non-empty UUID value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        OutputSetId value,
        JsonSerializerOptions options)
    {
        if (value.Value == Guid.Empty)
        {
            throw new JsonException("An Output Set ID must be a non-empty UUID value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
