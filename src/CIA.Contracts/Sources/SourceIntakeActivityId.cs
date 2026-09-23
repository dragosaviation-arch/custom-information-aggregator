using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Sources;

[JsonConverter(typeof(SourceIntakeActivityIdJsonConverter))]
public readonly record struct SourceIntakeActivityId
{
    private SourceIntakeActivityId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static SourceIntakeActivityId CreateNew() =>
        new(Guid.CreateVersion7());

    public static SourceIntakeActivityId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Source-intake activity IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new SourceIntakeActivityId(value);
    }

    public override string ToString() => Value.ToString("D");

    internal static bool IsValid(Guid value) =>
        value != Guid.Empty && value.Version == 7;
}

internal sealed class SourceIntakeActivityIdJsonConverter
    : JsonConverter<SourceIntakeActivityId>
{
    public override SourceIntakeActivityId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A source-intake activity ID must be a UUID string.");
        }

        try
        {
            return SourceIntakeActivityId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "A source-intake activity ID must be a non-empty UUIDv7 value.",
                exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        SourceIntakeActivityId value,
        JsonSerializerOptions options)
    {
        if (!SourceIntakeActivityId.IsValid(value.Value))
        {
            throw new JsonException(
                "A source-intake activity ID must be a non-empty UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
