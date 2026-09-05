using CIA.Contracts.Sources;

namespace CIA.ProcessingHost.Repository;

public sealed record IndexedOccurrence
{
    public IndexedOccurrence(string tag, string value, SourceId sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(value);

        if (sourceId.Value == Guid.Empty)
        {
            throw new ArgumentException("An indexed occurrence requires a source ID.", nameof(sourceId));
        }

        Tag = tag;
        Value = value;
        SourceId = sourceId;
    }

    public string Tag { get; }

    public string Value { get; }

    public SourceId SourceId { get; }
}
