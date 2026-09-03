using System.Text.Json.Serialization;

namespace CIA.Contracts.Operations;

public sealed record OperationCorrelation
{
    [JsonConstructor]
    public OperationCorrelation(OperationId operationId, DateTimeOffset initiatedAtUtc)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "Operation correlation requires a non-empty UUIDv7 Operation ID.",
                nameof(operationId));
        }

        if (initiatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Operation correlation timestamps must use UTC DateTimeOffset values.",
                nameof(initiatedAtUtc));
        }

        OperationId = operationId;
        InitiatedAtUtc = initiatedAtUtc;
    }

    public OperationId OperationId { get; }

    public DateTimeOffset InitiatedAtUtc { get; }

    public static OperationCorrelation CreateNew()
    {
        return CreateNew(DateTimeOffset.UtcNow);
    }

    public static OperationCorrelation CreateNew(DateTimeOffset initiatedAtUtc)
    {
        return new OperationCorrelation(OperationId.CreateNew(), initiatedAtUtc);
    }

    public OperationCorrelation CreateReinitiatedAttempt()
    {
        return CreateNew();
    }

    public OperationCorrelation CreateReinitiatedAttempt(DateTimeOffset initiatedAtUtc)
    {
        return CreateNew(initiatedAtUtc);
    }
}
