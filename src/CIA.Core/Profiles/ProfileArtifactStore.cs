using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Core.Runtime;

namespace CIA.Core.Profiles;

public interface IProfileArtifactFileOperations
{
    void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content);

    void PublishNew(string candidatePath, string targetPath);

    void Replace(string candidatePath, string targetPath);

    void DeleteCandidate(string candidatePath);
}

public sealed class ProfileArtifactFileOperations : IProfileArtifactFileOperations
{
    public void WriteCandidate(string candidatePath, ReadOnlyMemory<byte> content)
    {
        using var stream = new FileStream(
            candidatePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
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
}

public sealed class ProfileArtifactStore
{
    public const string FileExtension = ".cia-profile.json";
    public const string PublicationLockFileName = ".cia-profile-store.lock";
    public const long MaximumArtifactBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan DefaultPublicationLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PublicationLockRetryInterval = TimeSpan.FromMilliseconds(20);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _profilesDirectory;
    private readonly IProfileArtifactFileOperations _fileOperations;
    private readonly TimeSpan _publicationLockTimeout;

    public ProfileArtifactStore(
        ApplicationPaths applicationPaths,
        IProfileArtifactFileOperations? fileOperations = null,
        TimeSpan? publicationLockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _profilesDirectory = Canonicalize(applicationPaths.ProfilesDirectory);
        _fileOperations = fileOperations ?? new ProfileArtifactFileOperations();
        _publicationLockTimeout = publicationLockTimeout ?? DefaultPublicationLockTimeout;
        if (_publicationLockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationLockTimeout),
                "The profile publication-lock timeout must be positive.");
        }
    }

    public string ProfilesDirectory => _profilesDirectory;

