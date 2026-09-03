using System.IO.Pipes;
using CIA.Contracts.Ipc;

namespace CIA.Desktop.Ipc;

public static class ProcessingHostIpcClient
{
    public static async ValueTask<NamedPipeIpcConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        IpcProtocol.ValidatePipeName(pipeName);

        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new NamedPipeIpcConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
