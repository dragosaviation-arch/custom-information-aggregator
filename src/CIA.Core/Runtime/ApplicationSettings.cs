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
            PostExportBehavior = PostExportBehavior.StatusOnly
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
