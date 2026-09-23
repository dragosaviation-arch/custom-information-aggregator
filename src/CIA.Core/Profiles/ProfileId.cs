using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Core.Profiles;

[JsonConverter(typeof(ProfileIdJsonConverter))]
public readonly record struct ProfileId
{
    private ProfileId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static ProfileId CreateNew() => new(Guid.CreateVersion7());

    public static ProfileId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Profile IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new ProfileId(value);
    }

    public override string ToString() => Value.ToString("D");

    internal static bool IsValid(Guid value) =>
        value != Guid.Empty && value.Version == 7;
}

internal sealed class ProfileIdJsonConverter : JsonConverter<ProfileId>
{
    public override ProfileId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A Profile ID must be a UUID string.");
        }

        try
        {
            return ProfileId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "A Profile ID must be a non-empty UUIDv7 value.",
                exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProfileId value,
        JsonSerializerOptions options)
    {
        if (!ProfileId.IsValid(value.Value))
        {
            throw new JsonException("A Profile ID must be a non-empty UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
