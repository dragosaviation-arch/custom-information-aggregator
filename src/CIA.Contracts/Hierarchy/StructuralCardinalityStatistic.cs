using System.Text.Json.Serialization;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Hierarchy;

public sealed record StructuralCardinalityStatistic
{
    [JsonConstructor]
    public StructuralCardinalityStatistic(
        SourceSetId sourceSetId,
        string documentRootPath,
        string parentStructuralPath,
        string childStructuralPath,
        int maximumChildInstancesPerParent,
        long observedParentInstanceCount,
        long repeatingParentInstanceCount,
        int observedSourceCount)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "Structural cardinality requires a Source Set ID.",
                nameof(sourceSetId));
        }

        StructuralPathSyntax.Validate(documentRootPath, nameof(documentRootPath));
        StructuralPathSyntax.Validate(parentStructuralPath, nameof(parentStructuralPath));
        StructuralPathSyntax.Validate(childStructuralPath, nameof(childStructuralPath));
        if (documentRootPath != parentStructuralPath
            && !StructuralPathSyntax.IsStrictAncestor(documentRootPath, parentStructuralPath))
        {
            throw new ArgumentException(
                "The structural parent must belong to the stated document root.",
                nameof(parentStructuralPath));
        }

        if (!StructuralPathSyntax.IsDirectParent(parentStructuralPath, childStructuralPath))
        {
            throw new ArgumentException(
                "Structural cardinality must describe one direct parent-child path.",
                nameof(childStructuralPath));
        }

        if (maximumChildInstancesPerParent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChildInstancesPerParent));
        }

        if (observedParentInstanceCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observedParentInstanceCount));
        }

        if (repeatingParentInstanceCount < 0
            || repeatingParentInstanceCount > observedParentInstanceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(repeatingParentInstanceCount));
        }

        if ((maximumChildInstancesPerParent > 1) != (repeatingParentInstanceCount > 0))
        {
            throw new ArgumentException(
                "Repeated-parent counts must agree with the maximum observed cardinality.",
                nameof(repeatingParentInstanceCount));
        }

        if (observedSourceCount < 1 || observedSourceCount > observedParentInstanceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(observedSourceCount));
        }

        SourceSetId = sourceSetId;
        DocumentRootPath = documentRootPath;
        ParentStructuralPath = parentStructuralPath;
        ChildStructuralPath = childStructuralPath;
        MaximumChildInstancesPerParent = maximumChildInstancesPerParent;
        ObservedParentInstanceCount = observedParentInstanceCount;
        RepeatingParentInstanceCount = repeatingParentInstanceCount;
        ObservedSourceCount = observedSourceCount;
    }

    public SourceSetId SourceSetId { get; }

    public string DocumentRootPath { get; }

    public string ParentStructuralPath { get; }

    public string ChildStructuralPath { get; }

    public int MaximumChildInstancesPerParent { get; }

    public long ObservedParentInstanceCount { get; }

    public long RepeatingParentInstanceCount { get; }

    public int ObservedSourceCount { get; }

    [JsonIgnore]
    public bool IsRepeated => MaximumChildInstancesPerParent > 1;
}
