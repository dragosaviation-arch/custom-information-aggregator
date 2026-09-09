using CIA.Contracts.Sources;

namespace CIA.Core.Sources;

public sealed class InterpretedSourceDocument
{
    public InterpretedSourceDocument(
        SourceId originatingSourceId,
        string structureId,
        IEnumerable<InterpretedSourceValue> values,
        ArchiveSourceProvenance? archiveProvenance = null)
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

        if (valueArray.Any(value => value is null))
        {
            throw new ArgumentException(
                "Interpreted source values cannot contain null items.",
                nameof(values));
        }

        OriginatingSourceId = originatingSourceId;
        StructureId = structureId;
        Values = Array.AsReadOnly(valueArray);
        ArchiveProvenance = archiveProvenance;
    }

    public SourceId OriginatingSourceId { get; }

    public string StructureId { get; }

    public IReadOnlyList<InterpretedSourceValue> Values { get; }

    public ArchiveSourceProvenance? ArchiveProvenance { get; }
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentNullException.ThrowIfNull(content);

        InformationType = informationType;
        Content = content;
        Lineage = lineage;
    }

    public string InformationType { get; }

    public string Content { get; }

    public SourceValueLineage? Lineage { get; }
}
