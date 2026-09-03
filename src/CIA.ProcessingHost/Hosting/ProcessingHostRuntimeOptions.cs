using System.Globalization;
using CIA.Contracts.Ipc;
using Microsoft.Extensions.Configuration;

namespace CIA.ProcessingHost.Hosting;

public sealed record ProcessingHostRuntimeOptions(string PipeName, int ParentProcessId)
{
    public static ProcessingHostRuntimeOptions? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var pipeName = configuration[ProcessingHostRuntimeContract.PipeNameConfigurationKey];
        var parentProcessIdText = configuration[
            ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey];

        if (string.IsNullOrWhiteSpace(pipeName) && string.IsNullOrWhiteSpace(parentProcessIdText))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException("A managed Processing Host requires a Named-Pipe name.");
        }

        IpcProtocol.ValidatePipeName(pipeName);

        if (!int.TryParse(
                parentProcessIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId)
            || parentProcessId <= 0)
        {
            throw new InvalidOperationException(
                "A managed Processing Host requires a positive parent process ID.");
        }

        return new ProcessingHostRuntimeOptions(pipeName, parentProcessId);
    }
}
