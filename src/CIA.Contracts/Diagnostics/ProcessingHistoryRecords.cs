using System.Text.Json.Serialization;
using CIA.Contracts.Operations;

namespace CIA.Contracts.Diagnostics;

public sealed record ProcessingAttemptRecord
{
    [JsonConstructor]
    public ProcessingAttemptRecord(
        OperationCorrelation correlation,
        string operationName,
        string? finalStage,
        DateTimeOffset recordedAtUtc,
        OperationOutcome terminalOutcome,
        OperationCompletion? completion)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ValidateOptionalText(finalStage, nameof(finalStage));
        ValidateTimestamp(recordedAtUtc, correlation, nameof(recordedAtUtc));

        if (!Enum.IsDefined(terminalOutcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalOutcome),
                terminalOutcome,
                null);
        }

        if (completion is not null
            && (completion.Correlation != correlation
                || completion.Outcome != terminalOutcome))
        {
            throw new ArgumentException(
                "Completion context must match the attempt correlation and terminal outcome.",
                nameof(completion));
        }

        Correlation = correlation;
        OperationName = operationName;
        FinalStage = finalStage;
        RecordedAtUtc = recordedAtUtc;
        TerminalOutcome = terminalOutcome;
        Completion = completion;
    }

    public OperationCorrelation Correlation { get; }

    public string OperationName { get; }

    public string? FinalStage { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public OperationOutcome TerminalOutcome { get; }

    public OperationCompletion? Completion { get; }

    [JsonIgnore]
    public IReadOnlyList<OperationItemStatus> Items => Completion?.Items ?? [];

    public static ProcessingAttemptRecord FromCompletion(
        string operationName,
        string? finalStage,
        DateTimeOffset recordedAtUtc,
        OperationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        return new ProcessingAttemptRecord(
            completion.Correlation,
            operationName,
            finalStage,
            recordedAtUtc,
            completion.Outcome,
            completion);
    }

    public static ProcessingAttemptRecord FromTerminalOutcome(
        OperationCorrelation correlation,
        string operationName,
        string? finalStage,
        DateTimeOffset recordedAtUtc,
        OperationOutcome terminalOutcome)
    {
        return new ProcessingAttemptRecord(
            correlation,
            operationName,
            finalStage,
            recordedAtUtc,
            terminalOutcome,
            completion: null);
    }

    private static void ValidateTimestamp(
        DateTimeOffset timestamp,
        OperationCorrelation correlation,
        string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Processing history timestamps must use UTC DateTimeOffset values.",
                parameterName);
        }

        if (timestamp < correlation.InitiatedAtUtc)
        {
            throw new ArgumentException(
                "A processing history timestamp cannot precede operation initiation.",
                parameterName);
        }
    }

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Optional processing history text cannot be empty or whitespace.",
                parameterName);
        }
    }

    internal static void ValidateRecordTimestamp(
        DateTimeOffset timestamp,
        OperationCorrelation correlation,
        string parameterName)
    {
        ValidateTimestamp(timestamp, correlation, parameterName);
    }

    internal static void ValidateOptionalRecordText(string? value, string parameterName)
    {
        ValidateOptionalText(value, parameterName);
    }
}

public sealed record ProcessingDiagnosticRecord
{
    [JsonConstructor]
    public ProcessingDiagnosticRecord(
        DiagnosticRecordId diagnosticId,
        OperationCorrelation correlation,
        string operationName,
        string? processingStage,
        string? sourceId,
        string? itemId,
        OperationItemState? itemState,
        DateTimeOffset recordedAtUtc,
        OperationOutcome? terminalOutcome,
        string userFacingDescription,
        string? failureCode,
        string? technicalDetail)
    {
        if (!DiagnosticRecordId.IsValid(diagnosticId.Value))
        {
            throw new ArgumentException(
                "A diagnostic record requires a non-empty UUIDv7 identity.",
                nameof(diagnosticId));
        }

        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userFacingDescription);
        ProcessingAttemptRecord.ValidateOptionalRecordText(
            processingStage,
            nameof(processingStage));
        ProcessingAttemptRecord.ValidateOptionalRecordText(sourceId, nameof(sourceId));
        ProcessingAttemptRecord.ValidateOptionalRecordText(itemId, nameof(itemId));
        ProcessingAttemptRecord.ValidateOptionalRecordText(failureCode, nameof(failureCode));
        ProcessingAttemptRecord.ValidateOptionalRecordText(
            technicalDetail,
            nameof(technicalDetail));
        ProcessingAttemptRecord.ValidateRecordTimestamp(
            recordedAtUtc,
            correlation,
            nameof(recordedAtUtc));

