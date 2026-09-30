using System.Diagnostics;
using CIA.Core.Runtime;

namespace CIA.Core.Profiles;

public sealed class ProfileStorePublicationGate
{
    public const string LockFileName = ".cia-profile-store.lock";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(20);
    private readonly string _profilesDirectory;
    private readonly TimeSpan _timeout;

    public ProfileStorePublicationGate(
        ApplicationPaths applicationPaths,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _profilesDirectory = Canonicalize(applicationPaths.ProfilesDirectory);
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The profile publication-lock timeout must be positive.");
        }
    }

    public string ProfilesDirectory => _profilesDirectory;

    public ProfileStorePublicationLease Acquire()
    {
        EnsureSafeProfilesRoot(createIfMissing: true);
        var lockPath = Path.Combine(_profilesDirectory, LockFileName);
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
                return new ProfileStorePublicationLease(_profilesDirectory, stream);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                stream?.Dispose();
                if (stopwatch.Elapsed >= _timeout)
                {
                    throw new IOException(
                        "Timed out waiting for exclusive profile publication ownership.",
                        exception);
                }

                Thread.Sleep(RetryInterval);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
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

    private static bool IsSharingViolation(IOException exception) =>
        (uint)exception.HResult is 0x80070020 or 0x80070021;

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

public sealed class ProfileStorePublicationLease : IDisposable
{
    private readonly string _profilesDirectory;
    private FileStream? _stream;

    internal ProfileStorePublicationLease(string profilesDirectory, FileStream stream)
    {
        _profilesDirectory = profilesDirectory;
        _stream = stream;
    }

    internal void VerifyFor(string profilesDirectory)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(profilesDirectory));
        if (!string.Equals(
                canonicalDirectory,
                _profilesDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The publication lease belongs to a different Profiles directory.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
