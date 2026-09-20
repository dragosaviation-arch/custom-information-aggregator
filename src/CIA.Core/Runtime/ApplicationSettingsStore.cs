using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Core.Runtime;

public interface IAtomicSettingsFileWriter
{
    void Write(string finalPath, ReadOnlyMemory<byte> content);
}

public sealed class AtomicSettingsFileWriter : IAtomicSettingsFileWriter
{
    public void Write(string finalPath, ReadOnlyMemory<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("The settings file has no parent directory.");
        Directory.CreateDirectory(directory);
        var candidatePath = finalPath + "." + Guid.NewGuid().ToString("N") + ".incomplete";

        try
        {
            using (var stream = new FileStream(
                       candidatePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content.Span);
                stream.Flush(flushToDisk: true);
            }

            File.Move(candidatePath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }
}

public sealed class ApplicationSettingsStore
{
    public const string SettingsFileName = "application-settings.json";
    public const string BootstrapFileName = "settings-location.json";
    private const int BootstrapSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _localApplicationDataDirectory;
    private readonly string _fixedApplicationDataDirectory;
    private readonly string _bootstrapFilePath;
    private readonly IAtomicSettingsFileWriter _writer;

    public ApplicationSettingsStore(
        string localApplicationDataDirectory,
        IAtomicSettingsFileWriter? writer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        if (!Path.IsPathFullyQualified(localApplicationDataDirectory))
        {
            throw new ArgumentException(
                "The LocalAppData directory must be an absolute path.",
                nameof(localApplicationDataDirectory));
        }

        _localApplicationDataDirectory = Path.GetFullPath(localApplicationDataDirectory);
        _fixedApplicationDataDirectory = Path.Combine(
            _localApplicationDataDirectory,
            ApplicationPaths.ApplicationDirectoryName);
        _bootstrapFilePath = Path.Combine(_fixedApplicationDataDirectory, BootstrapFileName);
        _writer = writer ?? new AtomicSettingsFileWriter();
    }

    public string BootstrapFilePath => _bootstrapFilePath;

    public static ApplicationSettingsStore ForCurrentUser()
    {
        var localApplicationDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationDataDirectory))
        {
            throw new InvalidOperationException("The LocalAppData directory could not be resolved.");
        }

        return new ApplicationSettingsStore(localApplicationDataDirectory);
    }

    public ApplicationSettingsLoadResult Load()
    {
        var defaults = ApplicationSettings.CreateDefault(_localApplicationDataDirectory);
        var settingsDirectory = defaults.SettingsDirectory;
        var bootstrapProblem = default(string);

        if (File.Exists(_bootstrapFilePath))
        {
            try
            {
                var bootstrap = Deserialize<ApplicationSettingsBootstrap>(
                    File.ReadAllBytes(_bootstrapFilePath));
                if (bootstrap.SchemaVersion != BootstrapSchemaVersion
                    || string.IsNullOrWhiteSpace(bootstrap.SettingsDirectory)
                    || !Path.IsPathFullyQualified(bootstrap.SettingsDirectory))
                {
                    throw new InvalidDataException("The settings-location bootstrap is invalid.");
                }

                settingsDirectory = Path.GetFullPath(bootstrap.SettingsDirectory);
            }
            catch (Exception exception) when (IsControlledReadFailure(exception))
            {
                bootstrapProblem =
                    $"The settings-location bootstrap could not be read ({exception.Message}). " +
                    "CIA is using default settings for this session.";
            }
        }

        var settingsFilePath = Path.Combine(settingsDirectory, SettingsFileName);
        if (!File.Exists(settingsFilePath))
        {
            var settings = defaults with { SettingsDirectory = settingsDirectory };
            return CreateResult(
                settings,
                bootstrapProblem is null
                    ? ApplicationSettingsReadState.DefaultsBecauseFileMissing
                    : ApplicationSettingsReadState.DefaultsBecauseBootstrapInvalid,
                settingsFilePath,
                bootstrapProblem);
        }

        if (bootstrapProblem is not null)
        {
            return CreateResult(
                defaults,
                ApplicationSettingsReadState.DefaultsBecauseBootstrapInvalid,
                Path.Combine(defaults.SettingsDirectory, SettingsFileName),
                bootstrapProblem);
        }

        try
        {
            var settings = Deserialize<ApplicationSettings>(File.ReadAllBytes(settingsFilePath));
            if (settings.SchemaVersion > ApplicationSettings.CurrentSchemaVersion)
            {
                return CreateResult(
                    defaults with { SettingsDirectory = settingsDirectory },
                    ApplicationSettingsReadState.DefaultsBecauseVersionUnsupported,
                    settingsFilePath,
                    $"Settings schema version {settings.SchemaVersion} is newer than the supported version {ApplicationSettings.CurrentSchemaVersion}.");
            }

            if (settings.SchemaVersion != ApplicationSettings.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Settings schema version {settings.SchemaVersion} is not supported.");
            }

            var normalized = NormalizeAndValidate(settings);
            ValidateManagedPathsOutsideInstallation(normalized);
            if (!PathsEqual(normalized.SettingsDirectory, settingsDirectory))
            {
                throw new InvalidDataException(
                    "The settings document does not match the active settings directory bootstrap.");
            }

            return CreateResult(
                normalized,
                ApplicationSettingsReadState.Loaded,
                settingsFilePath,
                diagnostic: null);
        }
        catch (Exception exception) when (IsControlledReadFailure(exception))
        {
            return CreateResult(
                defaults with { SettingsDirectory = settingsDirectory },
                ApplicationSettingsReadState.DefaultsBecauseSettingsInvalid,
                settingsFilePath,
                $"The application settings file could not be read ({exception.Message}). CIA is using defaults for this session; the source file was not overwritten.");
        }
    }

