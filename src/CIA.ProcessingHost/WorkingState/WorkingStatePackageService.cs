using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.WorkingState;
using CIA.Core.Runtime;
using CIA.Core.WorkingState;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.WorkingState;

public sealed class WorkingStatePackageService
{
    private const string OperationItemId = "working-state-package";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 128,
        WriteIndented = true
    };
    private readonly StructuredInformationRepository _repository;
    private readonly ApplicationPaths _applicationPaths;
    private readonly CooperativeOperationCancellation _operationCancellation;
    private readonly ILogger<WorkingStatePackageService> _logger;
    private readonly IWorkingStatePackagePublisher _publisher;

    public WorkingStatePackageService(
        StructuredInformationRepository repository,
        ApplicationPaths applicationPaths,
        CooperativeOperationCancellation operationCancellation,
        ILogger<WorkingStatePackageService> logger)
        : this(
            repository,
            applicationPaths,
            operationCancellation,
            logger,
            FileSystemWorkingStatePackagePublisher.Instance)
    {
    }

    internal WorkingStatePackageService(
        StructuredInformationRepository repository,
        ApplicationPaths applicationPaths,
        CooperativeOperationCancellation operationCancellation,
        ILogger<WorkingStatePackageService> logger,
        IWorkingStatePackagePublisher publisher)
    {
        _repository = repository;
        _applicationPaths = applicationPaths;
        _operationCancellation = operationCancellation;
        _logger = logger;
        _publisher = publisher;
    }

    public Task<WorkingStatePackageHostResult> SaveAsync(
        OperationCorrelation correlation,
        string targetPath,
        WorkingStateSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            correlation,
            targetPath,
            snapshot,
            WorkingStatePublicationMode.ReplaceExisting,
            cancellationToken);

    public async Task<WorkingStatePackageHostResult> SaveAsync(
        OperationCorrelation correlation,
        string targetPath,
        WorkingStateSnapshot snapshot,
        WorkingStatePublicationMode publicationMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(publicationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(publicationMode));
        }

        var fullTargetPath = ValidatePackagePath(targetPath);
        WorkingStateContractValidator.Validate(snapshot);
        var operation = _operationCancellation.BeginOperation(
            correlation,
            "WorkingStateSave",
            "Working-state package publication",
            [new ProcessingItemPlan(OperationItemId)]);
        if (!operation.TryStartItem(OperationItemId, out var execution))
        {
            return WorkingStatePackageHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "working-state-save-unavailable",
                "Working-state save could not start.");
        }

        using (execution)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   execution!.CancellationToken))
        {
            var candidatePath = $"{fullTargetPath}.{correlation.OperationId}.incomplete";
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                WorkingStateDatabaseCoherenceValidator.Validate(snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
                DeleteIfExists(candidatePath);
                var databasePath = Path.Combine(
                    temporaryDirectory,
                    WorkingStatePackageFormat.DatabaseEntryName);
                var actualGeneration = await _repository.CreateWorkingStateSnapshotAsync(
                        databasePath,
                        snapshot.DatabaseGeneration,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                if (!GenerationsAgree(snapshot.DatabaseGeneration, actualGeneration))
                {
                    throw new InvalidDataException(
                        "The repository snapshot does not match the captured Desktop Database generation.");
                }

                var manifest = new WorkingStateManifest(
                    WorkingStatePackageFormat.CurrentSchemaVersion,
                    StructuredInformationRepository.CurrentSchemaVersion,
                    await ComputeSha256Async(databasePath, linkedCancellation.Token)
                        .ConfigureAwait(false),
                    snapshot);
                await WritePackageAsync(
                        candidatePath,
                        databasePath,
                        manifest,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                await ValidatePackageAsync(candidatePath, temporaryDirectory, linkedCancellation.Token)
                    .ConfigureAwait(false);

                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (!operation.TryEnterNonCancellableCommitBoundary())
                {
                    throw new OperationCanceledException(linkedCancellation.Token);
                }

                await _publisher.PublishAsync(
                        candidatePath,
                        fullTargetPath,
                        publicationMode)
                    .ConfigureAwait(false);
                execution.CommitCompletedResult();
                return WorkingStatePackageHostResult.Accept(manifest, operation.Complete());
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    _operationCancellation.RequestCancellation(correlation.OperationId);
                }

                execution.StopBeforeCommit();
                return WorkingStatePackageHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "working-state-save-cancelled",
                    "Working-state save was cancelled before publication.");
            }
            catch (WorkingStateTargetAlreadyExistsException)
            {
                _logger.LogWarning(
                    "Working-state CreateNew publication target became unavailable for {OperationId}",
                    correlation.OperationId);
                execution.RecordFailure("working-state-save-target-exists");
                return WorkingStatePackageHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "working-state-save-target-exists",
                    "A saved state with this name already exists or became unavailable before publication. Choose another name and try again.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Working-state package save failed for {OperationId}", correlation.OperationId);
                execution.RecordFailure("working-state-save-failed");
                return WorkingStatePackageHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "working-state-save-failed",
                    "The working-state package could not be saved safely.");
            }
            finally
            {
                TryDeleteFile(candidatePath);
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    public async Task<WorkingStatePackageHostResult> RestoreAsync(
        OperationCorrelation correlation,
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        var fullPackagePath = ValidatePackagePath(packagePath);
        var operation = _operationCancellation.BeginOperation(
            correlation,
            "WorkingStateRestore",
            "Working-state package validation and repository restore",
            [new ProcessingItemPlan(OperationItemId)]);
        if (!operation.TryStartItem(OperationItemId, out var execution))
        {
            return WorkingStatePackageHostResult.Reject(
                operation.CompleteTerminal(OperationOutcome.Failed),
                "working-state-restore-unavailable",
                "Working-state restore could not start.");
        }

        using (execution)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   execution!.CancellationToken))
        {
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var validated = await ValidatePackageAsync(
                        fullPackagePath,
                        temporaryDirectory,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                var restored = await _repository.RestoreWorkingStateSnapshotAsync(
                        validated.DatabasePath,
                        validated.Manifest.RepositorySchemaVersion,
                        validated.Manifest.Snapshot.DatabaseGeneration,
                        validated.Manifest.Snapshot.Sources,
                        operation.TryEnterNonCancellableCommitBoundary,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                if (!GenerationsAgree(validated.Manifest.Snapshot.DatabaseGeneration, restored))
                {
                    throw new InvalidDataException(
                        "The restored repository Database does not match the validated package manifest.");
                }

                execution.CommitCompletedResult();
                return WorkingStatePackageHostResult.Accept(
                    validated.Manifest,
                    operation.Complete());
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                if (!operation.IsCancellationAccepted)
                {
                    _operationCancellation.RequestCancellation(correlation.OperationId);
                }

                execution.StopBeforeCommit();
                return WorkingStatePackageHostResult.Reject(
                    await operation.Completion.ConfigureAwait(false),
                    "working-state-restore-cancelled",
                    "Working-state restore was cancelled before repository publication.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Working-state package restore failed for {OperationId}", correlation.OperationId);
                execution.RecordFailure("working-state-restore-failed");
                return WorkingStatePackageHostResult.Reject(
                    operation.CompleteTerminal(OperationOutcome.Failed),
                    "working-state-restore-failed",
                    "The working-state package is invalid or could not be restored safely.");
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    internal async Task<ValidatedWorkingStatePackage> ValidatePackageAsync(
        string packagePath,
        string temporaryDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The working-state package does not exist.", packagePath);
        }

        Directory.CreateDirectory(temporaryDirectory);
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count != 2
            || archive.Entries.Any(entry =>
                !string.Equals(entry.FullName, WorkingStatePackageFormat.ManifestEntryName, StringComparison.Ordinal)
                && !string.Equals(entry.FullName, WorkingStatePackageFormat.DatabaseEntryName, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A working-state package must contain exactly manifest.json and database.sqlite3.");
        }

        var manifestEntries = archive.Entries.Where(entry => string.Equals(
            entry.FullName,
            WorkingStatePackageFormat.ManifestEntryName,
            StringComparison.Ordinal)).ToArray();
        var databaseEntries = archive.Entries.Where(entry => string.Equals(
            entry.FullName,
            WorkingStatePackageFormat.DatabaseEntryName,
            StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1 || databaseEntries.Length != 1)
        {
            throw new InvalidDataException("Working-state package entries must be unique.");
        }

        var manifestEntry = manifestEntries[0];
        var databaseEntry = databaseEntries[0];
        if (manifestEntry.Length is <= 0 or > WorkingStatePackageFormat.MaximumManifestLength
            || databaseEntry.Length is <= 0 or > WorkingStatePackageFormat.MaximumDatabaseLength)
        {
            throw new InvalidDataException("A working-state package entry exceeds its controlled size boundary.");
        }

        WorkingStateManifest manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<WorkingStateManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The working-state manifest is empty.");
        }

        WorkingStateContractValidator.Validate(manifest.Snapshot);
        WorkingStateDatabaseCoherenceValidator.Validate(manifest.Snapshot);
        var databasePath = Path.Combine(
            temporaryDirectory,
            Guid.CreateVersion7().ToString("N") + ".sqlite3");
        await using (var source = databaseEntry.Open())
        await using (var destination = new FileStream(
                         databasePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var digest = await ComputeSha256Async(databasePath, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(manifest.DatabaseSha256)))
        {
            throw new InvalidDataException("The working-state SQLite snapshot digest does not match its manifest.");
        }

        await _repository.ValidateWorkingStateSnapshotAsync(
                databasePath,
                manifest.RepositorySchemaVersion,
                manifest.Snapshot.DatabaseGeneration,
                manifest.Snapshot.Sources,
                cancellationToken)
            .ConfigureAwait(false);
        return new ValidatedWorkingStatePackage(manifest, databasePath);
    }

    private static async Task WritePackageAsync(
        string candidatePath,
        string databasePath,
        WorkingStateManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            candidatePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        var manifestEntry = archive.CreateEntry(
            WorkingStatePackageFormat.ManifestEntryName,
            CompressionLevel.Optimal);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var databaseEntry = archive.CreateEntry(
            WorkingStatePackageFormat.DatabaseEntryName,
            CompressionLevel.NoCompression);
        await using (var entryStream = databaseEntry.Open())
        await using (var databaseStream = new FileStream(
                         databasePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await databaseStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }

        archive.Dispose();
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            _applicationPaths.TempDirectory,
            "WorkingState",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ValidatePackagePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".cia", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Working-state packages must use the .cia extension.", nameof(path));
        }

        return fullPath;
    }

    private static bool GenerationsAgree(
        CIA.Contracts.Database.DatabaseGenerationSummary? expected,
        CIA.Contracts.Database.DatabaseGenerationSummary? actual) =>
        expected is null
            ? actual is null
            : CIA.Contracts.Database.DatabaseGenerationSnapshotComparer.AreEquivalent(expected, actual);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Working-state candidate {CandidatePath} could not be cleaned", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Working-state temporary directory {TemporaryDirectory} could not be cleaned", path);
        }
    }
}