    public ProfileArtifactInventory CreateInventory()
    {
        if (!Directory.Exists(_profilesDirectory))
        {
            return new ProfileArtifactInventory([]);
        }

        try
        {
            EnsureSafeProfilesRoot();
            var inspections = Directory
                .EnumerateFiles(_profilesDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(HasProfileExtension)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Inspect)
                .ToArray();
            var duplicateIds = inspections
                .Where(item => item.State == ProfileArtifactReadState.Valid)
                .GroupBy(item => item.Artifact!.ProfileId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            if (duplicateIds.Count == 0)
            {
                return new ProfileArtifactInventory(inspections);
            }

            return new ProfileArtifactInventory(inspections
                .Select(item => item.Artifact is { } artifact
                                && duplicateIds.Contains(artifact.ProfileId)
                    ? item with
                    {
                        State = ProfileArtifactReadState.AmbiguousProfileId,
                        Problem = "Multiple profile files declare the same Profile ID."
                    }
                    : item)
                .ToArray());
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return new ProfileArtifactInventory(
            [
                new ProfileArtifactInspection(
                    _profilesDirectory,
                    ProfileArtifactReadState.UnsafePath,
                    Artifact: null,
                    $"The Profiles directory could not be inspected safely ({exception.Message}).")
            ]);
        }
    }

    public ProfileArtifactInspection Inspect(string path)
    {
        try
        {
            var fullPath = ValidateProfilePath(path, requireProfileExtension: true);
            return InspectCore(fullPath);
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return new ProfileArtifactInspection(
                SafeDisplayPath(path),
                ProfileArtifactReadState.UnsafePath,
                Artifact: null,
                exception.Message);
        }
    }

    public ProfileArtifactInspection FindById(ProfileId profileId)
    {
        _ = ProfileId.From(profileId.Value);
        var matches = CreateInventory().Items
            .Where(item => item.Artifact?.ProfileId == profileId)
            .ToArray();
        if (matches.Length == 0)
        {
            return new ProfileArtifactInspection(
                _profilesDirectory,
                ProfileArtifactReadState.NotFound,
                Artifact: null,
                "No profile file declares the requested Profile ID.");
        }

        if (matches.Length > 1
            || matches.Any(item => item.State == ProfileArtifactReadState.AmbiguousProfileId))
        {
            return new ProfileArtifactInspection(
                _profilesDirectory,
                ProfileArtifactReadState.AmbiguousProfileId,
                matches[0].Artifact,
                "Multiple profile files declare the requested Profile ID.");
        }

        return matches[0];
    }

    public ProfileArtifactWriteResult Create(
        string targetFileName,
        ProfileArtifactV1 artifact)
    {
        try
        {
            ProfileArtifactValidator.Validate(artifact);
            EnsureProfilesDirectoryForWrite();
            var targetPath = ResolveTargetFileName(targetFileName);
            using var publicationLock = AcquirePublicationLock();
            if (File.Exists(targetPath))
            {
                throw new IOException("The target profile file already exists.");
            }

            var existing = FindById(artifact.ProfileId);
            if (existing.State is ProfileArtifactReadState.Valid
                or ProfileArtifactReadState.AmbiguousProfileId)
            {
                throw new InvalidDataException(
                    "A profile file already declares the requested Profile ID.");
            }

            return WriteCandidateAndPublish(targetPath, artifact, replacement: null);
        }
        catch (Exception exception) when (IsControlledWriteFailure(exception))
        {
            return ProfileArtifactWriteResult.Failure(
                $"The profile could not be created safely ({exception.Message}).");
        }
    }

    public ProfileArtifactWriteResult Update(
        ProfileArtifactV1 replacement,
        ProfileArtifactFingerprint expectedCurrentFingerprint)
    {
        try
        {
            ProfileArtifactValidator.Validate(replacement);
            if (!ProfileArtifactFingerprint.IsValid(expectedCurrentFingerprint.Value))
            {
                throw new InvalidDataException(
                    "A valid current-profile fingerprint is required for replacement.");
            }

            EnsureProfilesDirectoryForWrite();
            using var publicationLock = AcquirePublicationLock();
            var existing = FindById(replacement.ProfileId);
            if (existing.State != ProfileArtifactReadState.Valid
                || existing.Artifact is null
                || existing.Fingerprint is not { } authoritativeFingerprint)
            {
                throw new InvalidDataException(existing.Problem ?? "The profile is not available for update.");
            }

            if (authoritativeFingerprint != expectedCurrentFingerprint)
            {
                throw new InvalidDataException(
                    "The profile changed after it was read; reload it before saving the replacement.");
            }

            ProfileArtifactValidator.ValidateReplacement(existing.Artifact, replacement);
            var targetPath = ValidateProfilePath(existing.Path, requireProfileExtension: true);
            return WriteCandidateAndPublish(
                targetPath,
                replacement,
                existing.Artifact,
                authoritativeFingerprint);
        }
        catch (Exception exception) when (IsControlledWriteFailure(exception))
        {
            return ProfileArtifactWriteResult.Failure(
                $"The profile could not be replaced safely ({exception.Message}).");
        }
    }

    public static byte[] Serialize(ProfileArtifactV1 artifact)
    {
        ProfileArtifactValidator.Validate(artifact);
        return JsonSerializer.SerializeToUtf8Bytes(artifact, SerializerOptions);
    }

    private ProfileArtifactWriteResult WriteCandidateAndPublish(
        string targetPath,
        ProfileArtifactV1 artifact,
        ProfileArtifactV1? replacement,
        ProfileArtifactFingerprint? expectedCurrentFingerprint = null)
    {
        var candidatePath = Path.Combine(
            _profilesDirectory,
            $".cia-profile-{Guid.CreateVersion7():N}.incomplete");
        try
        {
            var expectedBytes = Serialize(artifact);
            _fileOperations.WriteCandidate(candidatePath, expectedBytes);
            var candidate = InspectCore(
                ValidateCandidatePath(candidatePath),
                requireProfileExtension: false);
            if (candidate.State != ProfileArtifactReadState.Valid || candidate.Artifact is null)
            {
                throw new InvalidDataException(
                    candidate.Problem ?? "The candidate profile could not be validated.");
            }

            var validatedBytes = Serialize(candidate.Artifact);
            if (!expectedBytes.AsSpan().SequenceEqual(validatedBytes))
            {
                throw new InvalidDataException(
                    "The reread candidate profile does not match the requested artifact.");
            }

            _ = ValidateProfilePath(targetPath, requireProfileExtension: true);
            if (replacement is null)
            {
                if (File.Exists(targetPath))
                {
                    throw new IOException("The target profile file appeared before publication.");
                }

                _fileOperations.PublishNew(candidatePath, targetPath);
            }
            else
            {
                var current = InspectCore(targetPath);
                if (current.State != ProfileArtifactReadState.Valid
                    || current.Artifact?.ProfileId != replacement.ProfileId
                    || current.Fingerprint != expectedCurrentFingerprint)
                {
                    throw new InvalidDataException(
                        "The existing profile changed before replacement publication.");
                }

                _fileOperations.Replace(candidatePath, targetPath);
            }

            var published = InspectCore(targetPath);
            if (published.State != ProfileArtifactReadState.Valid
                || published.Artifact?.ProfileId != artifact.ProfileId)
            {
                throw new InvalidDataException("The published profile could not be verified.");
            }

            return ProfileArtifactWriteResult.Success(
                targetPath,
                published.Artifact,
                published.Fingerprint!.Value);
        }
        finally
        {
            try
            {
                _fileOperations.DeleteCandidate(candidatePath);
            }
            catch (Exception exception) when (IsControlledFileFailure(exception))
            {
                // Incomplete files are deliberately ignored by inventory and are never valid profiles.
            }
        }
    }

    private ProfileArtifactInspection InspectCore(
        string path,
        bool requireProfileExtension = true)
    {
        var fullPath = requireProfileExtension
            ? ValidateProfilePath(path, requireProfileExtension: true)
            : ValidateCandidatePath(path);
        if (!File.Exists(fullPath))
        {
            return new ProfileArtifactInspection(
                fullPath,
                ProfileArtifactReadState.NotFound,
                Artifact: null,
                "The profile file does not exist.");
        }

        try
        {
            EnsureNotReparsePoint(fullPath, "Profile files cannot be reparse points.");
            var fileLength = new FileInfo(fullPath).Length;
            if (fileLength is <= 0 or > MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"A profile artifact must contain 1 to {MaximumArtifactBytes} bytes.");
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
                throw new InvalidDataException("A profile artifact requires an integer schemaVersion.");
            }

            if (schemaVersion > ProfileArtifactV1.CurrentSchemaVersion)
            {
                return new ProfileArtifactInspection(
                    fullPath,
                    ProfileArtifactReadState.UnsupportedSchema,
                    Artifact: null,
                    $"Profile schema version {schemaVersion} is newer than supported schema version {ProfileArtifactV1.CurrentSchemaVersion}.");
            }

            var artifact = JsonSerializer.Deserialize<ProfileArtifactV1>(bytes, SerializerOptions)
                ?? throw new InvalidDataException("The profile artifact is empty.");
            ProfileArtifactValidator.Validate(artifact);
            return new ProfileArtifactInspection(
                fullPath,
                ProfileArtifactReadState.Valid,
                artifact,
                Problem: null)
            {
                Fingerprint = ProfileArtifactFingerprint.FromBytes(bytes)
            };
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return new ProfileArtifactInspection(
                fullPath,
                ProfileArtifactReadState.MalformedOrInvalid,
                Artifact: null,
                $"The profile artifact is malformed or invalid ({exception.Message}).");
        }
    }

