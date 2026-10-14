using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;
using CIA.Core.Sources;

namespace CIA.ProcessingHost.Discovery;

public sealed class StructuralCardinalityAccumulator
{
    private readonly Dictionary<SourceSetId, SourceSetStatistics> _sourceSets = [];

    internal int RetainedRootPathCount => _sourceSets.Values.Sum(value => value.RootPaths.Count);

    internal int RetainedCardinalityStatisticCount =>
        _sourceSets.Values.Sum(value => value.Statistics.Count);

    public void Add(SourceSetId sourceSetId, InterpretedSourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourceStatistics = CollectSource(source);
        if (sourceStatistics.RootPaths.Count == 0)
        {
            return;
        }

        if (!_sourceSets.TryGetValue(sourceSetId, out var sourceSet))
        {
            sourceSet = new SourceSetStatistics();
            _sourceSets.Add(sourceSetId, sourceSet);
        }

        sourceSet.RootPaths.UnionWith(sourceStatistics.RootPaths);
        foreach (var statistic in sourceStatistics.Statistics)
        {
            if (!sourceSet.Statistics.TryGetValue(statistic.Key, out var aggregate))
            {
                aggregate = new AggregateStatistic(statistic.Key);
                sourceSet.Statistics.Add(statistic.Key, aggregate);
            }

            aggregate.Add(statistic.Value);
        }
    }

    public void EnsureRoot(SourceSetId sourceSetId, string structuralPath)
    {
        var rootPath = GetRootPath(structuralPath);
        if (!_sourceSets.TryGetValue(sourceSetId, out var sourceSet))
        {
            sourceSet = new SourceSetStatistics();
            _sourceSets.Add(sourceSetId, sourceSet);
        }

        sourceSet.RootPaths.Add(rootPath);
    }

    public IReadOnlyList<string> GetRootPaths(SourceSetId sourceSetId)
    {
        return _sourceSets.TryGetValue(sourceSetId, out var sourceSet)
            ? sourceSet.RootPaths.Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public IReadOnlyList<StructuralCardinalityStatistic> GetStatistics(SourceSetId sourceSetId)
    {
        if (!_sourceSets.TryGetValue(sourceSetId, out var sourceSet))
        {
            return [];
        }

        return sourceSet.Statistics.Values
            .OrderBy(statistic => statistic.Key.ChildPath, StringComparer.Ordinal)
            .Select(statistic => statistic.Create(sourceSetId))
            .ToArray();
    }

    private static SourceStatistics CollectSource(InterpretedSourceDocument source)
    {
        if (source.StructuralCardinality.Count > 0)
        {
            return new SourceStatistics(
                source.StructuralCardinality
                    .Select(statistic => statistic.DocumentRootPath)
                    .ToHashSet(StringComparer.Ordinal),
                source.StructuralCardinality.ToDictionary(
                    statistic => new EdgeKey(
                        statistic.DocumentRootPath,
                        statistic.ParentStructuralPath,
                        statistic.ChildStructuralPath),
                    statistic => new SourceStatistic(
                        statistic.MaximumChildInstancesPerParent,
                        statistic.ObservedParentInstanceCount,
                        statistic.RepeatingParentInstanceCount)));
        }

        var roots = new HashSet<string>(StringComparer.Ordinal);
        var instancesByPath = new Dictionary<string, PathInstanceCounter>(StringComparer.Ordinal);
        var childrenByEdge = new Dictionary<EdgeKey, EdgeInstanceCounter>();

        foreach (var value in source.Values)
        {
            var lineage = value.Lineage;
            if (lineage is null)
            {
                continue;
            }

            string? rootPath = null;
            string? parentPath = null;
            long? parentInstanceId = null;
            foreach (var element in lineage.ElementPath)
            {
                var childPath = parentPath is null
                    ? $"/{element.ExpandedName}"
                    : $"{parentPath}/{element.ExpandedName}";

                rootPath ??= childPath;
                if (!instancesByPath.TryGetValue(childPath, out var pathCounter))
                {
                    pathCounter = new PathInstanceCounter();
                    instancesByPath.Add(childPath, pathCounter);
                }

                pathCounter.Observe(element.InstanceId);
                if (parentPath is not null && parentInstanceId is { } parentId)
                {
                    var edge = new EdgeKey(rootPath, parentPath, childPath);
                    if (!childrenByEdge.TryGetValue(edge, out var edgeCounter))
                    {
                        edgeCounter = new EdgeInstanceCounter();
                        childrenByEdge.Add(edge, edgeCounter);
                    }

                    edgeCounter.Observe(parentId, element.InstanceId);
                }

                parentPath = childPath;
                parentInstanceId = element.InstanceId;
            }

            if (rootPath is not null)
            {
                roots.Add(rootPath);
            }
        }

        var statistics = new Dictionary<EdgeKey, SourceStatistic>();
        foreach (var (edge, edgeCounter) in childrenByEdge)
        {
            edgeCounter.Complete();
            statistics.Add(edge, new SourceStatistic(
                edgeCounter.MaximumChildrenPerParent,
                instancesByPath[edge.ParentPath].Count,
                edgeCounter.RepeatingParentCount));
        }

        return new SourceStatistics(roots, statistics);
    }

    private static string GetRootPath(string structuralPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);
        if (structuralPath[0] != '/')
        {
            throw new ArgumentException(
                "A Discovery structural path must be absolute.",
                nameof(structuralPath));
        }

        var namespaceDepth = 0;
        for (var index = 1; index < structuralPath.Length; index++)
        {
            switch (structuralPath[index])
            {
                case '{':
                    namespaceDepth++;
                    break;
                case '}' when namespaceDepth > 0:
                    namespaceDepth--;
                    break;
                case '/' when namespaceDepth == 0:
                    return structuralPath[..index];
            }
        }

        return structuralPath;
    }

