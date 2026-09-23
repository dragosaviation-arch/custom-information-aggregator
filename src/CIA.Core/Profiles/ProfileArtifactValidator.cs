using CIA.Contracts.Sources;

namespace CIA.Core.Profiles;

public static class ProfileArtifactValidator
{
    public const int MaximumProfileNameLength = 200;
    public const int MaximumInformationTypeLength = 512;
    public const int MaximumStructuralValueLength = 16 * 1024;
    public const int MaximumEntries = 100_000;

    public static void Validate(ProfileArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.SchemaVersion != ProfileArtifactV1.CurrentSchemaVersion)
        {
            throw Invalid(
                $"Profile schema version {artifact.SchemaVersion} is not supported.");
        }

        if (!ProfileId.IsValid(artifact.ProfileId.Value))
        {
            throw Invalid("A profile requires a non-empty UUIDv7 Profile ID.");
        }

        if (!Enum.IsDefined(artifact.ProfileKind))
        {
            throw Invalid("The profile kind is not supported.");
        }

        ValidateText(artifact.Name, MaximumProfileNameLength, "profile name");
        ValidateUtc(artifact.CreatedAtUtc, "created timestamp");
        ValidateUtc(artifact.UpdatedAtUtc, "updated timestamp");
        if (artifact.CreatedAtUtc > artifact.UpdatedAtUtc)
        {
            throw Invalid("A profile cannot be updated before it was created.");
        }

        if (artifact.Content is null)
        {
            throw Invalid("A profile requires typed content.");
        }

        switch (artifact.ProfileKind, artifact.Content)
        {
            case (ProfileKind.InformationSelection, InformationSelectionProfileContentV1 content):
                ValidateInformationSelection(content);
                break;
            case (ProfileKind.Blacklist, BlacklistProfileContentV1 content):
                ValidateBlacklist(content);
                break;
            default:
                throw Invalid("The profile content does not match the declared profile kind.");
        }
    }

    public static void ValidateReplacement(
        ProfileArtifactV1 current,
        ProfileArtifactV1 replacement)
    {
        Validate(current);
        Validate(replacement);
        if (current.ProfileId != replacement.ProfileId)
        {
            throw Invalid("A replacement profile must preserve its Profile ID.");
        }

        if (current.ProfileKind != replacement.ProfileKind)
        {
            throw Invalid("A replacement profile must preserve its profile kind.");
        }

        if (current.CreatedAtUtc != replacement.CreatedAtUtc)
        {
            throw Invalid("A replacement profile must preserve its created timestamp.");
        }

        if (replacement.UpdatedAtUtc <= current.UpdatedAtUtc)
        {
            throw Invalid("A replacement profile must advance its updated timestamp.");
        }
    }

    private static void ValidateInformationSelection(
        InformationSelectionProfileContentV1 content)
    {
        if (content.Entries is null || content.Entries.Count > MaximumEntries)
        {
            throw Invalid("Information-selection profile entries are missing or exceed the supported limit.");
        }

        var identities = new HashSet<PortableDiscoveryInformationIdentity>();
        foreach (var entry in content.Entries)
        {
            if (entry is null)
            {
                throw Invalid("Information-selection profile entries cannot be null.");
            }

            ValidateIdentity(entry.Identity);
            if (!Enum.IsDefined(entry.Membership))
            {
                throw Invalid("An information-selection membership state is not supported.");
            }

            if (!identities.Add(entry.Identity))
            {
                throw Invalid("A profile cannot contain duplicate portable information identities.");
            }
        }
    }

    private static void ValidateBlacklist(BlacklistProfileContentV1 content)
    {
        if (content.Entries is null || content.Entries.Count > MaximumEntries)
        {
            throw Invalid("Blacklist profile entries are missing or exceed the supported limit.");
        }

        var identities = new HashSet<PortableDiscoveryInformationIdentity>();
        foreach (var identity in content.Entries)
        {
            ValidateIdentity(identity);
            if (!identities.Add(identity))
            {
                throw Invalid("A profile cannot contain duplicate portable information identities.");
            }
        }
    }

    private static void ValidateIdentity(PortableDiscoveryInformationIdentity? identity)
    {
        if (identity is null)
        {
            throw Invalid("A profile entry requires a portable information identity.");
        }

        ValidateText(
            identity.StructuralPath,
            MaximumStructuralValueLength,
            "structural path");
        ValidateText(
            identity.InformationType,
            MaximumInformationTypeLength,
            "information type");
        ValidateText(
            identity.StructuralIdentity,
            MaximumStructuralValueLength,
            "structural identity");
        if (!Enum.IsDefined(identity.CandidateKind))
        {
            throw Invalid("A portable information candidate kind is not supported.");
        }
    }

    private static void ValidateText(string? value, int maximumLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw Invalid($"The {fieldName} is empty or exceeds {maximumLength} characters.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string fieldName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw Invalid($"The profile {fieldName} must be a non-default UTC timestamp.");
        }
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
