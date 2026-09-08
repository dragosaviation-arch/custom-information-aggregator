using CIA.Contracts.Operations;

namespace CIA.Contracts.Database;

public sealed record DatabaseGenerationSummary
{
    public DatabaseGenerationSummary(
        OperationId operationId,
        DatabaseMappingSnapshot mapping,
        int valueCount)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "A Database generation requires a UUIDv7 Operation ID.",
                nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.Columns.Count == 0)
        {
            throw new ArgumentException(
                "A Database generation requires at least one mapped column.",
                nameof(mapping));
        }

        if (valueCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueCount),
                "A published Database generation requires at least one mapped value.");
        }

        OperationId = operationId;
        Mapping = mapping;
        ValueCount = valueCount;
    }

    public OperationId OperationId { get; }

    public DatabaseMappingSnapshot Mapping { get; }

    public int ValueCount { get; }
}
