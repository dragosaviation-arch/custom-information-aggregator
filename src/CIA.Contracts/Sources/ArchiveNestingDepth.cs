using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Sources;

[JsonConverter(typeof(ArchiveNestingDepthJsonConverter))]
public readonly record struct ArchiveNestingDepth
{
    public const int DefaultValue = 3;

    private ArchiveNestingDepth(int value)
    {
        Value = value;
    }

    public int Value { get; }

    public static ArchiveNestingDepth Default { get; } = new(DefaultValue);

    public static ArchiveNestingDepth From(int value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "The maximum archive nesting depth must be at least 1.");
        }

        return new ArchiveNestingDepth(value);
    }

    public bool AllowsLevel(int archiveLevel)
    {
        if (!IsValid(Value))
        {
            throw new InvalidOperationException(
                "A valid maximum archive nesting depth is required before evaluating an archive level.");
        }

        if (archiveLevel < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(archiveLevel),
                archiveLevel,
                "Archive levels start at 1 for the outermost archive.");
        }

        return archiveLevel <= Value;
    }

    internal static bool IsValid(int value)
    {
        return value >= 1;
    }

    public override string ToString()
    {
        return Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class ArchiveNestingDepthJsonConverter : JsonConverter<ArchiveNestingDepth>
{
    public override ArchiveNestingDepth Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
        {
            throw new JsonException("The maximum archive nesting depth must be an integer.");
        }

        try
        {
            return ArchiveNestingDepth.From(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                "The maximum archive nesting depth must be at least 1.",
                exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ArchiveNestingDepth value,
        JsonSerializerOptions options)
    {
        if (!ArchiveNestingDepth.IsValid(value.Value))
        {
            throw new JsonException("The maximum archive nesting depth must be at least 1.");
        }

        writer.WriteNumberValue(value.Value);
    }
}
