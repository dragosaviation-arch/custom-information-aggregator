using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Core.ManagedStorage;

[JsonConverter(typeof(ManagedStorageArtifactIdJsonConverter))]
public readonly record struct ManagedStorageArtifactId
{
    private ManagedStorageArtifactId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static ManagedStorageArtifactId CreateNew() =>
        new(Guid.CreateVersion7());

    public static ManagedStorageArtifactId From(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Managed-storage artifact IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new ManagedStorageArtifactId(value);
    }

    public override string ToString() => Value.ToString("D");
}

internal sealed class ManagedStorageArtifactIdJsonConverter
    : JsonConverter<ManagedStorageArtifactId>
{
    public override ManagedStorageArtifactId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A managed-storage artifact ID must be a UUID string.");
        }

        try
        {
            return ManagedStorageArtifactId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "A managed-storage artifact ID must be a non-empty UUIDv7 value.",
                exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ManagedStorageArtifactId value,
        JsonSerializerOptions options)
    {
        if (value.Value == Guid.Empty || value.Value.Version != 7)
        {
            throw new JsonException(
                "A managed-storage artifact ID must be a non-empty UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
