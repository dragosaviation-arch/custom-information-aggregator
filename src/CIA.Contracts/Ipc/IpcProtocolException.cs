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

    internal IpcProtocolException(
        IpcProtocolError error,
        string message,
        Exception innerException,
        Guid? messageId,
        string? messageType)
        : base(message, innerException)
    {
        Error = error;
        MessageId = messageId;
        MessageType = messageType;
    }

    public IpcProtocolError Error { get; }

    public Guid? MessageId { get; }

    public string? MessageType { get; }
}

public enum IpcProtocolError
{
    InvalidFrameLength,
    TruncatedFrame,
    MalformedJson,
    InvalidContract
}
