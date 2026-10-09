using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Hierarchy;

public enum RowFamilyKind
{
    Root = 1,
    Repeated = 2
}

public readonly record struct RowFamilyId : IComparable<RowFamilyId>
{
    [JsonConstructor]
    public RowFamilyId(
        SourceSetId sourceSetId,
        RowFamilyKind kind,
        string anchorPath)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A Row Family ID requires a Source Set ID.",
                nameof(sourceSetId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        StructuralPathSyntax.Validate(anchorPath, nameof(anchorPath));

        SourceSetId = sourceSetId;
        Kind = kind;
        AnchorPath = anchorPath;
    }

    public SourceSetId SourceSetId { get; }

    public RowFamilyKind Kind { get; }

    public string AnchorPath { get; }

    public int CompareTo(RowFamilyId other)
    {
        var sourceSetComparison = SourceSetId.Value.CompareTo(other.SourceSetId.Value);
        if (sourceSetComparison != 0)
        {
            return sourceSetComparison;
        }

        var pathComparison = StringComparer.Ordinal.Compare(AnchorPath, other.AnchorPath);
        return pathComparison != 0
            ? pathComparison
            : Kind.CompareTo(other.Kind);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;

            foreach (var value in SourceSetId.Value.ToByteArray())
            {
                hash = (hash ^ value) * prime;
            }

            hash = (hash ^ (uint)Kind) * prime;
            foreach (var value in AnchorPath)
            {
                hash = (hash ^ (byte)value) * prime;
                hash = (hash ^ (byte)(value >> 8)) * prime;
            }

            return (int)hash;
        }
    }

    public override string ToString()
    {
        return $"{SourceSetId}:{Kind}:{AnchorPath}";
    }
}

public sealed record RowFamilyDefinition
{
    [JsonConstructor]
    public RowFamilyDefinition(RowFamilyId id, RowFamilyId? parentRowFamilyId)
    {
        if (id == default)
        {
            throw new ArgumentException("A Row Family definition requires an ID.", nameof(id));
        }

        if (id.Kind == RowFamilyKind.Root && parentRowFamilyId is not null)
        {
            throw new ArgumentException(
                "A root Row Family cannot have a parent.",
                nameof(parentRowFamilyId));
        }

        if (id.Kind == RowFamilyKind.Repeated && parentRowFamilyId is null)
        {
            throw new ArgumentException(
                "A repeated Row Family requires a parent.",
                nameof(parentRowFamilyId));
        }

        if (parentRowFamilyId is { } parent
            && (parent.SourceSetId != id.SourceSetId
                || !StructuralPathSyntax.IsStrictAncestor(parent.AnchorPath, id.AnchorPath)))
        {
            throw new ArgumentException(
                "A Row Family parent must be a structural ancestor in the same Source Set.",
                nameof(parentRowFamilyId));
        }

        Id = id;
        ParentRowFamilyId = parentRowFamilyId;
    }

    public RowFamilyId Id { get; }

    public RowFamilyId? ParentRowFamilyId { get; }
}

public sealed record RowFamilyFieldAssignment
{
    [JsonConstructor]
    public RowFamilyFieldAssignment(
        DiscoveryInformationIdentity identity,
        RowFamilyId rowFamilyId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.SourceSetId != rowFamilyId.SourceSetId)
        {
            throw new ArgumentException(
                "A Row Family field assignment must remain inside one Source Set.",
                nameof(rowFamilyId));
        }

        Identity = identity;
        RowFamilyId = rowFamilyId;
    }

    public DiscoveryInformationIdentity Identity { get; }

    public RowFamilyId RowFamilyId { get; }
}

public sealed class RowFamilyClassificationResult
{
    [JsonConstructor]
    public RowFamilyClassificationResult(
        IReadOnlyList<RowFamilyDefinition> families,
        IReadOnlyList<RowFamilyFieldAssignment> fieldAssignments)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(fieldAssignments);

        var familyArray = families.ToArray();
        var assignmentArray = fieldAssignments.ToArray();
        if (familyArray.Length == 0 || familyArray.Any(family => family is null))
        {
            throw new ArgumentException(
                "A Row Family classification requires at least one family.",
                nameof(families));
        }

        if (familyArray.Select(family => family.Id).Distinct().Count() != familyArray.Length)
        {
            throw new ArgumentException(
                "A Row Family classification cannot contain duplicate family IDs.",
                nameof(families));
        }

