namespace CIA.Contracts.Ipc;

public static class IpcProtocol
{
    public const int CurrentVersion = 1;
    public const int FrameHeaderLength = sizeof(int);
    public const int MaximumPayloadLength = 1024 * 1024;
    public const string DefaultPipeName = "CIA.ProcessingHost.v1";

    public static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
    }
}
