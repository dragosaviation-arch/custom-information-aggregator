using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Sources;

[JsonConverter(typeof(SourceIdJsonConverter))]
public readonly record struct SourceId
{
    private SourceId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static SourceId CreateNew()
    {
        return new SourceId(Guid.CreateVersion7());
    }

    public static SourceId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Source IDs must be non-empty UUID values.", nameof(value));
        }

        return new SourceId(value);
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

internal sealed class SourceIdJsonConverter : JsonConverter<SourceId>
{
    public override SourceId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A Source ID must be a UUID string.");
        }

        try
        {
            return SourceId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("A Source ID must be a non-empty UUID value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        SourceId value,
        JsonSerializerOptions options)
    {
        if (!SourceId.IsValid(value.Value))
        {
            throw new JsonException("A Source ID must be a non-empty UUID value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
