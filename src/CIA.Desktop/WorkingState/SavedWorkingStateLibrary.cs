using System.IO;
using System.Security.Cryptography;
using CIA.Core.Runtime;

namespace CIA.Desktop.WorkingState;

public sealed record SavedWorkingStateFileIdentity(
    string CanonicalPath,
    long Length,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks,
    string ContentMarker);

public sealed record SavedWorkingStateEntry(
    string Name,
    string Path,
    DateTimeOffset ModifiedAtUtc,
    long SizeBytes,
    SavedWorkingStateFileIdentity Identity)
{
    public DateTimeOffset ModifiedAtLocal => ModifiedAtUtc.ToLocalTime();
}

public sealed record SavedWorkingStateInventoryProblem(string Path, string Description);

public sealed record SavedWorkingStateInventory(
    IReadOnlyList<SavedWorkingStateEntry> States,
    IReadOnlyList<SavedWorkingStateInventoryProblem> Problems);

public sealed record SavedWorkingStateLibraryResult(
    bool Succeeded,
    string? Path,
    SavedWorkingStateEntry? State,
    string? Problem)
{
    public static SavedWorkingStateLibraryResult Success(
        string path,
        SavedWorkingStateEntry? state = null) =>
        new(true, path, state, Problem: null);

    public static SavedWorkingStateLibraryResult Failure(string problem) =>
        new(false, Path: null, State: null, problem);
}

public sealed class SavedWorkingStateLibrary
{
    public const string DirectoryName = "Saved States";
    public const string PackageExtension = ".cia";
    public const int MaximumStateNameLength = 120;
    private static readonly char[] InvalidFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*', .. Path.GetInvalidFileNameChars()];
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly string _workingDirectory;
    private readonly string _savedStatesDirectory;

    public SavedWorkingStateLibrary(ApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _workingDirectory = Canonicalize(applicationPaths.WorkingDirectory);
        _savedStatesDirectory = Canonicalize(Path.Combine(_workingDirectory, DirectoryName));
        if (!IsDirectChild(_savedStatesDirectory, _workingDirectory))
        {
            throw new InvalidOperationException(
                "The saved-state library could not be resolved beneath the runtime Working directory.");
        }
    }

    public string DirectoryPath => _savedStatesDirectory;

    public SavedWorkingStateInventory CreateInventory()
    {
        if (!Directory.Exists(_savedStatesDirectory))
        {
            return new SavedWorkingStateInventory([], []);
        }

        try
        {
            EnsureSafeLibraryPath(createDirectory: false);
            var states = new List<SavedWorkingStateEntry>();
            var problems = new List<SavedWorkingStateInventoryProblem>();
            foreach (var path in Directory.GetFiles(
                         _savedStatesDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!path.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    states.Add(ReadCurrentEntry(path));
                }
                catch (Exception exception) when (IsControlledFileFailure(exception))
                {
                    problems.Add(new SavedWorkingStateInventoryProblem(
                        SafeDisplayPath(path),
                        $"The saved state could not be inspected safely ({exception.Message})."));
                }
            }

            return new SavedWorkingStateInventory(
                states
                    .OrderByDescending(state => state.ModifiedAtUtc)
                    .ThenBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                problems);
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return new SavedWorkingStateInventory(
                [],
                [new SavedWorkingStateInventoryProblem(
                    _savedStatesDirectory,
                    $"The saved-state library could not be inspected safely ({exception.Message}).")]);
        }
    }

