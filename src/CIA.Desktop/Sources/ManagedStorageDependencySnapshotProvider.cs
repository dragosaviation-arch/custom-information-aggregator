using System.IO;
using CIA.Contracts.Sources;
using CIA.Core.ManagedStorage;
using CIA.Core.Runtime;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Sources;

public sealed class ManagedStorageDependencySnapshotProvider(
    IApplicationWorkflowCoordinator workflowCoordinator,
    ActiveLoadedSourceSet sourceSet,
    ApplicationSettingsService settingsService,
    SourceIntakeActivityRegistry? intakeActivities = null)
    : IManagedStorageDependencySnapshotProvider
{
    public ManagedStorageDependencySnapshot CreateSnapshot()
    {
        var activeOperationIds = workflowCoordinator.Current.ActiveOperation is { } operation
            ? new[] { operation.Correlation.OperationId }
            : [];
        var sources = sourceSet.CreateWorkingStateSourceSnapshot();
        var sourceDependencies = sources.Select(source => new ManagedStorageSourceDependency(
            source.SourceId,
            source.SourceSetId,
            source.ArchiveProvenance?.OriginalArchiveSourceId,
            source.Path,
            source.ArchiveProvenance?.ExtractionRoot,
            source.ArchiveProvenance?.Retention)).ToArray();
        var protectedLocations = new List<ManagedStorageProtectedLocation>();

        foreach (var source in sources)
        {
            protectedLocations.Add(new ManagedStorageProtectedLocation(
                source.ArchiveProvenance?.OriginalArchivePath ?? source.Path,
                ManagedStorageArtifactKind.OriginalOrExternalSource,
                IncludeDescendants: false));

            if (source.ArchiveProvenance is
                {
                    Retention: ArchiveExtractionRetention.Persistent,
                    ExtractionRoot: { } extractionRoot
                })
            {
                protectedLocations.Add(new ManagedStorageProtectedLocation(
                    extractionRoot,
                    ManagedStorageArtifactKind.PersistentArchiveExtraction));
            }
        }

        AddPersistentExtractionLocation(
            protectedLocations,
            settingsService.Startup.Settings.PersistentArchiveExtractionDirectory);
        AddPersistentExtractionLocation(
            protectedLocations,
            settingsService.Current.PersistentArchiveExtractionDirectory);

        return new ManagedStorageDependencySnapshot(
            activeOperationIds,
            intakeActivities?.CreateSnapshot() ?? [],
            sourceDependencies,
            RetainedResultDependencies: [],
            protectedLocations
                .DistinctBy(
                    location => Path.GetFullPath(location.Path),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static void AddPersistentExtractionLocation(
        List<ManagedStorageProtectedLocation> protectedLocations,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return;
        }

        protectedLocations.Add(new ManagedStorageProtectedLocation(
            path!,
            ManagedStorageArtifactKind.PersistentArchiveExtraction));
    }
}
