namespace CIA.Core.Sources;

public sealed record SourceStructuralCardinality
{
    public SourceStructuralCardinality(
        string documentRootPath,
        string parentStructuralPath,
        string childStructuralPath,
        int maximumChildInstancesPerParent,
        long observedParentInstanceCount,
        long repeatingParentInstanceCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentStructuralPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(childStructuralPath);
        if (maximumChildInstancesPerParent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChildInstancesPerParent));
        }

        if (observedParentInstanceCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observedParentInstanceCount));
        }

        if (repeatingParentInstanceCount < 0
            || repeatingParentInstanceCount > observedParentInstanceCount
            || (maximumChildInstancesPerParent > 1) != (repeatingParentInstanceCount > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(repeatingParentInstanceCount));
        }

        DocumentRootPath = documentRootPath;
        ParentStructuralPath = parentStructuralPath;
        ChildStructuralPath = childStructuralPath;
        MaximumChildInstancesPerParent = maximumChildInstancesPerParent;
        ObservedParentInstanceCount = observedParentInstanceCount;
        RepeatingParentInstanceCount = repeatingParentInstanceCount;
    }

    public string DocumentRootPath { get; }

    public string ParentStructuralPath { get; }

    public string ChildStructuralPath { get; }

    public int MaximumChildInstancesPerParent { get; }

    public long ObservedParentInstanceCount { get; }

    public long RepeatingParentInstanceCount { get; }
}
