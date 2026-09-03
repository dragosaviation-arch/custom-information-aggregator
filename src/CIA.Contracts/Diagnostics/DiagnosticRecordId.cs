using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Diagnostics;

[JsonConverter(typeof(DiagnosticRecordIdJsonConverter))]
public readonly record struct DiagnosticRecordId
{
    private DiagnosticRecordId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static DiagnosticRecordId CreateNew()
    {
        return new DiagnosticRecordId(Guid.CreateVersion7());
    }

    public static DiagnosticRecordId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Diagnostic record IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new DiagnosticRecordId(value);
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

internal sealed class DiagnosticRecordIdJsonConverter : JsonConverter<DiagnosticRecordId>
{
    public override DiagnosticRecordId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A diagnostic record ID must be a UUID string.");
        }

        try
        {
            return DiagnosticRecordId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "A diagnostic record ID must be a non-empty UUIDv7 value.",
                exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        DiagnosticRecordId value,
        JsonSerializerOptions options)
    {
        if (!DiagnosticRecordId.IsValid(value.Value))
        {
            throw new JsonException(
                "A diagnostic record ID must be a non-empty UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
