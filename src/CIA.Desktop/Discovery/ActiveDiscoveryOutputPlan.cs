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
    IReadOnlyList<RowFamilyId> RowFamilyIds);

public sealed class ActiveDiscoveryOutputPlan
{
    private readonly object _stateGate = new();
    private IReadOnlyList<DiscoveryOutputSetDefinition> _outputSets = [];
    private IReadOnlyList<SourceSetId> _parentSourceSetOrder = [];

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
                                .ToArray())).ToArray());
            }
        }
    }

    public void Restore(DiscoveryOutputPlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_stateGate)
        {
            _parentSourceSetOrder = snapshot.ParentPlans
                .Select(plan => plan.ParentSourceSetId)
                .ToArray();
            _outputSets = snapshot.OutputSets
                .OrderBy(outputSet => outputSet.SetNumber)
                .ToArray();
        }
    }

    public void Reset()
    {
        lock (_stateGate)
        {
            _parentSourceSetOrder = [];
            _outputSets = [];
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
            var existing = _outputSets.ToDictionary(
                set => (set.ParentSourceSetId, set.ModelKey));
            var nextNumber = Math.Max(
                highestExistingSetNumber,
                _outputSets.Count == 0 ? 0 : _outputSets.Max(set => set.SetNumber)) + 1;
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
                        proposal.RowFamilyIds));
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
                    proposal.RowFamilyIds));
            }

            _outputSets = synchronized
                .OrderBy(set => set.SetNumber)
                .ToArray();
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
                current.RowFamilyIds);
            _outputSets = sets;
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
                current.RowFamilyIds);
            _outputSets = sets;
            return true;
        }
    }

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
        DatabaseBuildSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var proposals = new List<DiscoveryOutputModelProposal>();
        foreach (var dataset in specification.Datasets.OrderBy(dataset => dataset.Ordinal))
        {
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
                    [family.Id]));
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
