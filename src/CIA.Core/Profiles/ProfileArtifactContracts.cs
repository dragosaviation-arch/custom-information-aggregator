using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Core.Profiles;

public enum ProfileKind
{
    InformationSelection = 1,
    Blacklist = 2
}

public enum InformationSelectionMembership
{
    Selected = 1,
    Excluded = 2
}

public sealed record PortableDiscoveryInformationIdentity(
    string StructuralPath,
    string InformationType,
    SourceValueCandidateKind CandidateKind,
    string StructuralIdentity)
{
    public static PortableDiscoveryInformationIdentity From(
        DiscoveryInformationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new PortableDiscoveryInformationIdentity(
            identity.StructuralPath,
            identity.InformationType,
            identity.CandidateKind,
            identity.StructuralIdentity);
    }
}

public sealed record InformationSelectionProfileEntryV1(
    PortableDiscoveryInformationIdentity Identity,
    InformationSelectionMembership Membership);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$contentType")]
[JsonDerivedType(
    typeof(InformationSelectionProfileContentV1),
    typeDiscriminator: "informationSelection")]
[JsonDerivedType(typeof(BlacklistProfileContentV1), typeDiscriminator: "blacklist")]
public abstract record ProfileContentV1;

public sealed record InformationSelectionProfileContentV1(
    IReadOnlyList<InformationSelectionProfileEntryV1> Entries) : ProfileContentV1;

public sealed record BlacklistProfileContentV1(
    IReadOnlyList<PortableDiscoveryInformationIdentity> Entries) : ProfileContentV1;

public sealed record ProfileArtifactV1(
    int SchemaVersion,
    ProfileId ProfileId,
    ProfileKind ProfileKind,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ProfileContentV1 Content)
{
    public const int CurrentSchemaVersion = 1;
}

public enum ProfileArtifactReadState
{
    Valid = 1,
    MalformedOrInvalid = 2,
    UnsupportedSchema = 3,
    AmbiguousProfileId = 4,
    UnsafePath = 5,
    NotFound = 6
}

public readonly record struct ProfileArtifactFingerprint
{
    private ProfileArtifactFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProfileArtifactFingerprint Parse(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "A profile artifact fingerprint must be a 64-character SHA-256 value.",
                nameof(value));
        }

        return new ProfileArtifactFingerprint(value.ToLowerInvariant());
    }

    public override string ToString() => Value;

    internal static ProfileArtifactFingerprint FromBytes(ReadOnlySpan<byte> content) =>
        new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));

    internal static bool IsValid(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record ProfileArtifactInspection(
    string Path,
    ProfileArtifactReadState State,
    ProfileArtifactV1? Artifact,
    string? Problem)
{
    public ProfileArtifactFingerprint? Fingerprint { get; init; }
}

public sealed record ProfileArtifactInventory(IReadOnlyList<ProfileArtifactInspection> Items)
{
    public IReadOnlyList<ProfileArtifactV1> ValidProfiles => Items
        .Where(item => item.State == ProfileArtifactReadState.Valid)
        .Select(item => item.Artifact!)
        .ToArray();
}

public sealed record ProfileArtifactWriteResult(
    bool Succeeded,
    string? Path,
    ProfileArtifactV1? Artifact,
    string? Problem)
{
    public ProfileArtifactFingerprint? Fingerprint { get; init; }

    internal static ProfileArtifactWriteResult Success(
        string path,
        ProfileArtifactV1 artifact,
        ProfileArtifactFingerprint fingerprint) =>
        new(true, path, artifact, Problem: null)
        {
            Fingerprint = fingerprint
        };

    internal static ProfileArtifactWriteResult Failure(string problem) =>
        new(false, Path: null, Artifact: null, problem);
}
