using System.IO.Pipes;
using CIA.Contracts.Ipc;

namespace CIA.ProcessingHost.Ipc;

public static class ProcessingHostIpcServer
{
    public static async ValueTask<NamedPipeIpcConnection> AcceptConnectionAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        IpcProtocol.ValidatePipeName(pipeName);

        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new NamedPipeIpcConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
