using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Core.ManagedStorage;

public enum ManagedStorageMetadataReadState
{
    Valid = 1,
    Missing = 2,
    Malformed = 3,
    UnsupportedFutureVersion = 4,
    PathMismatch = 5,
    ReparsePoint = 6
}

public sealed record ManagedStorageMetadataReadResult(
    ManagedStorageMetadataReadState State,
    ManagedStorageOwnershipMetadata? Metadata,
    string? Problem);

public sealed class ManagedStorageOwnershipMetadataStore
{
    public const string FileName = ".cia-managed-artifact.json";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public void Write(string artifactDirectory, ManagedStorageOwnershipMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(metadata);
        var canonicalDirectory = Canonicalize(artifactDirectory);
        Validate(metadata, canonicalDirectory);

        var metadataPath = Path.Combine(canonicalDirectory, FileName);
        using var stream = new FileStream(
            metadataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, metadata, SerializerOptions);
        stream.Flush(flushToDisk: true);
    }

    public ManagedStorageMetadataReadResult Read(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        string canonicalDirectory;
        try
        {
            canonicalDirectory = Canonicalize(artifactDirectory);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Malformed,
                null,
                $"The artifact path is invalid ({exception.Message}).");
        }

        var metadataPath = Path.Combine(canonicalDirectory, FileName);
        if (!File.Exists(metadataPath))
        {
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Missing,
                null,
                "The artifact has no CIA ownership metadata.");
        }

        try
        {
            if ((File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
            {
                return new ManagedStorageMetadataReadResult(
                    ManagedStorageMetadataReadState.ReparsePoint,
                    null,
                    "The ownership metadata file is a reparse point and was not followed.");
            }
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Malformed,
                null,
                $"The ownership metadata could not be inspected ({exception.Message}).");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var versionProperty)
                || !versionProperty.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException("The metadata schema version is missing or invalid.");
            }

            if (schemaVersion > ManagedStorageOwnershipMetadata.CurrentSchemaVersion)
            {
                return new ManagedStorageMetadataReadResult(
                    ManagedStorageMetadataReadState.UnsupportedFutureVersion,
                    null,
                    $"Ownership metadata schema version {schemaVersion} is newer than the supported version {ManagedStorageOwnershipMetadata.CurrentSchemaVersion}.");
            }

            if (schemaVersion != ManagedStorageOwnershipMetadata.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Ownership metadata schema version {schemaVersion} is not supported.");
            }

            var metadata = document.RootElement.Deserialize<ManagedStorageOwnershipMetadata>(
                    SerializerOptions)
                ?? throw new InvalidDataException("The ownership metadata document was empty.");
            Validate(metadata, canonicalDirectory);
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Valid,
                metadata,
                null);
        }
        catch (ManagedStoragePathMismatchException exception)
        {
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.PathMismatch,
                null,
                exception.Message);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return new ManagedStorageMetadataReadResult(
                ManagedStorageMetadataReadState.Malformed,
                null,
                $"The ownership metadata could not be read ({exception.Message}).");
        }
    }

    private static void Validate(
        ManagedStorageOwnershipMetadata metadata,
        string canonicalDirectory)
    {
        if (metadata.SchemaVersion != ManagedStorageOwnershipMetadata.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The ownership metadata schema version is invalid.");
        }

        _ = ManagedStorageArtifactId.From(metadata.ArtifactId.Value);
        if (!Enum.IsDefined(metadata.ArtifactKind)
            || metadata.ArtifactKind == ManagedStorageArtifactKind.UnknownUnsafe
            || !Enum.IsDefined(metadata.Lifecycle)
            || metadata.Lifecycle == ManagedStorageLifecycle.Unknown)
        {
            throw new InvalidDataException("The ownership metadata classification is invalid.");
        }

        if (metadata.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The ownership creation timestamp must be UTC.");
        }

        if (metadata.OperationId is { } operationId)
        {
            _ = OperationId.From(operationId.Value);
        }

        if (metadata.SourceId is { } sourceId)
        {
            _ = SourceId.From(sourceId.Value);
        }

        if (metadata.SourceSetId is { } sourceSetId)
        {
            _ = SourceSetId.From(sourceSetId.Value);
        }

        if (metadata.ArtifactKind == ManagedStorageArtifactKind.TemporaryOperationArtifact
            && metadata.OperationId is null)
        {
            throw new InvalidDataException("Operation-owned temporary artifacts require an Operation ID.");
        }

        if (metadata.ArtifactKind is ManagedStorageArtifactKind.WorkingStateStagingCandidate
                or ManagedStorageArtifactKind.ExportStagingCandidate
            && metadata.OperationId is null)
        {
            throw new InvalidDataException("Staging artifacts require an owning Operation ID.");
        }

        if (metadata.ArtifactKind == ManagedStorageArtifactKind.ManagedTemporaryArchiveExtraction
            && metadata.SourceId is null)
        {
            throw new InvalidDataException("Managed temporary archive extractions require a source identity.");
        }

        var declaredPath = Canonicalize(metadata.CanonicalArtifactPath);
        if (!PathsEqual(declaredPath, canonicalDirectory))
        {
            throw new ManagedStoragePathMismatchException(
                "The ownership metadata path does not match the artifact being inspected.");
        }
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Managed-storage paths must be absolute.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool IsControlledFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ManagedStoragePathMismatchException
            or ArgumentException
            or NotSupportedException;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class ManagedStoragePathMismatchException(string message)
        : Exception(message);
}
