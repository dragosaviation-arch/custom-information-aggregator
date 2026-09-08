using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.SourceInterpretation;

public sealed class XmlElementValueSourceAdapter(SourceStructureDeclaration declaration)
    : ISourceAdapter, ISourceOccurrenceAdapter, ISourceValueBatchAdapter
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

    public ValueTask<int> ReadSelectedValuesAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken = default)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "A source adapter requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(selectedInformationTypes);
        ArgumentNullException.ThrowIfNull(onBatch);
        ValidateDocumentRoot(reader);

        return XmlElementValueReader.ReadSelectedValuesAsync(
            reader,
            selectedInformationTypes,
            onBatch,
            cancellationToken);
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

public sealed class GenericXmlElementValueSourceAdapter
    : IGenericXmlSourceAdapter, ISourceValueBatchAdapter
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

    public ValueTask<int> ReadSelectedValuesAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(originatingSourceId, reader);
        ArgumentNullException.ThrowIfNull(selectedInformationTypes);
        ArgumentNullException.ThrowIfNull(onBatch);

        return XmlElementValueReader.ReadSelectedValuesAsync(
            reader,
            selectedInformationTypes,
            onBatch,
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
            (informationType, value) =>
            {
                values.Add(new InterpretedSourceValue(informationType, value));
                return ValueTask.CompletedTask;
            },
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
                    return ValueTask.CompletedTask;
                }

                occurrenceCount++;
                if (occurrenceCount == localOrdinal)
                {
                    requestedValue = value;
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return new SourceOccurrenceRead(requestedValue, occurrenceCount);
    }

    public static async ValueTask<int> ReadSelectedValuesAsync(
        XmlReader reader,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken)
    {
        const int batchSize = 256;
        var batch = new List<InterpretedSourceValue>(batchSize);
        var valueCount = 0;

        await ReadAsync(
            reader,
            async (informationType, value) =>
            {
                if (!selectedInformationTypes.Contains(informationType))
                {
                    return;
                }

                batch.Add(new InterpretedSourceValue(informationType, value));
                valueCount++;
                if (batch.Count == batchSize)
                {
                    await onBatch(batch.ToArray()).ConfigureAwait(false);
                    batch.Clear();
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (batch.Count > 0)
        {
            await onBatch(batch.ToArray()).ConfigureAwait(false);
        }

        return valueCount;
    }

    private static async ValueTask ReadAsync(
        XmlReader reader,
        Func<string, string, ValueTask> onValue,
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
                        await onValue(informationType, reader.Value).ConfigureAwait(false);
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
