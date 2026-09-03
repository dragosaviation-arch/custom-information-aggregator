using System.Diagnostics;

namespace CIA.Desktop.Hosting;

public interface IProcessingHostProcessLauncher
{
    Process Start(ProcessStartInfo startInfo);
}

public sealed class SystemProcessingHostProcessLauncher : IProcessingHostProcessLauncher
{
    public Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Processing Host process could not be started.");
    }
}
