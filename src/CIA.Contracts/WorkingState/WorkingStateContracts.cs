using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;

namespace CIA.Contracts.WorkingState;

public static class WorkingStatePackageFormat
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "database.sqlite3";
    public const long MaximumManifestLength = 4 * 1024 * 1024;
    public const long MaximumDatabaseLength = 16L * 1024 * 1024 * 1024;
}

public enum WorkingStatePublicationMode
{
    ReplaceExisting = 0,
    CreateNew = 1
}

public sealed record WorkingStateSourceSet
{
    [JsonConstructor]
    public WorkingStateSourceSet(SourceSetId sourceSetId, string name, int ordinal)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException("A saved Source Set requires a valid identity.", nameof(sourceSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        SourceSetId = sourceSetId;
        Name = name;
        Ordinal = ordinal;
    }

    public SourceSetId SourceSetId { get; }

    public string Name { get; }

    public int Ordinal { get; }
}

public sealed record WorkingStateDatabaseTagOverride
{
    [JsonConstructor]
    public WorkingStateDatabaseTagOverride(
        DiscoveryInformationIdentity identity,
        string databaseTagName)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseTagName);
        Identity = identity;
        DatabaseTagName = databaseTagName;
    }

    public DiscoveryInformationIdentity Identity { get; }

    public string DatabaseTagName { get; }
}