        if (terminalOutcome is not null && !Enum.IsDefined(terminalOutcome.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalOutcome),
                terminalOutcome,
                null);
        }

        if (itemState is not null && !Enum.IsDefined(itemState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(itemState), itemState, null);
        }

        if (itemState is not null && itemId is null)
        {
            throw new ArgumentException(
                "An item state requires an associated item identity.",
                nameof(itemState));
        }

        DiagnosticId = diagnosticId;
        Correlation = correlation;
        OperationName = operationName;
        ProcessingStage = processingStage;
        SourceId = sourceId;
        ItemId = itemId;
        ItemState = itemState;
        RecordedAtUtc = recordedAtUtc;
        TerminalOutcome = terminalOutcome;
        UserFacingDescription = userFacingDescription;
        FailureCode = failureCode;
        TechnicalDetail = technicalDetail;
    }

    public DiagnosticRecordId DiagnosticId { get; }

    public OperationCorrelation Correlation { get; }

    public string OperationName { get; }

    public string? ProcessingStage { get; }

    public string? SourceId { get; }

    public string? ItemId { get; }

    public OperationItemState? ItemState { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public OperationOutcome? TerminalOutcome { get; }

    public string UserFacingDescription { get; }

    public string? FailureCode { get; }

    public string? TechnicalDetail { get; }

    public ProcessingIssueSummary ToIssueSummary()
    {
        return new ProcessingIssueSummary(
            DiagnosticId,
            Correlation.OperationId,
            OperationName,
            ProcessingStage,
            SourceId,
            ItemId,
            ItemState,
            RecordedAtUtc,
            TerminalOutcome,
            UserFacingDescription);
    }
}

public sealed record ProcessingIssueSummary
{
    [JsonConstructor]
    public ProcessingIssueSummary(
        DiagnosticRecordId diagnosticId,
        OperationId operationId,
        string operationName,
        string? processingStage,
        string? sourceId,
        string? itemId,
        OperationItemState? itemState,
        DateTimeOffset recordedAtUtc,
        OperationOutcome? terminalOutcome,
        string description)
    {
        if (!DiagnosticRecordId.IsValid(diagnosticId.Value))
        {
            throw new ArgumentException(
                "An issue summary requires a non-empty UUIDv7 diagnostic identity.",
                nameof(diagnosticId));
        }

        if (!OperationId.IsValid(operationId.Value))
        {
            throw new ArgumentException(
                "An issue summary requires a non-empty UUIDv7 Operation ID.",
                nameof(operationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ProcessingAttemptRecord.ValidateOptionalRecordText(
            processingStage,
            nameof(processingStage));
        ProcessingAttemptRecord.ValidateOptionalRecordText(sourceId, nameof(sourceId));
        ProcessingAttemptRecord.ValidateOptionalRecordText(itemId, nameof(itemId));

        if (recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Issue-summary timestamps must use UTC DateTimeOffset values.",
                nameof(recordedAtUtc));
        }

        if (terminalOutcome is not null && !Enum.IsDefined(terminalOutcome.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalOutcome),
                terminalOutcome,
                null);
        }

        if (itemState is not null && !Enum.IsDefined(itemState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(itemState), itemState, null);
        }

        if (itemState is not null && itemId is null)
        {
            throw new ArgumentException(
                "An item state requires an associated item identity.",
                nameof(itemState));
        }

        DiagnosticId = diagnosticId;
        OperationId = operationId;
        OperationName = operationName;
        ProcessingStage = processingStage;
        SourceId = sourceId;
        ItemId = itemId;
        ItemState = itemState;
        RecordedAtUtc = recordedAtUtc;
        TerminalOutcome = terminalOutcome;
        Description = description;
    }

    public DiagnosticRecordId DiagnosticId { get; }

    public OperationId OperationId { get; }

    public string OperationName { get; }

    public string? ProcessingStage { get; }

    public string? SourceId { get; }

    public string? ItemId { get; }

    public OperationItemState? ItemState { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public OperationOutcome? TerminalOutcome { get; }

    public string Description { get; }
}
