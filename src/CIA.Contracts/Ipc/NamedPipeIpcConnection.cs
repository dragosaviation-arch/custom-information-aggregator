using System.IO.Pipes;

namespace CIA.Contracts.Ipc;

public sealed class NamedPipeIpcConnection : IAsyncDisposable
{
    private readonly PipeStream _pipe;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public NamedPipeIpcConnection(PipeStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        _pipe = pipe;
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _pipe.IsConnected;

    public async ValueTask SendAsync(
        IpcMessage message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            await LengthPrefixedJsonMessageFramer
                .WriteAsync(_pipe, message, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IpcMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            return await LengthPrefixedJsonMessageFramer
                .ReadAsync(_pipe, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _readGate.Dispose();
        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