public sealed record WorkingStateSnapshot
{
    [JsonConstructor]
    public WorkingStateSnapshot(
        Guid packageId,
        DateTimeOffset savedAtUtc,
        DatabaseGenerationSummary? databaseGeneration,
        IReadOnlyList<WorkingStateSourceSet> sourceSets,
        SourceSetId? activeSourceSetId,
        IReadOnlyList<LoadedSourceContract> sources,
        DiscoveryConfigurationSnapshot discoveryConfiguration,
        IReadOnlyList<WorkingStateDatabaseTagOverride> databaseTagOverrides)
    {
        if (packageId == Guid.Empty || packageId.Version != 7)
        {
            throw new ArgumentException("A working-state package requires a UUIDv7 identity.", nameof(packageId));
        }

        if (savedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Working-state timestamps must use UTC DateTimeOffset values.", nameof(savedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(sourceSets);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(discoveryConfiguration);
        ArgumentNullException.ThrowIfNull(databaseTagOverrides);

        PackageId = packageId;
        SavedAtUtc = savedAtUtc;
        DatabaseGeneration = databaseGeneration;
        SourceSets = new ReadOnlyCollection<WorkingStateSourceSet>(sourceSets.ToArray());
        ActiveSourceSetId = activeSourceSetId;
        Sources = new ReadOnlyCollection<LoadedSourceContract>(sources
            .Select(CloneSource)
            .ToArray());
        DiscoveryConfiguration = discoveryConfiguration;
        DatabaseTagOverrides = new ReadOnlyCollection<WorkingStateDatabaseTagOverride>(
            databaseTagOverrides.ToArray());

        WorkingStateContractValidator.Validate(this);
    }

    private static LoadedSourceContract CloneSource(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source with
        {
            ArchiveProvenance = source.ArchiveProvenance is null
                ? null
                : source.ArchiveProvenance with
                {
                    ArchiveLineage = source.ArchiveProvenance.ArchiveLineage.ToArray()
                }
        };
    }

    public Guid PackageId { get; }

    public DateTimeOffset SavedAtUtc { get; }

    public DatabaseGenerationSummary? DatabaseGeneration { get; }

    public IReadOnlyList<WorkingStateSourceSet> SourceSets { get; }

    public SourceSetId? ActiveSourceSetId { get; }

    public IReadOnlyList<LoadedSourceContract> Sources { get; }

    public DiscoveryConfigurationSnapshot DiscoveryConfiguration { get; }

    public IReadOnlyList<WorkingStateDatabaseTagOverride> DatabaseTagOverrides { get; }
}

public sealed record WorkingStateManifest
{
    [JsonConstructor]
    public WorkingStateManifest(
        int packageSchemaVersion,
        int repositorySchemaVersion,
        string databaseSha256,
        WorkingStateSnapshot snapshot)
    {
        if (packageSchemaVersion != WorkingStatePackageFormat.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Working-state package schema version {packageSchemaVersion} is not supported.");
        }

        if (repositorySchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repositorySchemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(databaseSha256);
        if (databaseSha256.Length != 64 || databaseSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The SQLite snapshot SHA-256 digest is invalid.", nameof(databaseSha256));
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        PackageSchemaVersion = packageSchemaVersion;
        RepositorySchemaVersion = repositorySchemaVersion;
        DatabaseSha256 = databaseSha256.ToLowerInvariant();
        Snapshot = snapshot;
    }

    public int PackageSchemaVersion { get; }

    public int RepositorySchemaVersion { get; }

    public string DatabaseSha256 { get; }

    public WorkingStateSnapshot Snapshot { get; }
}

public static class WorkingStateContractValidator
{
    public static void Validate(WorkingStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sourceSets = snapshot.SourceSets.ToArray();
        if (sourceSets.Any(sourceSet => sourceSet is null)
            || sourceSets.Select(sourceSet => sourceSet.SourceSetId).Distinct().Count() != sourceSets.Length
            || !sourceSets.Select(sourceSet => sourceSet.Ordinal)
                .SequenceEqual(Enumerable.Range(1, sourceSets.Length)))
        {
            throw new ArgumentException("Saved Source Sets must have unique identities and deterministic order.");
        }

        var sourceSetIds = sourceSets.Select(sourceSet => sourceSet.SourceSetId).ToHashSet();
        if ((sourceSets.Length == 0) != (snapshot.ActiveSourceSetId is null)
            || snapshot.ActiveSourceSetId is { } active && !sourceSetIds.Contains(active))
        {
            throw new ArgumentException("The active Source Set is not present in the saved Source Sets.");
        }

        if (snapshot.Sources.Any(source => source is null
                || !SourceId.IsValid(source.SourceId.Value)
                || !sourceSetIds.Contains(source.SourceSetId)
                || string.IsNullOrWhiteSpace(source.Path)
                || !Path.IsPathFullyQualified(source.Path)
                || !Enum.IsDefined(source.Status)
                || !Enum.IsDefined(source.Kind)
                || !HasValidArchiveProvenance(source))
            || snapshot.Sources.Select(source => source.SourceId).Distinct().Count() != snapshot.Sources.Count)
        {
            throw new ArgumentException("Saved sources must have unique identities and valid Source Set membership.");
        }

        var configuredIdentities = snapshot.DiscoveryConfiguration.Items
            .Select(item => item.Identity)
            .ToHashSet();
        if (!snapshot.DiscoveryConfiguration.SourceSets
                .Select(configuration => configuration.SourceSetId)
                .SequenceEqual(sourceSets.Select(sourceSet => sourceSet.SourceSetId))
            || snapshot.DatabaseTagOverrides.Any(overridden =>
                overridden is null || !configuredIdentities.Contains(overridden.Identity))
            || snapshot.DatabaseTagOverrides.Select(overridden => overridden.Identity)
                .Distinct().Count() != snapshot.DatabaseTagOverrides.Count)
        {
            throw new ArgumentException("The saved Discovery configuration relationships are invalid.");
        }

        if (snapshot.DatabaseGeneration is { IsHierarchyAware: false })
        {
            throw new ArgumentException("Working state can retain only hierarchy-aware Database generations.");
        }

        if (snapshot.DatabaseGeneration is { } generation
            && generation.Datasets.Any(dataset => !sourceSetIds.Contains(dataset.SourceSetId)))
        {
            throw new ArgumentException("The saved Database references an unknown Source Set.");
        }
    }

    private static bool HasValidArchiveProvenance(LoadedSourceContract source)
    {
        var provenance = source.ArchiveProvenance;
        if (provenance is null)
        {
            return true;
        }

        if (!SourceId.IsValid(provenance.OriginalArchiveSourceId.Value)
            || string.IsNullOrWhiteSpace(provenance.OriginalArchivePath)
            || !Path.IsPathFullyQualified(provenance.OriginalArchivePath)
            || string.IsNullOrWhiteSpace(provenance.ExtractionRoot)
            || !Path.IsPathFullyQualified(provenance.ExtractionRoot)
            || string.IsNullOrWhiteSpace(provenance.ArchiveMemberPath)
            || provenance.ArchiveNestingLevel < 1
            || !ArchiveNestingDepth.IsValid(provenance.MaximumArchiveNestingDepth.Value)
            || !provenance.MaximumArchiveNestingDepth.AllowsLevel(provenance.ArchiveNestingLevel)
            || !Enum.IsDefined(provenance.Retention)
            || provenance.ArchiveLineage is null
            || provenance.ArchiveLineage.Count != provenance.ArchiveNestingLevel)
        {
            return false;
        }

        return provenance.ArchiveLineage.Select((lineage, index) => new { lineage, index })
            .All(item => item.lineage is not null
                && SourceId.IsValid(item.lineage.ArchiveSourceId.Value)
                && !string.IsNullOrWhiteSpace(item.lineage.Path)
                && item.lineage.NestingLevel == item.index + 1)
            && provenance.ArchiveLineage[0].ArchiveSourceId
                == provenance.OriginalArchiveSourceId;
    }
}
