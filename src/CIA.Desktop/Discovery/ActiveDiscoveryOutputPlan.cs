using System.Text;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Desktop.Discovery;

public sealed record DiscoveryOutputModelProposal(
    SourceSetId ParentSourceSetId,
    DiscoveryOutputModelKey ModelKey,
    string SuggestedName,
    int ModelOrder,
    IReadOnlyList<RowFamilyId> RowFamilyIds,
    DiscoveryOutputProjectionMode ProjectionMode =
        DiscoveryOutputProjectionMode.PreserveRelationships);

public sealed class ActiveDiscoveryOutputPlan
{
    private readonly object _stateGate = new();
    private IReadOnlyList<DiscoveryOutputSetDefinition> _outputSets = [];
    private IReadOnlyList<SourceSetId> _parentSourceSetOrder = [];
    private readonly Dictionary<SourceSetId, DiscoveryOutputProjectionMode> _projectionModes = [];
    private readonly Dictionary<(SourceSetId, DiscoveryOutputModelKey),
        DiscoveryOutputSetDefinition> _retainedOutputSets = [];

    public DiscoveryOutputPlanSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _parentSourceSetOrder.Count == 0
                    ? DiscoveryOutputPlanSnapshot.Empty
                    : new DiscoveryOutputPlanSnapshot(_parentSourceSetOrder.Select(parent =>
                        new DiscoveryParentOutputPlan(
                            parent,
                            _outputSets.Where(set => set.ParentSourceSetId == parent)
                                .OrderBy(set => set.ModelOrder)
                                .ToArray(),
                            GetProjectionModeUnsafe(parent))).ToArray());
            }
        }
    }

    public void Synchronize(
        IEnumerable<DiscoveryOutputModelProposal> proposals,
        int highestExistingSetNumber)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var proposalArray = proposals.ToArray();
        Synchronize(
            proposalArray.Select(proposal => proposal.ParentSourceSetId).Distinct(),
            proposalArray,
            highestExistingSetNumber);
    }

    public void Synchronize(
        IEnumerable<SourceSetId> parentSourceSetIds,
        IEnumerable<DiscoveryOutputModelProposal> proposals,
        int highestExistingSetNumber)
    {
        ArgumentNullException.ThrowIfNull(parentSourceSetIds);
        ArgumentNullException.ThrowIfNull(proposals);
        if (highestExistingSetNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(highestExistingSetNumber));
        }

        var parents = parentSourceSetIds.Distinct().ToArray();
        var proposalArray = proposals.ToArray();
        if (proposalArray.Any(proposal => proposal is null)
            || proposalArray.Any(proposal => !parents.Contains(proposal.ParentSourceSetId))
            || proposalArray.Select(proposal =>
                    (proposal.ParentSourceSetId, proposal.ModelKey))
                .Distinct()
                .Count() != proposalArray.Length
            || proposalArray.GroupBy(proposal => proposal.ParentSourceSetId).Any(group =>
                group.Select(proposal => proposal.ModelOrder).Distinct().Count() != group.Count()))
        {
            throw new ArgumentException(
                "Discovery output proposals must be unique and deterministically ordered per parent.",
                nameof(proposals));
        }

        lock (_stateGate)
        {
            _parentSourceSetOrder = parents;
            foreach (var parent in parents)
            {
                _projectionModes.TryAdd(
                    parent,
                    DiscoveryOutputProjectionMode.PreserveRelationships);
            }

            var existing = _retainedOutputSets;
            var nextNumber = Math.Max(
                highestExistingSetNumber,
                _retainedOutputSets.Count == 0
                    ? 0
                    : _retainedOutputSets.Values.Max(set => set.SetNumber)) + 1;
            var synchronized = new List<DiscoveryOutputSetDefinition>(proposalArray.Length);
            foreach (var proposal in proposalArray)
            {
                if (existing.TryGetValue(
                        (proposal.ParentSourceSetId, proposal.ModelKey),
                        out var retained))
                {
                    synchronized.Add(new DiscoveryOutputSetDefinition(
                        retained.OutputSetId,
                        retained.ParentSourceSetId,
                        retained.ModelKey,
                        retained.Name,
                        retained.SetNumber,
                        proposal.ModelOrder,
                        retained.IsEnabled,
                        proposal.RowFamilyIds,
                        proposal.ProjectionMode));
                    continue;
                }

                synchronized.Add(new DiscoveryOutputSetDefinition(
                    OutputSetId.CreateNew(),
                    proposal.ParentSourceSetId,
                    proposal.ModelKey,
                    proposal.SuggestedName,
                    nextNumber++,
                    proposal.ModelOrder,
                    isEnabled: true,
                    proposal.RowFamilyIds,
                    proposal.ProjectionMode));
            }

            _outputSets = synchronized
                .OrderBy(set => set.SetNumber)
                .ToArray();
            foreach (var outputSet in _outputSets)
            {
                _retainedOutputSets[(outputSet.ParentSourceSetId, outputSet.ModelKey)] = outputSet;
            }
        }
    }

    public DiscoveryOutputProjectionMode GetProjectionMode(SourceSetId parentSourceSetId)
    {
        lock (_stateGate)
        {
            return GetProjectionModeUnsafe(parentSourceSetId);
        }
    }

    public bool SetProjectionMode(
        SourceSetId parentSourceSetId,
        DiscoveryOutputProjectionMode projectionMode)
    {
        if (!Enum.IsDefined(projectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(projectionMode));
        }

        lock (_stateGate)
        {
            if (GetProjectionModeUnsafe(parentSourceSetId) == projectionMode)
            {
                return false;
            }

            _projectionModes[parentSourceSetId] = projectionMode;
            return true;
        }
    }

    public bool SetEnabled(OutputSetId outputSetId, bool isEnabled)
    {
        lock (_stateGate)
        {
            var index = FindIndex(outputSetId);
            if (index < 0 || _outputSets[index].IsEnabled == isEnabled)
            {
                return false;
            }

            var sets = _outputSets.ToArray();
            var current = sets[index];
            sets[index] = new DiscoveryOutputSetDefinition(
                current.OutputSetId,
                current.ParentSourceSetId,
                current.ModelKey,
                current.Name,
                current.SetNumber,
                current.ModelOrder,
                isEnabled,
                current.RowFamilyIds,
                current.ProjectionMode);
            _outputSets = sets;
            _retainedOutputSets[(current.ParentSourceSetId, current.ModelKey)] = sets[index];
            return true;
        }
    }

    public bool CanRename(
        OutputSetId outputSetId,
        string? name,
        IEnumerable<string> reservedNames,
        out string normalizedName,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(reservedNames);
        normalizedName = name?.Trim() ?? string.Empty;
        lock (_stateGate)
        {
            var index = FindIndex(outputSetId);
            if (index < 0)
            {
                failureReason = "The selected Output Set is no longer available.";
                return false;
            }

            if (normalizedName.Length == 0)
            {
                failureReason = "Enter an Output Set name.";
                return false;
            }

            if (normalizedName.Length > DiscoveryOutputSetDefinition.MaximumNameLength)
            {
                failureReason = $"Output Set names cannot exceed {DiscoveryOutputSetDefinition.MaximumNameLength} characters.";
                return false;
            }

            if (string.Equals(_outputSets[index].Name, normalizedName, StringComparison.Ordinal))
            {
                failureReason = null;
                return false;
            }

            var candidateName = normalizedName;
            if (reservedNames.Concat(_outputSets
                    .Where(set => set.OutputSetId != outputSetId)
                    .Select(set => set.Name))
                .Any(existingName => string.Equals(
                    existingName.Trim(),
                    candidateName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                failureReason = "Output Set names must be unique.";
                return false;
            }

            failureReason = null;
            return true;
        }
    }

    public bool Rename(OutputSetId outputSetId, string normalizedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        lock (_stateGate)
        {
            var index = FindIndex(outputSetId);
            if (index < 0)
            {
                return false;
            }

            var sets = _outputSets.ToArray();
            var current = sets[index];
            sets[index] = new DiscoveryOutputSetDefinition(
                current.OutputSetId,
                current.ParentSourceSetId,
                current.ModelKey,
                normalizedName.Trim(),
                current.SetNumber,
                current.ModelOrder,
                current.IsEnabled,
                current.RowFamilyIds,
                current.ProjectionMode);
            _outputSets = sets;
            _retainedOutputSets[(current.ParentSourceSetId, current.ModelKey)] = sets[index];
            return true;
        }
    }

    private DiscoveryOutputProjectionMode GetProjectionModeUnsafe(SourceSetId parentSourceSetId) =>
        _projectionModes.GetValueOrDefault(
            parentSourceSetId,
            DiscoveryOutputProjectionMode.PreserveRelationships);

    private int FindIndex(OutputSetId outputSetId)
    {
        for (var index = 0; index < _outputSets.Count; index++)
        {
            if (_outputSets[index].OutputSetId == outputSetId)
            {
                return index;
            }
        }

        return -1;
    }
}

public static class DiscoveryOutputPlanBuilder
{
    public static IReadOnlyList<DiscoveryOutputModelProposal> Create(
        DatabaseBuildSpecification specification,
        IReadOnlyDictionary<SourceSetId, DiscoveryOutputProjectionMode>? projectionModes = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var proposals = new List<DiscoveryOutputModelProposal>();
        foreach (var dataset in specification.Datasets.OrderBy(dataset => dataset.Ordinal))
        {
            var projectionMode = projectionModes?.GetValueOrDefault(
                dataset.SourceSetId,
                DiscoveryOutputProjectionMode.PreserveRelationships)
                ?? DiscoveryOutputProjectionMode.PreserveRelationships;
            if (projectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile)
            {
                var selectedFamilies = dataset.RowFamilies
                    .Where(family => dataset.Fields.Any(field => field.RowFamilyId == family.Id))
                    .Select(family => family.Id)
                    .ToArray();
                if (selectedFamilies.Length > 0)
                {
                    proposals.Add(new DiscoveryOutputModelProposal(
                        dataset.SourceSetId,
                        new DiscoveryOutputModelKey("source-file-projection:v1"),
                        $"{dataset.DisplayName} - Source files",
                        1,
                        selectedFamilies,
                        projectionMode));
                }

                continue;
            }

            var order = 0;
            foreach (var family in dataset.RowFamilies)
            {
                if (!dataset.Fields.Any(field => field.RowFamilyId == family.Id))
                {
                    continue;
                }

                order++;
                proposals.Add(new DiscoveryOutputModelProposal(
                    dataset.SourceSetId,
                    CreateModelKey(family.Id),
                    CreateSuggestedName(dataset.DisplayName, family.Id),
                    order,
                    [family.Id],
                    projectionMode));
            }
        }

        var uniqueProposals = proposals
            .DistinctBy(proposal => (proposal.ParentSourceSetId, proposal.ModelKey))
            .ToArray();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return uniqueProposals.Select(proposal =>
        {
            var baseName = LimitName(proposal.SuggestedName, suffixLength: 0);
            var candidate = baseName;
            var count = 1;
            while (!usedNames.Add(candidate))
            {
                count++;
                var suffix = $" ({count})";
                candidate = $"{LimitName(baseName, suffix.Length)}{suffix}";
            }

            return proposal with { SuggestedName = candidate };
        }).ToArray();
    }

    private static DiscoveryOutputModelKey CreateModelKey(RowFamilyId familyId)
    {
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(familyId.AnchorPath));
        return new DiscoveryOutputModelKey($"family-projection:v1:{(int)familyId.Kind}:{encodedPath}");
    }

    private static string CreateSuggestedName(string parentName, RowFamilyId familyId)
    {
        if (familyId.Kind == RowFamilyKind.Root)
        {
            return $"{parentName} — Root";
        }

        var segment = familyId.AnchorPath[(familyId.AnchorPath.LastIndexOf('/') + 1)..];
        var namespaceEnd = segment.LastIndexOf('}');
        var displaySegment = namespaceEnd >= 0 && namespaceEnd < segment.Length - 1
            ? segment[(namespaceEnd + 1)..]
            : segment;
        return $"{parentName} — {displaySegment}";
    }

    private static string LimitName(string name, int suffixLength)
    {
        var maximumBaseLength = DiscoveryOutputSetDefinition.MaximumNameLength - suffixLength;
        return name.Length <= maximumBaseLength
            ? name
            : name[..maximumBaseLength].TrimEnd();
    }
}
