using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Sources;

[JsonConverter(typeof(SourceSetIdJsonConverter))]
public readonly record struct SourceSetId
{
    private SourceSetId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static SourceSetId CreateNew()
    {
        return new SourceSetId(Guid.CreateVersion7());
    }

    public static SourceSetId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Source Set IDs must be non-empty UUID values.", nameof(value));
        }

        return new SourceSetId(value);
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }

    internal static bool IsValid(Guid value)
    {
        return value != Guid.Empty;
    }
}

internal sealed class SourceSetIdJsonConverter : JsonConverter<SourceSetId>
{
    public override SourceSetId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A Source Set ID must be a UUID string.");
        }

        try
        {
            return SourceSetId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("A Source Set ID must be a non-empty UUID value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        SourceSetId value,
        JsonSerializerOptions options)
    {
        if (!SourceSetId.IsValid(value.Value))
        {
            throw new JsonException("A Source Set ID must be a non-empty UUID value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
