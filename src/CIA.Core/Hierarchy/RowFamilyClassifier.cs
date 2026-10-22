using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Core.Hierarchy;

public sealed class RowFamilyClassifier
{
    public RowFamilyClassificationResult Classify(
        SourceSetId sourceSetId,
        IEnumerable<string> documentRootPaths,
        IEnumerable<StructuralCardinalityStatistic> cardinalityStatistics,
        IEnumerable<DiscoveryInformationIdentity> detailedIdentities)
    {
        if (sourceSetId == default)
        {
            throw new ArgumentException(
                "Row Family classification requires a Source Set ID.",
                nameof(sourceSetId));
        }

        ArgumentNullException.ThrowIfNull(documentRootPaths);
        ArgumentNullException.ThrowIfNull(cardinalityStatistics);
        ArgumentNullException.ThrowIfNull(detailedIdentities);

        var roots = documentRootPaths
            .Select(path => ParsedStructuralPath.Create(path, nameof(documentRootPaths)))
            .ToArray();
        if (roots.Length == 0 || roots.Select(root => root.Value).Distinct().Count() != roots.Length)
        {
            throw new ArgumentException(
                "Classification requires unique document root paths.",
                nameof(documentRootPaths));
        }

        if (roots.Any(root => roots.Any(other =>
                root != other && root.IsAncestorOf(other))))
        {
            throw new ArgumentException(
                "Document root paths cannot be nested.",
                nameof(documentRootPaths));
        }

        var statistics = cardinalityStatistics.ToArray();
        if (statistics.Any(statistic => statistic is null
                || statistic.SourceSetId != sourceSetId))
        {
            throw new ArgumentException(
                "Every structural statistic must belong to the classified Source Set.",
                nameof(cardinalityStatistics));
        }

        if (statistics.Select(statistic => statistic.ChildStructuralPath).Distinct().Count()
            != statistics.Length)
        {
            throw new ArgumentException(
                "Structural cardinality must contain one bounded aggregate per child path.",
                nameof(cardinalityStatistics));
        }

        var rootPaths = roots.ToDictionary(root => root.Value, StringComparer.Ordinal);
        foreach (var statistic in statistics)
        {
            if (!rootPaths.TryGetValue(statistic.DocumentRootPath, out var root)
                || !root.IsAncestorOfOrEqual(
                    ParsedStructuralPath.Create(
                        statistic.ChildStructuralPath,
                        nameof(cardinalityStatistics))))
            {
                throw new ArgumentException(
                    "Every structural statistic must reference one declared document root.",
                    nameof(cardinalityStatistics));
            }
        }

        var identities = detailedIdentities.ToArray();
        if (identities.Any(identity => identity is null || identity.SourceSetId != sourceSetId)
            || identities.Distinct().Count() != identities.Length)
        {
            throw new ArgumentException(
                "Detailed Discovery identities must be unique and belong to the classified Source Set.",
                nameof(detailedIdentities));
        }

        var repeatedPaths = statistics
            .Where(statistic => statistic.IsRepeated)
            .Select(statistic => ParsedStructuralPath.Create(
                statistic.ChildStructuralPath,
                nameof(cardinalityStatistics)))
            .ToArray();
        var structuralSlotPaths = identities
            .Where(identity => identity.CandidateKind == SourceValueCandidateKind.Structural)
            .Select(identity => FindStructuralSlotPath(identity, repeatedPaths))
            .OfType<ParsedStructuralPath>()
            .ToHashSet();
        var rowFamilyPaths = repeatedPaths
            .Where(path => !structuralSlotPaths.Contains(path))
            .ToArray();

        var definitions = new List<RowFamilyDefinition>(roots.Length + rowFamilyPaths.Length);
        definitions.AddRange(roots.Select(root => new RowFamilyDefinition(
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, root.Value),
            parentRowFamilyId: null)));

