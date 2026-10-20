using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Core.Database;

public static class DatabaseBuildSpecificationFactory
{
    public static DatabaseBuildSpecification CreateApprovedOutputSets(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames,
        DiscoveryFamilyCatalog familyCatalog,
        IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> familyAssignments,
        DiscoveryOutputPlanSnapshot outputPlan)
    {
        ArgumentNullException.ThrowIfNull(outputPlan);
        var parentDatasets = Create(
                configuration,
                overrides,
                sources,
                sourceSetNames,
                familyCatalog,
                familyAssignments)
            .Datasets
            .ToDictionary(dataset => dataset.SourceSetId);
        var enabledSets = outputPlan.OutputSets
            .Where(outputSet => outputSet.IsEnabled)
            .OrderBy(outputSet => outputSet.SetNumber)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>(enabledSets.Length);
        foreach (var outputSet in enabledSets)
        {
            if (!parentDatasets.TryGetValue(outputSet.ParentSourceSetId, out var parent))
            {
                throw new ArgumentException(
                    "An enabled Output Set has no current parent Source Set dataset.",
                    nameof(outputPlan));
            }

            var selectedFamilies = outputSet.RowFamilyIds.ToHashSet();
            var families = parent.RowFamilies
                .Where(family => selectedFamilies.Contains(family.Id))
                .ToArray();
            var fields = parent.Fields
                .Where(field => selectedFamilies.Contains(field.RowFamilyId))
                .ToArray();
            if (families.Length != selectedFamilies.Count
                || fields.Length == 0
                || fields.Any(field => !selectedFamilies.Contains(field.RowFamilyId)))
            {
                throw new ArgumentException(
                    "An enabled Output Set no longer matches its approved field projection.",
                    nameof(outputPlan));
            }

            if (outputSet.ProjectionMode == DiscoveryOutputProjectionMode.OneRowPerSourceFile)
            {
                var projectionFamily = new RowFamilyId(
                    outputSet.ParentSourceSetId,
                    RowFamilyKind.SourceFileProjection,
                    "/__cia_source_file__");
                var projectionFields = CreateSourceFileFieldMappings(
                    projectionFamily,
                    fields);
                var repeatedPaths = familyCatalog.Families
                    .Where(family => family.Id.SourceSetId == outputSet.ParentSourceSetId
                        && family.Id.Kind == RowFamilyKind.Repeated)
                    .Select(family => family.Id.AnchorPath)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                datasets.Add(new DatabaseDatasetBuildSpecification(
                    outputSet.OutputSetId,
                    outputSet.ParentSourceSetId,
                    outputSet.Name,
                    datasets.Count + 1,
                    RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                    parent.Sources,
                    [new RowFamilyDefinition(projectionFamily, null)],
                    projectionFields,
                    outputSet.ProjectionMode,
                    repeatedPaths));
                continue;
            }

            datasets.Add(new DatabaseDatasetBuildSpecification(
                outputSet.OutputSetId,
                outputSet.ParentSourceSetId,
                outputSet.Name,
                datasets.Count + 1,
                parent.RepeatedDataLayout,
                parent.Sources,
                families,
                fields,
                outputSet.ProjectionMode));
        }

        return new DatabaseBuildSpecification(datasets);
    }

    private static IReadOnlyList<DatabaseFieldMapping> CreateSourceFileFieldMappings(
        RowFamilyId projectionFamily,
        IReadOnlyList<DatabaseFieldMapping> fields)
    {
        var candidates = fields
            .SelectMany(field => field.DetailedIdentities.Select(identity => new
            {
                OriginatingFamilyId = field.RowFamilyId,
                field.EffectiveName,
                field.IsExplicitOverride,
                Identity = identity
            }))
            .OrderBy(candidate => candidate.OriginatingFamilyId)
            .ThenBy(candidate => candidate.Identity.StructuralPath, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.CandidateKind)
            .ThenBy(candidate => candidate.Identity.StructuralIdentity, StringComparer.Ordinal)
            .ToArray();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var mappings = new List<DatabaseFieldMapping>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var effectiveName = candidate.EffectiveName;
            if (!usedNames.Add(effectiveName))
            {
                var suffix = 2;
                while (!usedNames.Add($"{candidate.EffectiveName} ({suffix})"))
                {
                    suffix++;
                }

                effectiveName = $"{candidate.EffectiveName} ({suffix})";
            }

            mappings.Add(new DatabaseFieldMapping(
                projectionFamily,
                effectiveName,
                candidate.IsExplicitOverride,
                [candidate.Identity],
                candidate.OriginatingFamilyId));
        }

        return mappings;
    }

    public static DatabaseBuildSpecification Create(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames,
        DiscoveryFamilyCatalog familyCatalog,
        IReadOnlyDictionary<DiscoveryInformationIdentity, RowFamilyId> familyAssignments)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceSetNames);
        ArgumentNullException.ThrowIfNull(familyCatalog);
        ArgumentNullException.ThrowIfNull(familyAssignments);

        var includedReadySources = sources
            .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>();
        foreach (var setConfiguration in configuration.SourceSets)
        {
            var fields = DatabaseTagMapper.CreateFieldMappings(
                setConfiguration.SourceSetId,
                configuration.Items,
                overrides,
                familyAssignments);
            var datasetSources = includedReadySources
                .Where(source => source.SourceSetId == setConfiguration.SourceSetId)
                .ToArray();
            if (fields.Count == 0 || datasetSources.Length == 0)
            {
                continue;
            }

            var selectedFamilyIds = fields.Select(field => field.RowFamilyId).ToHashSet();
            var families = familyCatalog.Families
                .Where(family => selectedFamilyIds.Contains(family.Id))
                .OrderBy(family => family.Id)
                .ToArray();

            datasets.Add(new DatabaseDatasetBuildSpecification(
                setConfiguration.SourceSetId,
                sourceSetNames.GetValueOrDefault(
                    setConfiguration.SourceSetId,
                    $"Set {datasets.Count + 1}"),
                datasets.Count + 1,
                setConfiguration.RepeatedDataLayout,
                datasetSources,
                families,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }

    public static DatabaseBuildSpecification Create(
        DiscoveryConfigurationSnapshot configuration,
        IReadOnlyDictionary<DiscoveryInformationIdentity, string> overrides,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyDictionary<SourceSetId, string> sourceSetNames)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceSetNames);

        var includedReadySources = sources
            .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
            .ToArray();
        var datasets = new List<DatabaseDatasetBuildSpecification>();
        foreach (var setConfiguration in configuration.SourceSets)
        {
            var fields = DatabaseTagMapper.CreateFieldMappings(
                setConfiguration.SourceSetId,
                configuration.Items,
                overrides);
            var datasetSources = includedReadySources
                .Where(source => source.SourceSetId == setConfiguration.SourceSetId)
                .ToArray();
            if (fields.Count == 0 || datasetSources.Length == 0)
            {
                continue;
            }

            datasets.Add(new DatabaseDatasetBuildSpecification(
                setConfiguration.SourceSetId,
                sourceSetNames.GetValueOrDefault(
                    setConfiguration.SourceSetId,
                    $"Set {datasets.Count + 1}"),
                datasets.Count + 1,
                setConfiguration.RepeatedDataLayout,
                datasetSources,
                fields));
        }

        return new DatabaseBuildSpecification(datasets);
    }
}
