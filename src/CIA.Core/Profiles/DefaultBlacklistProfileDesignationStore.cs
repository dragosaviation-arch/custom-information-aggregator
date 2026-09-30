using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Core.Runtime;

namespace CIA.Core.Profiles;

public sealed record DefaultBlacklistProfileDesignationV1(
    int SchemaVersion,
    ProfileId ProfileId)
{
    public const int CurrentSchemaVersion = 1;
}

public enum DefaultBlacklistProfileDesignationState
{
    NotConfigured = 1,
    Valid = 2,
    MalformedOrInvalid = 3,
    UnsupportedSchema = 4,
    UnsafePath = 5
}

public sealed record DefaultBlacklistProfileDesignationReadResult(
    DefaultBlacklistProfileDesignationState State,
    ProfileId? ProfileId,
    string? Problem);

public sealed record DefaultBlacklistProfileDesignationWriteResult(
    bool Succeeded,
    ProfileId? ProfileId,
    string Message);

public interface IDefaultBlacklistProfileDesignationFileOperations
{
    void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content);

    void PublishNew(string candidatePath, string targetPath);

    void Replace(string candidatePath, string targetPath);

    void DeleteCandidate(string candidatePath);

    void DeleteDesignation(string targetPath);
}

public sealed class DefaultBlacklistProfileDesignationFileOperations :
    IDefaultBlacklistProfileDesignationFileOperations
{
    public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content)
    {
        using var stream = new FileStream(
            candidatePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(content.Span);
        stream.Flush(flushToDisk: true);
    }

    public void PublishNew(string candidatePath, string targetPath) =>
        File.Move(candidatePath, targetPath, overwrite: false);

    public void Replace(string candidatePath, string targetPath) =>
        File.Replace(candidatePath, targetPath, destinationBackupFileName: null);

    public void DeleteCandidate(string candidatePath)
    {
        if (File.Exists(candidatePath))
        {
            File.Delete(candidatePath);
        }
    }

    public void DeleteDesignation(string targetPath) => File.Delete(targetPath);
}

public sealed class DefaultBlacklistProfileDesignationStore
{
    public const string FileName = ".cia-default-blacklist.json";
    public const long MaximumArtifactBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _profilesDirectory;
    private readonly string _designationPath;
    private readonly IDefaultBlacklistProfileDesignationFileOperations _fileOperations;
    private readonly ProfileStorePublicationGate _publicationGate;

    public DefaultBlacklistProfileDesignationStore(
        ApplicationPaths applicationPaths,
        IDefaultBlacklistProfileDesignationFileOperations? fileOperations = null,
        ProfileStorePublicationGate? publicationGate = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _profilesDirectory = Canonicalize(applicationPaths.ProfilesDirectory);
        _designationPath = Path.Combine(_profilesDirectory, FileName);
        _fileOperations = fileOperations
            ?? new DefaultBlacklistProfileDesignationFileOperations();
        _publicationGate = publicationGate
            ?? new ProfileStorePublicationGate(applicationPaths);
        if (!string.Equals(
                _publicationGate.ProfilesDirectory,
                _profilesDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The publication gate must target the same Profiles directory as the designation store.",
                nameof(publicationGate));
        }
    }

    public string DesignationPath => _designationPath;

    public string ProfilesDirectory => _profilesDirectory;