        var familyIds = familyArray.Select(family => family.Id).ToHashSet();
        if (familyArray.Any(family => family.ParentRowFamilyId is { } parent
                && !familyIds.Contains(parent)))
        {
            throw new ArgumentException(
                "Every Row Family parent must be part of the classification.",
                nameof(families));
        }

        if (assignmentArray.Any(assignment => assignment is null
                || !familyIds.Contains(assignment.RowFamilyId)))
        {
            throw new ArgumentException(
                "Every field assignment must reference a classified Row Family.",
                nameof(fieldAssignments));
        }

        if (assignmentArray.Select(assignment => assignment.Identity).Distinct().Count()
            != assignmentArray.Length)
        {
            throw new ArgumentException(
                "A detailed Discovery identity can be assigned only once.",
                nameof(fieldAssignments));
        }

        Families = new ReadOnlyCollection<RowFamilyDefinition>(familyArray);
        FieldAssignments = new ReadOnlyCollection<RowFamilyFieldAssignment>(assignmentArray);
    }

    public IReadOnlyList<RowFamilyDefinition> Families { get; }

    public IReadOnlyList<RowFamilyFieldAssignment> FieldAssignments { get; }
}

internal static class StructuralPathSyntax
{
    public static void Validate(string path, string parameterName)
    {
        _ = GetSegments(path, parameterName);
    }

    public static bool IsStrictAncestor(string possibleAncestor, string path)
    {
        var ancestorSegments = GetSegments(possibleAncestor, nameof(possibleAncestor));
        var pathSegments = GetSegments(path, nameof(path));
        return ancestorSegments.Length < pathSegments.Length
            && ancestorSegments.SequenceEqual(
                pathSegments.Take(ancestorSegments.Length),
                StringComparer.Ordinal);
    }

    public static bool IsDirectParent(string possibleParent, string path)
    {
        var parentSegments = GetSegments(possibleParent, nameof(possibleParent));
        var pathSegments = GetSegments(path, nameof(path));
        return parentSegments.Length + 1 == pathSegments.Length
            && parentSegments.SequenceEqual(
                pathSegments.Take(parentSegments.Length),
                StringComparer.Ordinal);
    }

    private static string[] GetSegments(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path[0] != '/' || path.Length == 1 || path[^1] == '/' || path.Trim() != path)
        {
            throw new ArgumentException(
                "A structural path must be an absolute, non-empty canonical path.",
                parameterName);
        }

        var segments = new List<string>();
        var segmentStart = 1;
        var insideNamespace = false;
        for (var index = 1; index <= path.Length; index++)
        {
            if (index == path.Length || path[index] == '/' && !insideNamespace)
            {
                if (index == segmentStart)
                {
                    throw new ArgumentException(
                        "A structural path cannot contain an empty segment.",
                        parameterName);
                }

                var segment = path[segmentStart..index];
                ValidateSegment(segment, parameterName);
                segments.Add(segment);
                segmentStart = index + 1;
                continue;
            }

            if (path[index] == '{')
            {
                if (insideNamespace || index != segmentStart)
                {
                    throw new ArgumentException(
                        "A structural path contains an invalid expanded-name segment.",
                        parameterName);
                }

                insideNamespace = true;
            }
            else if (path[index] == '}')
            {
                if (!insideNamespace || index == segmentStart + 1)
                {
                    throw new ArgumentException(
                        "A structural path contains an invalid namespace name.",
                        parameterName);
                }

                insideNamespace = false;
            }
        }

        if (insideNamespace)
        {
            throw new ArgumentException(
                "A structural path contains an unterminated namespace name.",
                parameterName);
        }

        return segments.ToArray();
    }

    private static void ValidateSegment(string segment, string parameterName)
    {
        if (segment[0] == '{')
        {
            var namespaceEnd = segment.IndexOf('}');
            if (namespaceEnd < 2
                || namespaceEnd == segment.Length - 1
                || segment[(namespaceEnd + 1)..].IndexOfAny(['{', '}']) >= 0)
            {
                throw new ArgumentException(
                    "A structural path contains an invalid expanded-name segment.",
                    parameterName);
            }

            return;
        }

        if (segment.IndexOfAny(['{', '}']) >= 0)
        {
            throw new ArgumentException(
                "A structural path contains an invalid name segment.",
                parameterName);
        }
    }
}