        foreach (var repeatedPath in rowFamilyPaths)
        {
            var root = FindApplicableRoot(repeatedPath, roots, nameof(cardinalityStatistics));
            var repeatedParent = rowFamilyPaths
                .Where(candidate => candidate != repeatedPath && candidate.IsAncestorOf(repeatedPath))
                .OrderByDescending(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            var parent = repeatedParent is null
                ? new RowFamilyId(sourceSetId, RowFamilyKind.Root, root.Value)
                : new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, repeatedParent.Value);
            definitions.Add(new RowFamilyDefinition(
                new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, repeatedPath.Value),
                parent));
        }

        var orderedDefinitions = definitions
            .OrderBy(definition => definition.Id)
            .ToArray();
        var assignments = identities
            .Select(identity => Assign(identity, roots, rowFamilyPaths))
            .OrderBy(assignment => assignment.RowFamilyId)
            .ThenBy(assignment => assignment.Identity.StructuralPath, StringComparer.Ordinal)
            .ThenBy(assignment => assignment.Identity.CandidateKind)
            .ThenBy(assignment => assignment.Identity.StructuralIdentity, StringComparer.Ordinal)
            .ThenBy(assignment => assignment.Identity.InformationType, StringComparer.Ordinal)
            .ToArray();

        return new RowFamilyClassificationResult(orderedDefinitions, assignments);

        RowFamilyFieldAssignment Assign(
            DiscoveryInformationIdentity identity,
            IReadOnlyList<ParsedStructuralPath> availableRoots,
            IReadOnlyList<ParsedStructuralPath> availableRepeatedPaths)
        {
            var fieldPath = ParsedStructuralPath.Create(
                identity.StructuralPath,
                nameof(detailedIdentities));
            var root = FindApplicableRoot(fieldPath, availableRoots, nameof(detailedIdentities));
            var repeatedOwner = availableRepeatedPaths
                .Where(candidate => candidate.IsAncestorOfOrEqual(fieldPath))
                .OrderByDescending(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            var owner = repeatedOwner is null
                ? new RowFamilyId(sourceSetId, RowFamilyKind.Root, root.Value)
                : new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, repeatedOwner.Value);
            return new RowFamilyFieldAssignment(identity, owner);
        }

        static ParsedStructuralPath? FindStructuralSlotPath(
            DiscoveryInformationIdentity identity,
            IReadOnlyList<ParsedStructuralPath> availableRepeatedPaths)
        {
            var fieldPath = ParsedStructuralPath.Create(
                identity.StructuralPath,
                nameof(detailedIdentities));
            // The interpreter's selector-qualified Structural identity proves a typed
            // slot. Its parent must also be a repeated record; otherwise ordinary
            // repeated entries beneath a singleton branch remain independent rows.
            return availableRepeatedPaths
                .Where(candidate => candidate.IsAncestorOfOrEqual(fieldPath)
                    && availableRepeatedPaths.Any(parent =>
                        parent.Depth == candidate.Depth - 1
                        && parent.IsAncestorOf(candidate))
                    && identity.StructuralIdentity.StartsWith(
                        $"{candidate.Value}[",
                        StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Value, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    private static ParsedStructuralPath FindApplicableRoot(
        ParsedStructuralPath path,
        IReadOnlyList<ParsedStructuralPath> roots,
        string parameterName)
    {
        var matches = roots.Where(root => root.IsAncestorOfOrEqual(path)).ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "Every classified structural path must belong to exactly one document root.",
                parameterName);
        }

        return matches[0];
    }

    private sealed class ParsedStructuralPath : IEquatable<ParsedStructuralPath>
    {
        private ParsedStructuralPath(string value, string[] segments)
        {
            Value = value;
            Segments = segments;
        }

        public string Value { get; }

        public IReadOnlyList<string> Segments { get; }

        public int Depth => Segments.Count;

        public static ParsedStructuralPath Create(string value, string parameterName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
            if (value[0] != '/' || value.Length == 1 || value[^1] == '/' || value.Trim() != value)
            {
                throw new ArgumentException(
                    "A structural path must be an absolute, non-empty canonical path.",
                    parameterName);
            }

            var segments = new List<string>();
            var segmentStart = 1;
            var insideNamespace = false;
            for (var index = 1; index <= value.Length; index++)
            {
                if (index == value.Length || value[index] == '/' && !insideNamespace)
                {
                    if (index == segmentStart)
                    {
                        throw new ArgumentException(
                            "A structural path cannot contain an empty segment.",
                            parameterName);
                    }

                    segments.Add(value[segmentStart..index]);
                    segmentStart = index + 1;
                    continue;
                }

                if (value[index] == '{')
                {
                    if (insideNamespace || index != segmentStart)
                    {
                        throw new ArgumentException(
                            "A structural path contains an invalid expanded-name segment.",
                            parameterName);
                    }

                    insideNamespace = true;
                }
                else if (value[index] == '}')
                {
                    if (!insideNamespace || index == segmentStart + 1 || index == value.Length - 1)
                    {
                        throw new ArgumentException(
                            "A structural path contains an invalid namespace name.",
                            parameterName);
                    }

                    insideNamespace = false;
                }
            }

            if (insideNamespace || segments.Any(segment => !IsValidSegment(segment)))
            {
                throw new ArgumentException(
                    "A structural path contains an invalid expanded-name segment.",
                    parameterName);
            }

            return new ParsedStructuralPath(value, segments.ToArray());
        }

        public bool IsAncestorOf(ParsedStructuralPath other)
        {
            return Depth < other.Depth && IsAncestorOfOrEqual(other);
        }

        public bool IsAncestorOfOrEqual(ParsedStructuralPath other)
        {
            return Depth <= other.Depth
                && Segments.SequenceEqual(
                    other.Segments.Take(Depth),
                    StringComparer.Ordinal);
        }

        public bool Equals(ParsedStructuralPath? other)
        {
            return other is not null && StringComparer.Ordinal.Equals(Value, other.Value);
        }

        public override bool Equals(object? obj)
        {
            return obj is ParsedStructuralPath other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        private static bool IsValidSegment(string segment)
        {
            if (segment[0] != '{')
            {
                return segment.IndexOfAny(['{', '}']) < 0;
            }

            var namespaceEnd = segment.IndexOf('}');
            return namespaceEnd >= 2
                && namespaceEnd < segment.Length - 1
                && segment[(namespaceEnd + 1)..].IndexOfAny(['{', '}']) < 0;
        }
    }
}