    private string ResolveTargetFileName(string targetFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFileName);
        if (!string.Equals(Path.GetFileName(targetFileName), targetFileName, StringComparison.Ordinal)
            || targetFileName.Length == FileExtension.Length
            || !HasProfileExtension(targetFileName))
        {
            throw new ArgumentException(
                $"A profile target must be a direct file in ProfilesDirectory ending with {FileExtension}.",
                nameof(targetFileName));
        }

        return ValidateProfilePath(
            Path.Combine(_profilesDirectory, targetFileName),
            requireProfileExtension: true);
    }

    private string ValidateProfilePath(string path, bool requireProfileExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureSafeProfilesRoot();
        var fullPath = Canonicalize(path);
        if (!PathsEqual(Path.GetDirectoryName(fullPath), _profilesDirectory))
        {
            throw new InvalidDataException(
                "Profile artifacts must be direct files inside the configured Profiles directory.");
        }

        if (requireProfileExtension && !HasProfileExtension(fullPath))
        {
            throw new InvalidDataException(
                $"Profile artifacts must use the {FileExtension} extension.");
        }

        if (File.Exists(fullPath))
        {
            EnsureNotReparsePoint(fullPath, "Profile files cannot be reparse points.");
        }

        return fullPath;
    }

    private string ValidateCandidatePath(string candidatePath)
    {
        var fullPath = ValidateProfilePath(candidatePath, requireProfileExtension: false);
        if (!Path.GetFileName(fullPath).EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Profile candidates must use the incomplete suffix.");
        }

        return fullPath;
    }

    private void EnsureProfilesDirectoryForWrite()
    {
        if (File.Exists(_profilesDirectory))
        {
            throw new InvalidDataException("The configured Profiles location is not a directory.");
        }

        Directory.CreateDirectory(_profilesDirectory);
        EnsureSafeProfilesRoot();
    }

    private ProfileStorePublicationLease AcquirePublicationLock()
    {
        var lockPath = ValidateProfilePath(
            Path.Combine(_profilesDirectory, PublicationLockFileName),
            requireProfileExtension: false);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                EnsureNotReparsePoint(
                    lockPath,
                    "The profile publication lock cannot be a reparse point.");
                return new ProfileStorePublicationLease(stream);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                stream?.Dispose();
                if (stopwatch.Elapsed >= _publicationLockTimeout)
                {
                    throw new IOException(
                        "Timed out waiting for exclusive profile publication ownership.",
                        exception);
                }

                Thread.Sleep(PublicationLockRetryInterval);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    private void EnsureSafeProfilesRoot()
    {
        if (File.Exists(_profilesDirectory))
        {
            throw new InvalidDataException("The configured Profiles location is not a directory.");
        }

        if (Directory.Exists(_profilesDirectory))
        {
            EnsureNotReparsePoint(
                _profilesDirectory,
                "The configured Profiles directory cannot be a reparse point.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
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

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static bool HasProfileExtension(string path) =>
        path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase);

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string? first, string second) =>
        first is not null
        && string.Equals(
            Canonicalize(first),
            Canonicalize(second),
            StringComparison.OrdinalIgnoreCase);

    private static string SafeDisplayPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsControlledFileFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException;

    private static bool IsControlledWriteFailure(Exception exception) =>
        IsControlledFileFailure(exception)
        || exception is JsonException;

    private static bool IsSharingViolation(IOException exception) =>
        (uint)exception.HResult is 0x80070020 or 0x80070021;

    private sealed class ProfileStorePublicationLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
    }
}