    public SavedWorkingStateLibraryResult ResolveNewTarget(string? stateName)
    {
        var validationProblem = ValidateStateName(stateName);
        if (validationProblem is not null)
        {
            return SavedWorkingStateLibraryResult.Failure(validationProblem);
        }

        try
        {
            EnsureSafeLibraryPath(createDirectory: true);
            var targetPath = ValidateManagedPackagePath(
                Path.Combine(_savedStatesDirectory, stateName + PackageExtension),
                requireExistingFile: false);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                return SavedWorkingStateLibraryResult.Failure(
                    $"A saved state named '{stateName}' already exists.");
            }

            return SavedWorkingStateLibraryResult.Success(targetPath);
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return SavedWorkingStateLibraryResult.Failure(
                $"The saved-state target is unavailable ({exception.Message}).");
        }
    }

    public SavedWorkingStateLibraryResult Revalidate(SavedWorkingStateEntry? selectedState)
    {
        if (selectedState is null)
        {
            return SavedWorkingStateLibraryResult.Failure("Select a saved state first.");
        }

        try
        {
            EnsureSafeLibraryPath(createDirectory: false);
            var current = ReadCurrentEntry(selectedState.Path);
            if (current.Identity != selectedState.Identity)
            {
                return SavedWorkingStateLibraryResult.Failure(
                    "The selected saved state changed after it was listed. Refresh the library and select it again.");
            }

            return SavedWorkingStateLibraryResult.Success(current.Path, current);
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return SavedWorkingStateLibraryResult.Failure(
                $"The selected saved state is no longer available ({exception.Message}).");
        }
    }

    public SavedWorkingStateLibraryResult Delete(SavedWorkingStateEntry? selectedState)
    {
        var validation = Revalidate(selectedState);
        if (!validation.Succeeded || validation.State is null)
        {
            return validation;
        }

        try
        {
            File.Delete(validation.State.Path);
            return SavedWorkingStateLibraryResult.Success(validation.State.Path);
        }
        catch (Exception exception) when (IsControlledFileFailure(exception))
        {
            return SavedWorkingStateLibraryResult.Failure(
                $"The saved state could not be deleted ({exception.Message}).");
        }
    }

    public static string? ValidateStateName(string? stateName)
    {
        if (string.IsNullOrWhiteSpace(stateName))
        {
            return "Enter a saved-state name.";
        }

        if (!string.Equals(stateName, stateName.Trim(), StringComparison.Ordinal))
        {
            return "Saved-state names cannot begin or end with whitespace.";
        }

        if (stateName.Length > MaximumStateNameLength)
        {
            return $"Saved-state names cannot exceed {MaximumStateNameLength} characters.";
        }

        if (stateName is "." or ".."
            || Path.IsPathFullyQualified(stateName)
            || stateName.IndexOfAny(InvalidFileNameCharacters) >= 0)
        {
            return "The saved-state name must be a single valid Windows filename.";
        }

        if (stateName.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            return "Enter the saved-state name without the .cia extension.";
        }

        if (stateName.EndsWith('.') || stateName.EndsWith(' '))
        {
            return "Saved-state names cannot end with a period or space.";
        }

        var deviceName = stateName.Split('.')[0];
        return ReservedDeviceNames.Contains(deviceName)
            ? "The saved-state name is reserved by Windows."
            : null;
    }

    private SavedWorkingStateEntry ReadCurrentEntry(string path)
    {
        var fullPath = ValidateManagedPackagePath(path, requireExistingFile: true);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Saved states must be files.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Saved-state files cannot be reparse points.");
        }

        var information = new FileInfo(fullPath);
        information.Refresh();
        var contentMarker = ComputeBoundedContentMarker(fullPath);
        information.Refresh();
        if (information.Length != contentMarker.Length)
        {
            throw new IOException("The saved-state file changed while it was being inspected.");
        }

        var modifiedAtUtc = new DateTimeOffset(information.LastWriteTimeUtc, TimeSpan.Zero);
        var identity = new SavedWorkingStateFileIdentity(
            fullPath,
            information.Length,
            information.CreationTimeUtc.Ticks,
            information.LastWriteTimeUtc.Ticks,
            contentMarker.Marker);
        var stateName = Path.GetFileNameWithoutExtension(fullPath);
        var nameProblem = ValidateStateName(stateName);
        if (nameProblem is not null)
        {
            throw new InvalidDataException(nameProblem);
        }

        return new SavedWorkingStateEntry(
            stateName,
            fullPath,
            modifiedAtUtc,
            information.Length,
            identity);
    }

    private string ValidateManagedPackagePath(string path, bool requireExistingFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Canonicalize(path);
        if (!IsDirectChild(fullPath, _savedStatesDirectory)
            || !fullPath.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Saved states must be direct .cia files inside the managed saved-state library.");
        }

        if (requireExistingFile && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("The saved-state file does not exist.", fullPath);
        }

        return fullPath;
    }

    private void EnsureSafeLibraryPath(bool createDirectory)
    {
        if (File.Exists(_workingDirectory) || File.Exists(_savedStatesDirectory))
        {
            throw new InvalidDataException("The configured saved-state location is not a directory.");
        }

        if (Directory.Exists(_workingDirectory))
        {
            EnsureNotReparsePoint(
                _workingDirectory,
                "The runtime Working directory cannot be a reparse point.");
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(_savedStatesDirectory);
        }

        if (Directory.Exists(_savedStatesDirectory))
        {
            EnsureNotReparsePoint(
                _savedStatesDirectory,
                "The saved-state library cannot be a reparse point.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private static (long Length, string Marker) ComputeBoundedContentMarker(string path)
    {
        const int markerWindowBytes = 4 * 1024;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            markerWindowBytes,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = stream.Length;
        var buffer = new byte[markerWindowBytes];
        var firstLength = (int)Math.Min(length, markerWindowBytes);
        stream.ReadExactly(buffer.AsSpan(0, firstLength));
        hash.AppendData(buffer, 0, firstLength);
        if (length > firstLength)
        {
            stream.Position = Math.Max(firstLength, length - markerWindowBytes);
            var finalLength = (int)Math.Min(
                markerWindowBytes,
                length - stream.Position);
            stream.ReadExactly(buffer.AsSpan(0, finalLength));
            hash.AppendData(buffer, 0, finalLength);
        }

        hash.AppendData(BitConverter.GetBytes(length));
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool IsDirectChild(string path, string directory)
    {
        var parent = Path.GetDirectoryName(Canonicalize(path));
        return parent is not null
            && string.Equals(
                Canonicalize(parent),
                Canonicalize(directory),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

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
            or NotSupportedException;
}
