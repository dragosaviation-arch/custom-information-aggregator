using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Operations;

[JsonConverter(typeof(OperationIdJsonConverter))]
public readonly record struct OperationId
{
    private OperationId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static OperationId CreateNew()
    {
        return new OperationId(Guid.CreateVersion7());
    }

    public static OperationId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Operation IDs must be non-empty UUIDv7 values.", nameof(value));
        }

        return new OperationId(value);
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }

    internal static bool IsValid(Guid value)
    {
        return value != Guid.Empty && value.Version == 7;
    }
}

internal sealed class OperationIdJsonConverter : JsonConverter<OperationId>
{
    public override OperationId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("An Operation ID must be a UUID string.");
        }

        try
        {
            return OperationId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("An Operation ID must be a non-empty UUIDv7 value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        OperationId value,
        JsonSerializerOptions options)
    {
        if (!OperationId.IsValid(value.Value))
        {
            throw new JsonException("An Operation ID must be a non-empty UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