    public ApplicationSettingsSaveResult Save(ApplicationSettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ApplicationSettings normalized;
        try
        {
            normalized = NormalizeAndValidate(candidate);
            ValidateManagedPathsOutsideInstallation(normalized);
            EnsureCandidateDirectories(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return ApplicationSettingsSaveResult.Failure(exception.Message);
        }

        var activeDirectory = ResolveActiveSettingsDirectory();
        var settingsFilePath = Path.Combine(normalized.SettingsDirectory, SettingsFileName);
        try
        {
            _writer.Write(settingsFilePath, JsonSerializer.SerializeToUtf8Bytes(
                normalized,
                SerializerOptions));

            if (!PathsEqual(activeDirectory, normalized.SettingsDirectory))
            {
                var bootstrap = new ApplicationSettingsBootstrap(
                    BootstrapSchemaVersion,
                    normalized.SettingsDirectory);
                _writer.Write(
                    _bootstrapFilePath,
                    JsonSerializer.SerializeToUtf8Bytes(bootstrap, SerializerOptions));
            }

            return ApplicationSettingsSaveResult.Success(normalized);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return ApplicationSettingsSaveResult.Failure(
                $"Application settings could not be saved: {exception.Message}");
        }
    }

    private ApplicationSettingsLoadResult CreateResult(
        ApplicationSettings settings,
        ApplicationSettingsReadState state,
        string settingsFilePath,
        string? diagnostic)
    {
        var normalized = NormalizeAndValidate(settings);
        ValidateManagedPathsOutsideInstallation(normalized);
        return new ApplicationSettingsLoadResult(
            normalized,
            ApplicationPaths.FromSettings(_localApplicationDataDirectory, normalized),
            state,
            settingsFilePath,
            diagnostic);
    }

    private string ResolveActiveSettingsDirectory()
    {
        if (!File.Exists(_bootstrapFilePath))
        {
            return ApplicationSettings.CreateDefault(_localApplicationDataDirectory).SettingsDirectory;
        }

        try
        {
            var bootstrap = Deserialize<ApplicationSettingsBootstrap>(
                File.ReadAllBytes(_bootstrapFilePath));
            return bootstrap.SchemaVersion == BootstrapSchemaVersion
                   && !string.IsNullOrWhiteSpace(bootstrap.SettingsDirectory)
                   && Path.IsPathFullyQualified(bootstrap.SettingsDirectory)
                ? Path.GetFullPath(bootstrap.SettingsDirectory)
                : ApplicationSettings.CreateDefault(_localApplicationDataDirectory).SettingsDirectory;
        }
        catch (Exception exception) when (IsControlledReadFailure(exception))
        {
            return ApplicationSettings.CreateDefault(_localApplicationDataDirectory).SettingsDirectory;
        }
    }

    private static ApplicationSettings NormalizeAndValidate(ApplicationSettings settings)
    {
        if (settings.SchemaVersion != ApplicationSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Settings schema version {settings.SchemaVersion} is not supported.");
        }

        if (!Enum.IsDefined(settings.PostExportBehavior))
        {
            throw new InvalidDataException("The configured post-export behavior is invalid.");
        }

        if (!ArchiveNestingDepthIsValid(settings.MaximumArchiveNestingDepth))
        {
            throw new InvalidDataException("The archive nesting depth must be at least 1.");
        }

        var temporaryDirectory = NormalizeRequiredPath(
            settings.TemporaryDirectory,
            nameof(settings.TemporaryDirectory));
        var workingDirectory = NormalizeRequiredPath(
            settings.WorkingDirectory,
            nameof(settings.WorkingDirectory));
        var profilesDirectory = NormalizeRequiredPath(
            settings.ProfilesDirectory,
            nameof(settings.ProfilesDirectory));
        var settingsDirectory = NormalizeRequiredPath(
            settings.SettingsDirectory,
            nameof(settings.SettingsDirectory));
        var extractionDirectory = NormalizeOptionalPath(
            settings.PersistentArchiveExtractionDirectory,
            nameof(settings.PersistentArchiveExtractionDirectory));
        var outputDirectory = NormalizeOptionalPath(
            settings.LastUsedOutputDirectory,
            nameof(settings.LastUsedOutputDirectory));

        if (settings.PersistentArchiveExtractionEnabled && extractionDirectory is null)
        {
            throw new InvalidDataException(
                "Persistent archive extraction requires a configured destination.");
        }

        return settings with
        {
            TemporaryDirectory = temporaryDirectory,
            WorkingDirectory = workingDirectory,
            ProfilesDirectory = profilesDirectory,
            SettingsDirectory = settingsDirectory,
            PersistentArchiveExtractionDirectory = extractionDirectory,
            LastUsedOutputDirectory = outputDirectory
        };
    }

    private static bool ArchiveNestingDepthIsValid(
        CIA.Contracts.Sources.ArchiveNestingDepth depth) => depth.Value >= 1;

    private static void EnsureCandidateDirectories(ApplicationSettings settings)
    {
        Directory.CreateDirectory(settings.SettingsDirectory);
        Directory.CreateDirectory(settings.TemporaryDirectory);
        Directory.CreateDirectory(settings.WorkingDirectory);
        Directory.CreateDirectory(settings.ProfilesDirectory);
        if (settings.PersistentArchiveExtractionEnabled)
        {
            Directory.CreateDirectory(settings.PersistentArchiveExtractionDirectory!);
        }
    }

    private void ValidateManagedPathsOutsideInstallation(ApplicationSettings settings)
    {
        var installedDirectory = Path.Combine(
            _localApplicationDataDirectory,
            "Programs",
            ApplicationPaths.ApplicationDirectoryName);
        foreach (var managedPath in new[]
                 {
                     settings.TemporaryDirectory,
                     settings.WorkingDirectory,
                     settings.ProfilesDirectory,
                     settings.SettingsDirectory,
                     settings.PersistentArchiveExtractionDirectory
                 }.Where(path => path is not null))
        {
            if (IsWithin(managedPath!, installedDirectory))
            {
                throw new InvalidDataException(
                    "CIA-managed writable data cannot be stored inside the installed-binary directory.");
            }
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var resolvedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return resolvedPath.Equals(resolvedDirectory, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(
                resolvedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRequiredPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"{parameterName} must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string? NormalizeOptionalPath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return NormalizeRequiredPath(path, parameterName);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static T Deserialize<T>(byte[] content)
    {
        return JsonSerializer.Deserialize<T>(content, SerializerOptions)
            ?? throw new InvalidDataException("The JSON document was empty.");
    }

    private static bool IsControlledReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record ApplicationSettingsBootstrap(
        int SchemaVersion,
        string SettingsDirectory);
}

public sealed class ApplicationSettingsService
{
    private readonly ApplicationSettingsStore _store;

    public ApplicationSettingsService(ApplicationSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Startup = store.Load();
        Current = Startup.Settings;
    }

    public ApplicationSettingsLoadResult Startup { get; }

    public ApplicationSettings Current { get; private set; }

    public ApplicationPaths RuntimePaths => Startup.RuntimePaths;

    public string CurrentSettingsFilePath => Path.Combine(
        Current.SettingsDirectory,
        ApplicationSettingsStore.SettingsFileName);

    public bool IsRestartRequired =>
        !PathsEqual(Current.TemporaryDirectory, RuntimePaths.TempDirectory)
        || !PathsEqual(Current.WorkingDirectory, RuntimePaths.WorkingDirectory)
        || !PathsEqual(Current.ProfilesDirectory, RuntimePaths.ProfilesDirectory)
        || !PathsEqual(Current.SettingsDirectory, RuntimePaths.SettingsDirectory);

    public static ApplicationSettingsService ForCurrentUser() =>
        new(ApplicationSettingsStore.ForCurrentUser());

    public ApplicationSettingsSaveResult Save(ApplicationSettings candidate)
    {
        var result = _store.Save(candidate);
        if (result.Succeeded)
        {
            Current = result.Settings!;
        }

        return result;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
