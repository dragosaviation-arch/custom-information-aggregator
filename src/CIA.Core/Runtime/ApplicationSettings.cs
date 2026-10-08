using CIA.Contracts.Sources;

namespace CIA.Core.Runtime;

public enum PostExportBehavior
{
    StatusOnly = 1,
    OpenExportedFile = 2,
    OpenContainingFolder = 3,
    AskEachTime = 4
}

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required string TemporaryDirectory { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string ProfilesDirectory { get; init; }

    public required string SettingsDirectory { get; init; }

    public required bool TraverseSubfolders { get; init; }

    public required ArchiveNestingDepth MaximumArchiveNestingDepth { get; init; }

    public required bool PersistentArchiveExtractionEnabled { get; init; }

    public string? PersistentArchiveExtractionDirectory { get; init; }

    public string? LastUsedOutputDirectory { get; init; }

    public required PostExportBehavior PostExportBehavior { get; init; }

    public Dictionary<string, double> ColumnWidths { get; init; } = [];

    public bool OpenDiscoveryWhenGenerationCompletes { get; init; }

    public bool OpenDatabaseWhenCreationCompletes { get; init; }

    public bool Equals(ApplicationSettings? other)
    {
        return ReferenceEquals(this, other)
            || other is not null
            && SchemaVersion == other.SchemaVersion
            && string.Equals(TemporaryDirectory, other.TemporaryDirectory, StringComparison.Ordinal)
            && string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.Ordinal)
            && string.Equals(ProfilesDirectory, other.ProfilesDirectory, StringComparison.Ordinal)
            && string.Equals(SettingsDirectory, other.SettingsDirectory, StringComparison.Ordinal)
            && TraverseSubfolders == other.TraverseSubfolders
            && MaximumArchiveNestingDepth == other.MaximumArchiveNestingDepth
            && PersistentArchiveExtractionEnabled == other.PersistentArchiveExtractionEnabled
            && string.Equals(
                PersistentArchiveExtractionDirectory,
                other.PersistentArchiveExtractionDirectory,
                StringComparison.Ordinal)
            && string.Equals(
                LastUsedOutputDirectory,
                other.LastUsedOutputDirectory,
                StringComparison.Ordinal)
            && PostExportBehavior == other.PostExportBehavior
            && OpenDiscoveryWhenGenerationCompletes
                == other.OpenDiscoveryWhenGenerationCompletes
            && OpenDatabaseWhenCreationCompletes == other.OpenDatabaseWhenCreationCompletes
            && ColumnWidths.Count == other.ColumnWidths.Count
            && ColumnWidths.All(entry =>
                other.ColumnWidths.TryGetValue(entry.Key, out var width)
                && entry.Value.Equals(width));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(TemporaryDirectory, StringComparer.Ordinal);
        hash.Add(WorkingDirectory, StringComparer.Ordinal);
        hash.Add(ProfilesDirectory, StringComparer.Ordinal);
        hash.Add(SettingsDirectory, StringComparer.Ordinal);
        hash.Add(TraverseSubfolders);
        hash.Add(MaximumArchiveNestingDepth);
        hash.Add(PersistentArchiveExtractionEnabled);
        hash.Add(PersistentArchiveExtractionDirectory, StringComparer.Ordinal);
        hash.Add(LastUsedOutputDirectory, StringComparer.Ordinal);
        hash.Add(PostExportBehavior);
        hash.Add(OpenDiscoveryWhenGenerationCompletes);
        hash.Add(OpenDatabaseWhenCreationCompletes);
        foreach (var entry in ColumnWidths.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            hash.Add(entry.Key, StringComparer.Ordinal);
            hash.Add(entry.Value);
        }

        return hash.ToHashCode();
    }

    public static ApplicationSettings CreateDefault(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        if (!Path.IsPathFullyQualified(localApplicationDataDirectory))
        {
            throw new ArgumentException(
                "The LocalAppData directory must be an absolute path.",
                nameof(localApplicationDataDirectory));
        }

        var applicationDataDirectory = Path.Combine(
            Path.GetFullPath(localApplicationDataDirectory),
            ApplicationPaths.ApplicationDirectoryName);
        return new ApplicationSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            TemporaryDirectory = Path.Combine(applicationDataDirectory, "Temp"),
            WorkingDirectory = Path.Combine(applicationDataDirectory, "Working"),
            ProfilesDirectory = Path.Combine(applicationDataDirectory, "Profiles"),
            SettingsDirectory = Path.Combine(applicationDataDirectory, "Settings"),
            TraverseSubfolders = true,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.Default,
            PersistentArchiveExtractionEnabled = false,
            PersistentArchiveExtractionDirectory = null,
            LastUsedOutputDirectory = null,
            PostExportBehavior = PostExportBehavior.StatusOnly,
            ColumnWidths = [],
            OpenDiscoveryWhenGenerationCompletes = false,
            OpenDatabaseWhenCreationCompletes = false
        };
    }
}

public enum ApplicationSettingsReadState
{
    Loaded = 1,
    DefaultsBecauseFileMissing = 2,
    DefaultsBecauseBootstrapInvalid = 3,
    DefaultsBecauseSettingsInvalid = 4,
    DefaultsBecauseVersionUnsupported = 5
}

public sealed record ApplicationSettingsLoadResult(
    ApplicationSettings Settings,
    ApplicationPaths RuntimePaths,
    ApplicationSettingsReadState State,
    string SettingsFilePath,
    string? Diagnostic)
{
    public bool UsedFallbackDefaults => State != ApplicationSettingsReadState.Loaded
        && State != ApplicationSettingsReadState.DefaultsBecauseFileMissing;
}

public sealed record ApplicationSettingsSaveResult(
    bool Succeeded,
    ApplicationSettings? Settings,
    string? FailureDescription)
{
    public static ApplicationSettingsSaveResult Success(ApplicationSettings settings) =>
        new(true, settings, null);

    public static ApplicationSettingsSaveResult Failure(string description) =>
        new(false, null, description);
}
