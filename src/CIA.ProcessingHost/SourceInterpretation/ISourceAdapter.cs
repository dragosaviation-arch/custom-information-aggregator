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
