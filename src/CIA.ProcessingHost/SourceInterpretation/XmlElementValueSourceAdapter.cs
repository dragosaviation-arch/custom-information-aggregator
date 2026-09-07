using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.SourceInterpretation;

public sealed class XmlElementValueSourceAdapter(SourceStructureDeclaration declaration)
    : ISourceAdapter, ISourceOccurrenceAdapter
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

        var values = await XmlElementValueReader
            .ReadValuesAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        return new InterpretedSourceDocument(
            originatingSourceId,
            Declaration.StructureId,
            values);
    }

    public async ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        int localOrdinal,
        CancellationToken cancellationToken = default)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "A source adapter requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        if (localOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localOrdinal),
                "A source occurrence ordinal must be positive.");
        }

        ValidateDocumentRoot(reader);

        return await XmlElementValueReader
            .ReadOccurrenceAsync(reader, informationType, localOrdinal, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ValidateDocumentRoot(XmlReader reader)
    {
        if (reader.NodeType != XmlNodeType.Element
            || XName.Get(reader.LocalName, reader.NamespaceURI) != Declaration.RootElementName)
        {
            throw new SourceAdapterValidationException(
                "unexpected-document-root",
                "The XML document root does not match the declared source structure.");
        }
    }
}

public sealed class GenericXmlElementValueSourceAdapter : IGenericXmlSourceAdapter
{
    public const string GenericStructureId = "cia.xml.generic.v1";

    public string StructureId => GenericStructureId;

    public async ValueTask<InterpretedSourceDocument> InterpretAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(originatingSourceId, reader);

        var values = await XmlElementValueReader
            .ReadValuesAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        return new InterpretedSourceDocument(originatingSourceId, StructureId, values);
    }

    public ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        int localOrdinal,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(originatingSourceId, reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        if (localOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localOrdinal),
                "A source occurrence ordinal must be positive.");
        }

        return XmlElementValueReader.ReadOccurrenceAsync(
            reader,
            informationType,
            localOrdinal,
            cancellationToken);
    }

    private static void ValidateArguments(SourceId originatingSourceId, XmlReader reader)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "A source adapter requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentNullException.ThrowIfNull(reader);

        if (reader.NodeType != XmlNodeType.Element)
        {
            throw new SourceAdapterValidationException(
                "missing-document-element",
                "The XML source does not contain a document element.");
        }
    }
}

internal static class XmlElementValueReader
{
    public static async ValueTask<IReadOnlyList<InterpretedSourceValue>> ReadValuesAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        var values = new List<InterpretedSourceValue>();

        await ReadAsync(
            reader,
            (informationType, value) => values.Add(
                new InterpretedSourceValue(informationType, value)),
            cancellationToken).ConfigureAwait(false);

        return values;
    }

    public static async ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        XmlReader reader,
        string informationType,
        int localOrdinal,
        CancellationToken cancellationToken)
    {
        var occurrenceCount = 0;
        string? requestedValue = null;

        await ReadAsync(
            reader,
            (currentInformationType, value) =>
            {
                if (!string.Equals(
                        currentInformationType,
                        informationType,
                        StringComparison.Ordinal))
                {
                    return;
                }

                occurrenceCount++;
                if (occurrenceCount == localOrdinal)
                {
                    requestedValue = value;
                }
            },
            cancellationToken).ConfigureAwait(false);

        return new SourceOccurrenceRead(requestedValue, occurrenceCount);
    }

    private static async ValueTask ReadAsync(
        XmlReader reader,
        Action<string, string> onValue,
        CancellationToken cancellationToken)
    {
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
                        onValue(informationType, reader.Value);
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (containingElements.Count == 0)
                    {
                        throw new SourceAdapterValidationException(
                            "invalid-element-boundary",
                            "The XML element boundary is not valid.");
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
                "The XML element boundary is not valid.");
        }
    }
}
