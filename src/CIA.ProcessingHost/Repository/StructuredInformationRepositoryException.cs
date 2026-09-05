namespace CIA.ProcessingHost.Repository;

public class StructuredInformationRepositoryException : Exception
{
    public StructuredInformationRepositoryException(string message)
        : base(message)
    {
    }

    public StructuredInformationRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class UnsupportedRepositorySchemaVersionException : StructuredInformationRepositoryException
{
    public UnsupportedRepositorySchemaVersionException(int actualVersion, int supportedVersion)
        : base(
            $"The repository schema version {actualVersion} is newer than the supported version " +
            $"{supportedVersion}.")
    {
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
    }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }
}
