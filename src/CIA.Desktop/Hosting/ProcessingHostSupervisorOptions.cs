using System.IO;
using CIA.Contracts.Ipc;

namespace CIA.Desktop.Hosting;

public sealed class ProcessingHostSupervisorOptions
{
    public ProcessingHostSupervisorOptions(
        string executablePath,
        string? logDirectory = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? livenessInterval = null,
        TimeSpan? livenessTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        ExecutablePath = Path.GetFullPath(executablePath);
        LogDirectory = ResolveOptionalDirectory(logDirectory);
        StartupTimeout = RequirePositive(startupTimeout ?? TimeSpan.FromSeconds(10), nameof(startupTimeout));
        LivenessInterval = RequirePositive(
            livenessInterval ?? TimeSpan.FromSeconds(2),
            nameof(livenessInterval));
        LivenessTimeout = RequirePositive(
            livenessTimeout ?? TimeSpan.FromSeconds(2),
            nameof(livenessTimeout));
        ShutdownTimeout = RequirePositive(
            shutdownTimeout ?? TimeSpan.FromSeconds(5),
            nameof(shutdownTimeout));
    }

    public string ExecutablePath { get; }

    public string? LogDirectory { get; }

    public TimeSpan StartupTimeout { get; }

    public TimeSpan LivenessInterval { get; }

    public TimeSpan LivenessTimeout { get; }

    public TimeSpan ShutdownTimeout { get; }

    public static string ResolveCompanionExecutablePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "CIA.ProcessingHost.exe");
    }

    internal string CreatePipeName()
    {
        return $"{IpcProtocol.DefaultPipeName}.{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    private static string? ResolveOptionalDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The Processing Host log directory must be absolute.", nameof(directory));
        }

        return Path.GetFullPath(directory);
    }

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Lifecycle timeouts must be positive.");
        }

        return value;
    }
}
