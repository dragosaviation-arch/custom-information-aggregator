namespace CIA.Desktop.Hosting;

public sealed class ProcessingHostLifecycleException : Exception
{
    public ProcessingHostLifecycleException(
        ProcessingHostLifecycleError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public ProcessingHostLifecycleError Error { get; }
}

public enum ProcessingHostLifecycleError
{
    ExecutableNotFound,
    ProcessLaunchFailed,
    ReadinessTimedOut,
    InvalidReadinessResponse,
    AutomaticRecreationFailed
}