internal interface IWorkingStatePackagePublisher
{
    Task PublishAsync(
        string candidatePath,
        string targetPath,
        WorkingStatePublicationMode publicationMode);
}

internal sealed class FileSystemWorkingStatePackagePublisher : IWorkingStatePackagePublisher
{
    public static FileSystemWorkingStatePackagePublisher Instance { get; } = new();

    private FileSystemWorkingStatePackagePublisher()
    {
    }

    public Task PublishAsync(
        string candidatePath,
        string targetPath,
        WorkingStatePublicationMode publicationMode)
    {
        switch (publicationMode)
        {
            case WorkingStatePublicationMode.CreateNew:
                try
                {
                    File.Move(candidatePath, targetPath, overwrite: false);
                }
                catch (IOException exception) when (IsTargetCollision(exception, targetPath))
                {
                    throw new WorkingStateTargetAlreadyExistsException(targetPath, exception);
                }

                break;
            case WorkingStatePublicationMode.ReplaceExisting:
                if (File.Exists(targetPath))
                {
                    File.Replace(candidatePath, targetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(candidatePath, targetPath);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(publicationMode));
        }

        return Task.CompletedTask;
    }

    private static bool IsTargetCollision(IOException exception, string targetPath)
    {
        const int errorFileExists = 80;
        const int errorAlreadyExists = 183;
        var nativeError = exception.HResult & 0xffff;
        return nativeError is errorFileExists or errorAlreadyExists || File.Exists(targetPath);
    }
}

internal sealed class WorkingStateTargetAlreadyExistsException(
    string targetPath,
    Exception innerException) :
    IOException($"The working-state target already exists: {targetPath}", innerException);

internal sealed record ValidatedWorkingStatePackage(
    WorkingStateManifest Manifest,
    string DatabasePath);

public sealed record WorkingStatePackageHostResult(
    bool Accepted,
    OperationCompletion Completion,
    WorkingStateManifest? Manifest,
    IpcFailure? Failure)
{
    public static WorkingStatePackageHostResult Accept(
        WorkingStateManifest manifest,
        OperationCompletion completion) =>
        new(true, completion, manifest, Failure: null);

    public static WorkingStatePackageHostResult Reject(
        OperationCompletion completion,
        string code,
        string description) =>
        new(false, completion, Manifest: null, new IpcFailure(code, description));
}
