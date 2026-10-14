using CIA.Contracts.Sources;

namespace CIA.Core.Sources;

public sealed class InterpretedSourceDocument
{
    public InterpretedSourceDocument(
        SourceId originatingSourceId,
        string structureId,
        IEnumerable<InterpretedSourceValue> values,
        ArchiveSourceProvenance? archiveProvenance = null,
        IEnumerable<SourceStructuralCardinality>? structuralCardinality = null,
        IEnumerable<InterpretedSourceElement>? elements = null)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "An interpreted source requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(structureId);
        ArgumentNullException.ThrowIfNull(values);

        var valueArray = values.ToArray();
        var cardinalityArray = structuralCardinality?.ToArray() ?? [];
        var elementArray = elements?.ToArray() ?? [];

        if (valueArray.Any(value => value is null))
        {
            throw new ArgumentException(
                "Interpreted source values cannot contain null items.",
                nameof(values));
        }

        if (cardinalityArray.Any(statistic => statistic is null))
        {
            throw new ArgumentException(
                "Source structural cardinality cannot contain null items.",
                nameof(structuralCardinality));
        }

        if (elementArray.Any(element => element is null))
        {
            throw new ArgumentException(
                "Interpreted source elements cannot contain null items.",
                nameof(elements));
        }

        OriginatingSourceId = originatingSourceId;
        StructureId = structureId;
        Values = Array.AsReadOnly(valueArray);
        ArchiveProvenance = archiveProvenance;
        StructuralCardinality = Array.AsReadOnly(cardinalityArray);
        Elements = Array.AsReadOnly(elementArray);
    }

    public SourceId OriginatingSourceId { get; }

    public string StructureId { get; }

    public IReadOnlyList<InterpretedSourceValue> Values { get; }

    public ArchiveSourceProvenance? ArchiveProvenance { get; }

    public IReadOnlyList<SourceStructuralCardinality> StructuralCardinality { get; }

    public IReadOnlyList<InterpretedSourceElement> Elements { get; }
}

public sealed record InterpretedSourceElement
{
    public InterpretedSourceElement(
        string informationType,
        string structuralPath,
        int occurrenceCount,
        SourceElementRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        if (occurrenceCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceCount));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }

        InformationType = informationType;
        StructuralPath = structuralPath;
        OccurrenceCount = occurrenceCount;
        Role = role;
    }

    public string InformationType { get; }

    public string StructuralPath { get; }

    public int OccurrenceCount { get; }

    public SourceElementRole Role { get; }
}

public sealed class InterpretedSourceValue
{
    public InterpretedSourceValue(string informationType, string content)
        : this(informationType, content, lineage: null)
    {
    }

    public InterpretedSourceValue(
        string informationType,
        string content,
        SourceValueLineage? lineage)
        : this(
            informationType,
            content,
            lineage,
            SourceValueCandidateKind.Element,
            lineage?.StructuralPath ?? $"/{informationType}")
    {
    }

    public InterpretedSourceValue(
        string informationType,
        string content,
        SourceValueLineage? lineage,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralIdentity);

        if (!Enum.IsDefined(candidateKind))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateKind), candidateKind, null);
        }

        InformationType = informationType;
        Content = content;
        Lineage = lineage;
        CandidateKind = candidateKind;
        StructuralIdentity = structuralIdentity;
    }

    public string InformationType { get; }

    public string Content { get; }

    public SourceValueLineage? Lineage { get; }

    public SourceValueCandidateKind CandidateKind { get; }

    public string StructuralIdentity { get; }
}
