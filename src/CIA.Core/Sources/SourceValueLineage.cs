using CIA.Contracts.Sources;

namespace CIA.Core.Sources;

public sealed class SourceElementInstance
{
    public SourceElementInstance(
        string localName,
        string namespaceUri,
        string qualifiedName,
        long instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        ArgumentNullException.ThrowIfNull(namespaceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);

        if (instanceId < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instanceId),
                "A source element instance ID must be positive.");
        }

        LocalName = localName;
        NamespaceUri = namespaceUri;
        QualifiedName = qualifiedName;
        InstanceId = instanceId;
    }

    public string LocalName { get; }

    public string NamespaceUri { get; }

    public string QualifiedName { get; }

    public long InstanceId { get; }

    public string ExpandedName => string.IsNullOrEmpty(NamespaceUri)
        ? LocalName
        : $"{{{NamespaceUri}}}{LocalName}";
}

public sealed class SourceValueLineage
{
    public SourceValueLineage(
        SourceId originatingSourceId,
        IEnumerable<SourceElementInstance> elementPath,
        long traversalOrder)
    {
        if (originatingSourceId == default)
        {
            throw new ArgumentException(
                "Source value lineage requires an originating Source ID.",
                nameof(originatingSourceId));
        }

        ArgumentNullException.ThrowIfNull(elementPath);

        var path = elementPath.ToArray();
        if (path.Length == 0)
        {
            throw new ArgumentException(
                "Source value lineage requires an element path.",
                nameof(elementPath));
        }

        if (path.Any(element => element is null))
        {
            throw new ArgumentException(
                "A source value element path cannot contain null items.",
                nameof(elementPath));
        }

        if (path.Select(element => element.InstanceId).Distinct().Count() != path.Length)
        {
            throw new ArgumentException(
                "A source value element path cannot repeat an element instance.",
                nameof(elementPath));
        }

        if (traversalOrder < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traversalOrder),
                "A source value traversal order must be positive.");
        }

        OriginatingSourceId = originatingSourceId;
        ElementPath = Array.AsReadOnly(path);
        AncestorInstanceIds = Array.AsReadOnly(
            path.Take(path.Length - 1).Select(element => element.InstanceId).ToArray());
        StructuralPath = "/" + string.Join(
            "/",
            path.Select(element => element.ExpandedName));
        TraversalOrder = traversalOrder;
    }

    public SourceId OriginatingSourceId { get; }

    public IReadOnlyList<SourceElementInstance> ElementPath { get; }

    public string StructuralPath { get; }

    public long NodeInstanceId => ElementPath[^1].InstanceId;

    public long? ParentInstanceId => ElementPath.Count > 1
        ? ElementPath[^2].InstanceId
        : null;

    public IReadOnlyList<long> AncestorInstanceIds { get; }

    public long TraversalOrder { get; }
}
