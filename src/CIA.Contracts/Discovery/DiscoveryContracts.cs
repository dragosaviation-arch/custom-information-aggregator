using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Discovery;

public sealed record DiscoveryInformationIdentity
{
    private static readonly SourceSetId LegacySourceSetId = SourceSetId.From(
        Guid.Parse("00000000-0000-7000-8000-000000000136"));

    public DiscoveryInformationIdentity(
        SourceSetId sourceSetId,
        string structuralPath,
        string informationType)
        : this(
            sourceSetId,
            structuralPath,
            informationType,
            SourceValueCandidateKind.Element,
            structuralPath)
    {
    }

    [JsonConstructor]
    public DiscoveryInformationIdentity(
        SourceSetId sourceSetId,
        string structuralPath,
        string informationType,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A Discovery information identity requires a Source Set ID.",
                nameof(sourceSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralIdentity);

        if (!Enum.IsDefined(candidateKind))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateKind), candidateKind, null);
        }

        SourceSetId = sourceSetId;
        StructuralPath = structuralPath;
        InformationType = informationType;
        CandidateKind = candidateKind;
        StructuralIdentity = structuralIdentity;
    }

    public SourceSetId SourceSetId { get; }

    public string StructuralPath { get; }

    public string InformationType { get; }

    public SourceValueCandidateKind CandidateKind { get; }

    public string StructuralIdentity { get; }

    internal static DiscoveryInformationIdentity CreateLegacy(string informationType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        return new DiscoveryInformationIdentity(
            LegacySourceSetId,
            $"/{informationType}",
            informationType);
    }
}

public sealed record DiscoveredSourceContribution(
    SourceId SourceId,
    string SourceName,
    int OccurrenceCount);

[method: JsonConstructor]
public sealed record DiscoveredInformation(
    DiscoveryInformationIdentity Identity,
    int TotalOccurrenceCount,
    IReadOnlyList<DiscoveredSourceContribution> ContributingSources,
    string SampleValue)
{
    public DiscoveredInformation(
        string informationType,
        int totalOccurrenceCount,
        IReadOnlyList<DiscoveredSourceContribution> contributingSources,
        string sampleValue)
        : this(
            DiscoveryInformationIdentity.CreateLegacy(informationType),
            totalOccurrenceCount,
            contributingSources,
            sampleValue)
    {
    }

    [JsonIgnore]
    public SourceSetId SourceSetId => Identity.SourceSetId;

    [JsonIgnore]
    public string StructuralPath => Identity.StructuralPath;

    [JsonIgnore]
    public string InformationType => Identity.InformationType;
}

[method: JsonConstructor]
public sealed record DiscoveredOccurrence(
    DiscoveryInformationIdentity Identity,
    int Ordinal,
    int TotalOccurrenceCount,
    SourceId SourceId,
    string Value)
{
    public DiscoveredOccurrence(
        string informationType,
        int ordinal,
        int totalOccurrenceCount,
        SourceId sourceId,
        string value)
        : this(
            DiscoveryInformationIdentity.CreateLegacy(informationType),
            ordinal,
            totalOccurrenceCount,
            sourceId,
            value)
    {
    }

    [JsonIgnore]
    public string InformationType => Identity.InformationType;
}

[method: JsonConstructor]
public sealed record DiscoveryOccurrenceLookup(
    OperationId DiscoveryOperationId,
    DiscoveryInformationIdentity Identity,
    int GlobalOrdinal,
    int TotalOccurrenceCount,
    LoadedSourceContract Source,
    int LocalOrdinal,
    int ExpectedSourceOccurrenceCount)
{
    public DiscoveryOccurrenceLookup(
        OperationId discoveryOperationId,
        string informationType,
        int globalOrdinal,
        int totalOccurrenceCount,
        LoadedSourceContract source,
        int localOrdinal,
        int expectedSourceOccurrenceCount)
        : this(
            discoveryOperationId,
            new DiscoveryInformationIdentity(
                source.SourceSetId,
                $"/{informationType}",
                informationType),
            globalOrdinal,
            totalOccurrenceCount,
            source,
            localOrdinal,
            expectedSourceOccurrenceCount)
    {
    }

    [JsonIgnore]
    public string InformationType => Identity.InformationType;

    [JsonIgnore]
    public string StructuralPath => Identity.StructuralPath;
}

public sealed record DiscoverySourceIssue(
    SourceId SourceId,
    string Code,
    string Description);
