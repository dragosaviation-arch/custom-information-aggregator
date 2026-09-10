using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.SourceInterpretation;

public interface ISourceAdapter
{
    SourceStructureDeclaration Declaration { get; }

    ValueTask<InterpretedSourceDocument> InterpretAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        CancellationToken cancellationToken = default);
}

public interface IGenericXmlSourceAdapter
{
    string StructureId { get; }

    ValueTask<InterpretedSourceDocument> InterpretAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        CancellationToken cancellationToken = default);

    ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        string structuralPath,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int localOrdinal,
        CancellationToken cancellationToken = default);
}

public interface ISourceOccurrenceAdapter
{
    ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        string structuralPath,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int localOrdinal,
        CancellationToken cancellationToken = default);
}

public interface ISourceValueBatchAdapter
{
    ValueTask<int> ReadSelectedValuesAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken = default);
}

public sealed record SourceOccurrenceRead(
    string? Value,
    int OccurrenceCount);

public sealed class SourceStructureDeclaration
{
    public SourceStructureDeclaration(string structureId, XName rootElementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structureId);
        ArgumentNullException.ThrowIfNull(rootElementName);

        StructureId = structureId;
        RootElementName = rootElementName;
    }

    public string StructureId { get; }

    public XName RootElementName { get; }
}

public sealed class SourceAdapterValidationException : Exception
{
    public SourceAdapterValidationException(string failureCode, string description)
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}
