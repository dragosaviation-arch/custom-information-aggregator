using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Discovery;

public static class DiscoveryContributorPaging
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;
}

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

public readonly record struct DiscoveryLogicalIdentity(
    SourceSetId SourceSetId,
    SourceValueCandidateKind CandidateKind,
    string CanonicalFieldIdentity)
{
    public static DiscoveryLogicalIdentity Create(DiscoveryInformationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var canonicalFieldIdentity = identity.CandidateKind switch
        {
            SourceValueCandidateKind.Element => GetFinalStructuralSegment(
                identity.StructuralPath),
            SourceValueCandidateKind.Attribute => GetFinalStructuralSegment(
                identity.StructuralIdentity),
            _ => identity.StructuralIdentity
        };
        return new DiscoveryLogicalIdentity(
            identity.SourceSetId,
            identity.CandidateKind,
            canonicalFieldIdentity);
    }

    private static string GetFinalStructuralSegment(string path)
    {
        var namespaceDepth = 0;
        var finalSeparator = -1;
        for (var index = 0; index < path.Length; index++)
        {
            switch (path[index])
            {
                case '{':
                    namespaceDepth++;
                    break;
                case '}' when namespaceDepth > 0:
                    namespaceDepth--;
                    break;
                case '/' when namespaceDepth == 0:
                    finalSeparator = index;
                    break;
            }
        }

        return finalSeparator < 0 ? path : path[(finalSeparator + 1)..];
    }
}

[method: JsonConstructor]
public sealed record DiscoveredInformation(
    DiscoveryInformationIdentity Identity,
    int TotalOccurrenceCount,
    int LogicalSourceCount,
    string SampleValue)
{
    public DiscoveredInformation(
        string informationType,
        int totalOccurrenceCount,
        IReadOnlyList<DiscoveredSourceContribution> contributingSources,
        string sampleValue)
        : this(
            informationType,
            totalOccurrenceCount,
            CountDistinctSources(contributingSources),
            sampleValue)
    {
    }

    public DiscoveredInformation(
        DiscoveryInformationIdentity identity,
        int totalOccurrenceCount,
        IReadOnlyList<DiscoveredSourceContribution> contributingSources,
        string sampleValue)
        : this(
            identity,
            totalOccurrenceCount,
            CountDistinctSources(contributingSources),
            sampleValue)
    {
    }

    public DiscoveredInformation(
        string informationType,
        int totalOccurrenceCount,
        int logicalSourceCount,
        string sampleValue)
        : this(
            DiscoveryInformationIdentity.CreateLegacy(informationType),
            totalOccurrenceCount,
            logicalSourceCount,
            sampleValue)
    {
    }

    private static int CountDistinctSources(
        IReadOnlyList<DiscoveredSourceContribution> contributingSources)
    {
        ArgumentNullException.ThrowIfNull(contributingSources);
        return contributingSources.Select(source => source.SourceId).Distinct().Count();
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
    string SourceName,
    string Value)
{
    public DiscoveredOccurrence(
        DiscoveryInformationIdentity identity,
        int ordinal,
        int totalOccurrenceCount,
        SourceId sourceId,
        string value)
        : this(identity, ordinal, totalOccurrenceCount, sourceId, sourceId.ToString(), value)
    {
    }

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
            sourceId.ToString(),
            value)
    {
    }

    [JsonIgnore]
    public string InformationType => Identity.InformationType;
}

[method: JsonConstructor]
public sealed record DiscoveryOccurrenceLookup(
    OperationId DiscoveryOperationId,
    IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities,
    int GlobalOrdinal,
    int TotalOccurrenceCount)
{
    public DiscoveryOccurrenceLookup(
        OperationId DiscoveryOperationId,
        DiscoveryInformationIdentity Identity,
        int GlobalOrdinal,
        int TotalOccurrenceCount,
        LoadedSourceContract Source,
        int LocalOrdinal,
        int ExpectedSourceOccurrenceCount)
        : this(DiscoveryOperationId, [Identity], GlobalOrdinal, TotalOccurrenceCount)
    {
        ArgumentNullException.ThrowIfNull(Source);
    }

    public DiscoveryOccurrenceLookup(
        OperationId DiscoveryOperationId,
        string InformationType,
        int GlobalOrdinal,
        int TotalOccurrenceCount,
        LoadedSourceContract Source,
        int LocalOrdinal,
        int ExpectedSourceOccurrenceCount)
        : this(
            DiscoveryOperationId,
            [new DiscoveryInformationIdentity(
                Source.SourceSetId,
                $"/{InformationType}",
                InformationType)],
            GlobalOrdinal,
            TotalOccurrenceCount)
    {
    }

    [JsonIgnore]
    public DiscoveryInformationIdentity Identity => DetailedIdentities[0];

    [JsonIgnore]
    public string InformationType => DetailedIdentities[0].InformationType;

    [JsonIgnore]
    public DiscoveryLogicalIdentity LogicalIdentity =>
        DiscoveryLogicalIdentity.Create(DetailedIdentities[0]);
}

public sealed record DiscoveryContributorPageQuery(
    OperationId DiscoveryOperationId,
    IReadOnlyList<DiscoveryInformationIdentity> DetailedIdentities,
    int StartIndex,
    int PageSize,
    int ExpectedSourceCount);

public sealed record DiscoveryContributorPage(
    OperationId DiscoveryOperationId,
    int StartIndex,
    int TotalSourceCount,
    int TotalOccurrenceCount,
    IReadOnlyList<DiscoveredSourceContribution> Sources);

public sealed record DiscoverySourceIssue(
    SourceId SourceId,
    string Code,
    string Description);
