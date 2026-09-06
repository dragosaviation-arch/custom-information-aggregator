using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.SourceInterpretation;

public sealed class XmlElementValueSourceAdapter(SourceStructureDeclaration declaration)
    : ISourceAdapter
{
    public SourceStructureDeclaration Declaration { get; } =
        declaration ?? throw new ArgumentNullException(nameof(declaration));

    public async ValueTask<InterpretedSourceDocument> InterpretAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        CancellationToken cancellationToken = default)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "A source adapter requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentNullException.ThrowIfNull(reader);

        if (reader.NodeType != XmlNodeType.Element
            || XName.Get(reader.LocalName, reader.NamespaceURI) != Declaration.RootElementName)
        {
            throw new SourceAdapterValidationException(
                "unexpected-document-root",
                "The XML document root does not match the declared source structure.");
        }

        var values = new List<InterpretedSourceValue>();
        var containingElements = new Stack<string>();

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (reader.NodeType)
            {
                case XmlNodeType.Element when !reader.IsEmptyElement:
                    containingElements.Push(reader.Name);
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (containingElements.TryPeek(out var informationType)
                        && !string.IsNullOrWhiteSpace(reader.Value))
                    {
                        values.Add(new InterpretedSourceValue(informationType, reader.Value));
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (containingElements.Count == 0)
                    {
                        throw new SourceAdapterValidationException(
                            "invalid-element-boundary",
                            "The XML element boundary is not valid for the declared structure.");
                    }

                    containingElements.Pop();
                    break;
            }
        }
        while (await reader.ReadAsync().ConfigureAwait(false));

        if (containingElements.Count != 0)
        {
            throw new SourceAdapterValidationException(
                "invalid-element-boundary",
                "The XML element boundary is not valid for the declared structure.");
        }

        return new InterpretedSourceDocument(
            originatingSourceId,
            Declaration.StructureId,
            values);
    }
}

public static class ReleaseSupportedSourceStructures
{
    public const string CmlStructureId = "cia.cml.v1";

    public static SourceStructureDeclaration Cml { get; } = new(
        CmlStructureId,
        XName.Get("cml", string.Empty));
}
