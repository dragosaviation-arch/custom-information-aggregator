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
            .ReadValuesAsync(originatingSourceId, reader, cancellationToken)
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
        string structuralPath,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralIdentity);

        if (!Enum.IsDefined(candidateKind))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateKind), candidateKind, null);
        }

        if (localOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localOrdinal),
                "A source occurrence ordinal must be positive.");
        }

        ValidateDocumentRoot(reader);

        return await XmlElementValueReader
            .ReadOccurrenceAsync(
                originatingSourceId,
                reader,
                informationType,
                structuralPath,
                candidateKind,
                structuralIdentity,
                localOrdinal,
                cancellationToken)
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
            originatingSourceId,
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
            .ReadValuesAsync(originatingSourceId, reader, cancellationToken)
            .ConfigureAwait(false);

        return new InterpretedSourceDocument(originatingSourceId, StructureId, values);
    }

    public ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        string structuralPath,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int localOrdinal,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(originatingSourceId, reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralIdentity);

        if (!Enum.IsDefined(candidateKind))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateKind), candidateKind, null);
        }

        if (localOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localOrdinal),
                "A source occurrence ordinal must be positive.");
        }

        return XmlElementValueReader.ReadOccurrenceAsync(
            originatingSourceId,
            reader,
            informationType,
            structuralPath,
            candidateKind,
            structuralIdentity,
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
            originatingSourceId,
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
        SourceId originatingSourceId,
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        var values = new List<InterpretedSourceValue>();

        await ReadAsync(
            originatingSourceId,
            reader,
            value =>
            {
                values.Add(value);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return values
            .OrderBy(value => value.Lineage?.TraversalOrder ?? long.MaxValue)
            .ToArray();
    }

    public static async ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        string informationType,
        string structuralPath,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int localOrdinal,
        CancellationToken cancellationToken)
    {
        var occurrenceCount = 0;
        string? requestedValue = null;

        await ReadAsync(
            originatingSourceId,
            reader,
            value =>
            {
                if (!string.Equals(
                        value.InformationType,
                        informationType,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        value.Lineage?.StructuralPath ?? $"/{value.InformationType}",
                        structuralPath,
                        StringComparison.Ordinal)
                    || value.CandidateKind != candidateKind
                    || !string.Equals(
                        value.StructuralIdentity,
                        structuralIdentity,
                        StringComparison.Ordinal))
                {
                    return ValueTask.CompletedTask;
                }

                occurrenceCount++;
                if (occurrenceCount == localOrdinal)
                {
                    requestedValue = value.Content;
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return new SourceOccurrenceRead(requestedValue, occurrenceCount);
    }

    public static async ValueTask<int> ReadSelectedValuesAsync(
        SourceId originatingSourceId,
        XmlReader reader,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken)
    {
        const int batchSize = 256;
        var batch = new List<InterpretedSourceValue>(batchSize);
        var valueCount = 0;

        await ReadAsync(
            originatingSourceId,
            reader,
            async value =>
            {
                if (!selectedInformationTypes.Contains(value.InformationType))
                {
                    return;
                }

                batch.Add(value);
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
        SourceId originatingSourceId,
        XmlReader reader,
        Func<InterpretedSourceValue, ValueTask> onValue,
        CancellationToken cancellationToken)
    {
        var containingElements = new Stack<ElementFrame>();
        var structuralSlots = new StructuralSlotDetector(onValue);
        long elementInstanceId = 0;
        long valueTraversalOrder = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var siblingPosition = containingElements.TryPeek(out var parentFrame)
                        ? parentFrame.TakeNextChildPosition()
                        : 1;
                    var attributes = ReadAttributes(reader);
                    var element = new SourceElementInstance(
                        reader.LocalName,
                        reader.NamespaceURI,
                        reader.Name,
                        checked(++elementInstanceId),
                        siblingPosition,
                        attributes);

                    var elementPath = containingElements
                        .Reverse()
                        .Select(frame => frame.Element)
                        .Append(element)
                        .ToArray();

                    foreach (var attribute in attributes)
                    {
                        var lineage = new SourceValueLineage(
                            originatingSourceId,
                            elementPath,
                            checked(++valueTraversalOrder));
                        await onValue(
                                new InterpretedSourceValue(
                                    attribute.QualifiedName,
                                    attribute.Value,
                                    lineage,
                                    SourceValueCandidateKind.Attribute,
                                    $"{lineage.StructuralPath}/@{attribute.ExpandedName}"))
                            .ConfigureAwait(false);
                    }

                    if (!reader.IsEmptyElement)
                    {
                        containingElements.Push(new ElementFrame(element));
                    }

                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    if (containingElements.TryPeek(out var containingElement)
                        && !string.IsNullOrWhiteSpace(reader.Value))
                    {
                        var lineage = new SourceValueLineage(
                            originatingSourceId,
                            containingElements
                                .Reverse()
                                .Select(frame => frame.Element),
                            checked(++valueTraversalOrder));
                        containingElement.DirectTextValues.Add(
                            new InterpretedSourceValue(
                                containingElement.Element.QualifiedName,
                                reader.Value,
                                lineage));
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (containingElements.Count == 0)
                    {
                        throw new SourceAdapterValidationException(
                            "invalid-element-boundary",
                            "The XML element boundary is not valid.");
                    }

                    var completedElement = containingElements.Pop();
                    await EmitDirectValueChildrenAsync(
                            completedElement,
                            structuralSlots,
                            onValue)
                        .ConfigureAwait(false);

                    if (completedElement.DirectTextValues.Count > 0)
                    {
                        if (containingElements.TryPeek(out var parent))
                        {
                            if (containingElements.Count == 1)
                            {
                                foreach (var value in completedElement.DirectTextValues)
                                {
                                    await onValue(value).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                parent.DirectValueChildren.Add(
                                    new CompletedValueElement(
                                        completedElement.Element,
                                        completedElement.DirectTextValues.ToArray()));
                            }
                        }
                        else
                        {
                            foreach (var value in completedElement.DirectTextValues)
                            {
                                await onValue(value).ConfigureAwait(false);
                            }
                        }
                    }

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

        await structuralSlots.FlushAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<SourceXmlAttribute> ReadAttributes(XmlReader reader)
    {
        const string xmlnsNamespace = "http://www.w3.org/2000/xmlns/";

        if (!reader.HasAttributes)
        {
            return [];
        }

        var attributes = new List<SourceXmlAttribute>(reader.AttributeCount);
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (!string.Equals(reader.NamespaceURI, xmlnsNamespace, StringComparison.Ordinal))
                {
                    attributes.Add(
                        new SourceXmlAttribute(
                            reader.LocalName,
                            reader.NamespaceURI,
                            reader.Name,
                            reader.Value));
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return attributes;
    }

    private static async ValueTask EmitDirectValueChildrenAsync(
        ElementFrame frame,
        StructuralSlotDetector structuralSlots,
        Func<InterpretedSourceValue, ValueTask> onValue)
    {
        foreach (var group in frame.DirectValueChildren.GroupBy(
                     child => child.Element.ExpandedName,
                     StringComparer.Ordinal))
        {
            var children = group.ToArray();
            if (children.Length == 1)
            {
                foreach (var value in children[0].Values)
                {
                    await onValue(value).ConfigureAwait(false);
                }

                continue;
            }

            await structuralSlots.ProcessAsync(children).ConfigureAwait(false);
        }
    }

    private sealed class ElementFrame(SourceElementInstance element)
    {
        private int nextChildPosition;

        public SourceElementInstance Element { get; } = element;

        public List<InterpretedSourceValue> DirectTextValues { get; } = [];

        public List<CompletedValueElement> DirectValueChildren { get; } = [];

        public int TakeNextChildPosition() => checked(++nextChildPosition);
    }

    private sealed record CompletedValueElement(
        SourceElementInstance Element,
        IReadOnlyList<InterpretedSourceValue> Values);

    private sealed class StructuralSlotDetector(
        Func<InterpretedSourceValue, ValueTask> onValue)
    {
        private readonly Dictionary<string, StructuralSlotSelector> confirmedStructuralGroups =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<CompletedValueElement>> pendingGroups =
            new(StringComparer.Ordinal);

        public async ValueTask ProcessAsync(IReadOnlyList<CompletedValueElement> children)
        {
            var key = CreateGroupKey(children[0]);
            if (confirmedStructuralGroups.TryGetValue(key, out var selector))
            {
                await EmitStructuralValuesAsync(children, selector).ConfigureAwait(false);
                return;
            }

            if (pendingGroups.Remove(key, out var firstGroup))
            {
                selector = FindStableAttributeName(firstGroup, children) is { } attributeName
                    ? new StructuralSlotSelector(attributeName)
                    : StructuralSlotSelector.ByPosition;
                confirmedStructuralGroups.Add(key, selector);
                await EmitStructuralValuesAsync(firstGroup, selector).ConfigureAwait(false);
                await EmitStructuralValuesAsync(children, selector).ConfigureAwait(false);
                return;
            }

            pendingGroups.Add(key, children);
        }

        public async ValueTask FlushAsync()
        {
            foreach (var group in pendingGroups.Values)
            {
                foreach (var child in group)
                {
                    foreach (var value in child.Values)
                    {
                        await onValue(value).ConfigureAwait(false);
                    }
                }
            }

            pendingGroups.Clear();
        }

        private async ValueTask EmitStructuralValuesAsync(
            IReadOnlyList<CompletedValueElement> children,
            StructuralSlotSelector selector)
        {
            foreach (var child in children)
            {
                var rawValue = child.Values[0];
                var (displayName, structuralIdentity) = selector.AttributeName is null
                    ? CreatePositionIdentity(child, rawValue)
                    : CreateAttributeIdentity(child, rawValue, selector.AttributeName);

                foreach (var value in child.Values)
                {
                    await onValue(
                            new InterpretedSourceValue(
                                displayName,
                                value.Content,
                                value.Lineage,
                                SourceValueCandidateKind.Structural,
                                structuralIdentity))
                        .ConfigureAwait(false);
                }
            }
        }

        private static string CreateGroupKey(CompletedValueElement child)
        {
            var path = child.Values[0].Lineage?.StructuralPath
                ?? $"/{child.Element.ExpandedName}";
            var separator = path.LastIndexOf('/');
            var parentPath = separator > 0 ? path[..separator] : "/";
            return $"{parentPath}\u001f{child.Element.ExpandedName}";
        }

        private static string? FindStableAttributeName(
            IReadOnlyList<CompletedValueElement> firstGroup,
            IReadOnlyList<CompletedValueElement> secondGroup)
        {
            return firstGroup[0].Element.Attributes
                .Select(attribute => attribute.ExpandedName)
                .Where(attributeName => HasStableAttributeSlots(firstGroup, attributeName))
                .Where(attributeName => HasStableAttributeSlots(secondGroup, attributeName))
                .Where(attributeName => GetAttributeValues(firstGroup, attributeName)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(GetAttributeValues(secondGroup, attributeName)))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static bool HasStableAttributeSlots(
            IReadOnlyList<CompletedValueElement> children,
            string attributeName)
        {
            var values = GetAttributeValues(children, attributeName).ToArray();
            return values.Length == children.Count
                && values.Distinct(StringComparer.Ordinal).Count() == children.Count;
        }

        private static IEnumerable<string> GetAttributeValues(
            IReadOnlyList<CompletedValueElement> children,
            string attributeName)
        {
            return children.SelectMany(child => child.Element.Attributes
                .Where(attribute => string.Equals(
                    attribute.ExpandedName,
                    attributeName,
                    StringComparison.Ordinal))
                .Select(attribute => attribute.Value));
        }

        private static (string DisplayName, string StructuralIdentity) CreateAttributeIdentity(
            CompletedValueElement child,
            InterpretedSourceValue rawValue,
            string attributeName)
        {
            var attribute = child.Element.Attributes.SingleOrDefault(
                candidate => string.Equals(
                    candidate.ExpandedName,
                    attributeName,
                    StringComparison.Ordinal));
            if (attribute is null)
            {
                return CreatePositionIdentity(child, rawValue);
            }

            var escapedValue = attribute.Value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
            return (
                $"{child.Element.QualifiedName} [{attribute.QualifiedName}={attribute.Value}]",
                $"{rawValue.Lineage?.StructuralPath ?? $"/{child.Element.ExpandedName}"}[@{attributeName}='{escapedValue}']");
        }

        private static (string DisplayName, string StructuralIdentity) CreatePositionIdentity(
            CompletedValueElement child,
            InterpretedSourceValue rawValue)
        {
            return (
                $"{child.Element.QualifiedName} [position {child.Element.SiblingPosition}]",
                $"{rawValue.Lineage?.StructuralPath ?? $"/{child.Element.ExpandedName}"}[position={child.Element.SiblingPosition}]");
        }

        private sealed record StructuralSlotSelector(string? AttributeName)
        {
            public static StructuralSlotSelector ByPosition { get; } = new(AttributeName: null);
        }
    }
}
