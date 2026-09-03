namespace CIA.Contracts.Ipc;

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(IpcProtocolError error, string message)
        : base(message)
    {
        Error = error;
    }

    internal IpcProtocolException(IpcProtocolError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public IpcProtocolError Error { get; }
}

public enum IpcProtocolError
{
    InvalidFrameLength,
    TruncatedFrame,
    MalformedJson,
    InvalidContract
}