    public DefaultBlacklistProfileDesignationReadResult Read()
    {
        try
        {
            EnsureSafeProfilesRoot(createIfMissing: false);
            return ReadCore(_designationPath, candidate: false);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationReadResult(
                DefaultBlacklistProfileDesignationState.UnsafePath,
                ProfileId: null,
                $"The default blacklist designation could not be read safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationWriteResult Set(ProfileId profileId)
    {
        try
        {
            EnsureSafeProfilesRoot(createIfMissing: true);
            using var publicationLock = _publicationGate.Acquire();
            return Set(profileId, publicationLock);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationWriteResult(
                false,
                ProfileId: null,
                $"The default blacklist designation could not be saved safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationWriteResult Set(
        ProfileId profileId,
        ProfileStorePublicationLease publicationLease)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(publicationLease);
            publicationLease.VerifyFor(_profilesDirectory);
            _ = ProfileId.From(profileId.Value);
            EnsureSafeProfilesRoot(createIfMissing: true);
            var existing = ReadCore(_designationPath, candidate: false);
            if (existing.State is not (DefaultBlacklistProfileDesignationState.NotConfigured
                or DefaultBlacklistProfileDesignationState.Valid))
            {
                throw new InvalidDataException(
                    existing.Problem
                    ?? "The existing default blacklist designation is not safe to replace.");
            }

            var designation = new DefaultBlacklistProfileDesignationV1(
                DefaultBlacklistProfileDesignationV1.CurrentSchemaVersion,
                profileId);
            var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(
                designation,
                SerializerOptions);
            var candidatePath = Path.Combine(
                _profilesDirectory,
                $".cia-default-blacklist-{Guid.CreateVersion7():N}.incomplete");
            try
            {
                _fileOperations.WriteCandidate(candidatePath, expectedBytes);
                var candidate = ReadCore(candidatePath, candidate: true);
                if (candidate.State != DefaultBlacklistProfileDesignationState.Valid
                    || candidate.ProfileId != profileId)
                {
                    throw new InvalidDataException(
                        candidate.Problem
                        ?? "The candidate default blacklist designation is invalid.");
                }

                if (existing.State == DefaultBlacklistProfileDesignationState.Valid)
                {
                    EnsureSafeDirectFile(_designationPath);
                    _fileOperations.Replace(candidatePath, _designationPath);
                }
                else
                {
                    _fileOperations.PublishNew(candidatePath, _designationPath);
                }

                var published = ReadCore(_designationPath, candidate: false);
                if (published.State != DefaultBlacklistProfileDesignationState.Valid
                    || published.ProfileId != profileId)
                {
                    throw new InvalidDataException(
                        "The published default blacklist designation could not be verified.");
                }

                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    profileId,
                    "The default blacklist profile was designated.");
            }
            finally
            {
                try
                {
                    _fileOperations.DeleteCandidate(candidatePath);
                }
                catch (Exception exception) when (IsControlledFailure(exception))
                {
                    // Candidate files are never interpreted as designations or profiles.
                }
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationWriteResult(
                false,
                ProfileId: null,
                $"The default blacklist designation could not be saved safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationWriteResult Clear()
    {
        try
        {
            EnsureSafeProfilesRoot(createIfMissing: false);
            if (!Directory.Exists(_profilesDirectory))
            {
                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    ProfileId: null,
                    "No default blacklist profile is configured.");
            }

            using var publicationLock = _publicationGate.Acquire();
            return Clear(publicationLock);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationWriteResult(
                false,
                ProfileId: null,
                $"The default blacklist designation could not be cleared safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationWriteResult Clear(
        ProfileStorePublicationLease publicationLease)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(publicationLease);
            publicationLease.VerifyFor(_profilesDirectory);
            EnsureSafeProfilesRoot(createIfMissing: false);
            if (!Directory.Exists(_profilesDirectory))
            {
                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    ProfileId: null,
                    "No default blacklist profile is configured.");
            }

            var existing = ReadCore(_designationPath, candidate: false);
            if (existing.State == DefaultBlacklistProfileDesignationState.NotConfigured)
            {
                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    ProfileId: null,
                    "No default blacklist profile is configured.");
            }

            if (existing.State != DefaultBlacklistProfileDesignationState.Valid)
            {
                throw new InvalidDataException(
                    existing.Problem
                    ?? "The existing default blacklist designation is invalid and was not removed.");
            }

            return DeleteDesignation();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationWriteResult(
                false,
                ProfileId: null,
                $"The default blacklist designation could not be cleared safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationWriteResult ClearIfMatches(
        ProfileId expectedProfileId,
        ProfileStorePublicationLease publicationLease)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(publicationLease);
            publicationLease.VerifyFor(_profilesDirectory);
            _ = ProfileId.From(expectedProfileId.Value);
            EnsureSafeProfilesRoot(createIfMissing: false);
            var existing = ReadCore(_designationPath, candidate: false);
            if (existing.State == DefaultBlacklistProfileDesignationState.NotConfigured)
            {
                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    ProfileId: null,
                    "No default blacklist profile is configured.");
            }

            if (existing.State != DefaultBlacklistProfileDesignationState.Valid
                || existing.ProfileId is not { } currentProfileId)
            {
                throw new InvalidDataException(
                    existing.Problem
                    ?? "The existing default blacklist designation is invalid and was not removed.");
            }

            if (currentProfileId != expectedProfileId)
            {
                return new DefaultBlacklistProfileDesignationWriteResult(
                    true,
                    currentProfileId,
                    "The default blacklist designation changed and was not cleared.");
            }

            return DeleteDesignation();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationWriteResult(
                false,
                ProfileId: null,
                $"The default blacklist designation could not be cleared safely ({exception.Message}).");
        }
    }

    public DefaultBlacklistProfileDesignationReadResult Read(
        ProfileStorePublicationLease publicationLease)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(publicationLease);
            publicationLease.VerifyFor(_profilesDirectory);
            EnsureSafeProfilesRoot(createIfMissing: false);
            return ReadCore(_designationPath, candidate: false);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new DefaultBlacklistProfileDesignationReadResult(
                DefaultBlacklistProfileDesignationState.UnsafePath,
                ProfileId: null,
                $"The default blacklist designation could not be read safely ({exception.Message}).");
        }
    }

