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

public sealed record ProfileArtifactInspection(
    string Path,
    ProfileArtifactReadState State,
    ProfileArtifactV1? Artifact,
    string? Problem);

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
    internal static ProfileArtifactWriteResult Success(
        string path,
        ProfileArtifactV1 artifact) =>
        new(true, path, artifact, Problem: null);

    internal static ProfileArtifactWriteResult Failure(string problem) =>
        new(false, Path: null, Artifact: null, problem);
}
