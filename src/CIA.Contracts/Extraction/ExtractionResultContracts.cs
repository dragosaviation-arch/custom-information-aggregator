using CIA.Contracts.Database;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Extraction;

public sealed record ExtractionResultSummary
{
    public ExtractionResultSummary(
        OperationId operationId,
        DatabaseGenerationSummary databaseGeneration)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "An Extraction Result requires a UUIDv7 Operation ID.",
                nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(databaseGeneration);
        OperationId = operationId;
        DatabaseGeneration = databaseGeneration;
    }

    public OperationId OperationId { get; }

    public DatabaseGenerationSummary DatabaseGeneration { get; }

    public int ValueCount => DatabaseGeneration.ValueCount;
}

public sealed record ExtractionResultValue
{
    public ExtractionResultValue(
        string databaseFieldName,
        string sourceInformationType,
        string value,
        SourceId sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInformationType);
        ArgumentNullException.ThrowIfNull(value);
        if (!SourceId.IsValid(sourceId.Value))
        {
            throw new ArgumentException(
                "An extracted value requires a valid Source ID.",
                nameof(sourceId));
        }

        DatabaseFieldName = databaseFieldName;
        SourceInformationType = sourceInformationType;
        Value = value;
        SourceId = sourceId;
    }

    public string DatabaseFieldName { get; }

    public string SourceInformationType { get; }

    public string Value { get; }

    public SourceId SourceId { get; }
}