    private DefaultBlacklistProfileDesignationWriteResult DeleteDesignation()
    {
        EnsureSafeDirectFile(_designationPath);
        _fileOperations.DeleteDesignation(_designationPath);
        if (File.Exists(_designationPath))
        {
            throw new IOException("The default blacklist designation still exists after deletion.");
        }

        return new DefaultBlacklistProfileDesignationWriteResult(
            true,
            ProfileId: null,
            "The default blacklist designation was cleared.");
    }

    private DefaultBlacklistProfileDesignationReadResult ReadCore(
        string path,
        bool candidate)
    {
        var fullPath = ValidateDirectPath(path, candidate);
        if (!File.Exists(fullPath))
        {
            return new DefaultBlacklistProfileDesignationReadResult(
                DefaultBlacklistProfileDesignationState.NotConfigured,
                ProfileId: null,
                Problem: null);
        }

        try
        {
            EnsureSafeDirectFile(fullPath);
            var length = new FileInfo(fullPath).Length;
            if (length is <= 0 or > MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"A default designation must contain 1 to {MaximumArtifactBytes} bytes.");
            }

            var bytes = File.ReadAllBytes(fullPath);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = SerializerOptions.MaxDepth
            });
            ValidateNoDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                || !schemaElement.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException(
                    "A default blacklist designation requires an integer schemaVersion.");
            }

            if (schemaVersion > DefaultBlacklistProfileDesignationV1.CurrentSchemaVersion)
            {
                return new DefaultBlacklistProfileDesignationReadResult(
                    DefaultBlacklistProfileDesignationState.UnsupportedSchema,
                    ProfileId: null,
                    $"Default blacklist designation schema version {schemaVersion} is newer than supported schema version {DefaultBlacklistProfileDesignationV1.CurrentSchemaVersion}.");
            }

            var designation = JsonSerializer.Deserialize<DefaultBlacklistProfileDesignationV1>(
                    bytes,
                    SerializerOptions)
                ?? throw new InvalidDataException("The default blacklist designation is empty.");
            if (designation.SchemaVersion
                != DefaultBlacklistProfileDesignationV1.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Default blacklist designation schema version {designation.SchemaVersion} is not supported.");
            }

            _ = ProfileId.From(designation.ProfileId.Value);
            return new DefaultBlacklistProfileDesignationReadResult(
                DefaultBlacklistProfileDesignationState.Valid,
                designation.ProfileId,
                Problem: null);
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return new DefaultBlacklistProfileDesignationReadResult(
                DefaultBlacklistProfileDesignationState.MalformedOrInvalid,
                ProfileId: null,
                $"The default blacklist designation is malformed or invalid ({exception.Message}).");
        }
    }

    private string ValidateDirectPath(string path, bool candidate)
    {
        EnsureSafeProfilesRoot(createIfMissing: false);
        var fullPath = Canonicalize(path);
        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                _profilesDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Default blacklist designation files must remain directly inside ProfilesDirectory.");
        }

        var fileName = Path.GetFileName(fullPath);
        if (candidate)
        {
            if (!fileName.StartsWith(
                    ".cia-default-blacklist-",
                    StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Default blacklist designation candidates require the controlled incomplete filename.");
            }
        }
        else if (!string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The default blacklist designation filename is not supported.");
        }

        return fullPath;
    }

    private void EnsureSafeProfilesRoot(bool createIfMissing)
    {
        if (File.Exists(_profilesDirectory))
        {
            throw new InvalidDataException("The configured Profiles location is not a directory.");
        }

        if (createIfMissing)
        {
            Directory.CreateDirectory(_profilesDirectory);
        }

        if (Directory.Exists(_profilesDirectory)
            && (File.GetAttributes(_profilesDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The configured Profiles directory cannot be a reparse point.");
        }
    }

    private static void EnsureSafeDirectFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Default blacklist designation files cannot be reparse points.");
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The JSON property '{property.Name}' is duplicated.");
                }

                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or JsonException;

}