    private sealed class SourceSetStatistics
    {
        public HashSet<string> RootPaths { get; } = new(StringComparer.Ordinal);

        public Dictionary<EdgeKey, AggregateStatistic> Statistics { get; } = [];
    }

    private sealed record SourceStatistics(
        IReadOnlySet<string> RootPaths,
        IReadOnlyDictionary<EdgeKey, SourceStatistic> Statistics);

    private sealed record SourceStatistic(
        int MaximumChildrenPerParent,
        long ObservedParentCount,
        long RepeatingParentCount);

    private sealed class PathInstanceCounter
    {
        private long _latestInstanceId;

        public int Count { get; private set; }

        public void Observe(long instanceId)
        {
            // Element instance IDs are assigned in forward XmlReader order. A lineage may
            // revisit the current element for multiple values, but cannot introduce an
            // earlier unseen instance after a later one at the same structural path.
            if (instanceId <= _latestInstanceId)
            {
                return;
            }

            _latestInstanceId = instanceId;
            Count++;
        }
    }

    private sealed class EdgeInstanceCounter
    {
        private long _currentParentInstanceId;
        private long _latestChildInstanceId;
        private int _currentChildCount;

        public int MaximumChildrenPerParent { get; private set; }

        public long RepeatingParentCount { get; private set; }

        public void Observe(long parentInstanceId, long childInstanceId)
        {
            if (_currentParentInstanceId != parentInstanceId)
            {
                CompleteCurrentParent();
                _currentParentInstanceId = parentInstanceId;
                _latestChildInstanceId = 0;
            }

            if (childInstanceId <= _latestChildInstanceId)
            {
                return;
            }

            _latestChildInstanceId = childInstanceId;
            _currentChildCount++;
        }

        public void Complete()
        {
            CompleteCurrentParent();
        }

        private void CompleteCurrentParent()
        {
            if (_currentParentInstanceId == 0)
            {
                return;
            }

            MaximumChildrenPerParent = Math.Max(
                MaximumChildrenPerParent,
                _currentChildCount);
            if (_currentChildCount > 1)
            {
                RepeatingParentCount++;
            }

            _currentParentInstanceId = 0;
            _currentChildCount = 0;
        }
    }

    private sealed class AggregateStatistic(EdgeKey key)
    {
        public EdgeKey Key { get; } = key;

        public int MaximumChildrenPerParent { get; private set; }

        public long ObservedParentCount { get; private set; }

        public long RepeatingParentCount { get; private set; }

        public int ObservedSourceCount { get; private set; }

        public void Add(SourceStatistic statistic)
        {
            MaximumChildrenPerParent = Math.Max(
                MaximumChildrenPerParent,
                statistic.MaximumChildrenPerParent);
            ObservedParentCount += statistic.ObservedParentCount;
            RepeatingParentCount += statistic.RepeatingParentCount;
            ObservedSourceCount++;
        }

        public StructuralCardinalityStatistic Create(SourceSetId sourceSetId)
        {
            return new StructuralCardinalityStatistic(
                sourceSetId,
                Key.RootPath,
                Key.ParentPath,
                Key.ChildPath,
                MaximumChildrenPerParent,
                ObservedParentCount,
                RepeatingParentCount,
                ObservedSourceCount);
        }
    }

    private readonly record struct EdgeKey(
        string RootPath,
        string ParentPath,
        string ChildPath);
}
