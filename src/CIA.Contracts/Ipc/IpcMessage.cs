using System.Text.Json.Serialization;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Ipc;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "messageType",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EstablishConnectionCommand), "establishConnectionCommand")]
[JsonDerivedType(typeof(ProcessingHostLivenessCommand), "processingHostLivenessCommand")]
[JsonDerivedType(typeof(CancelOperationCommand), "cancelOperationCommand")]
[JsonDerivedType(typeof(LoadSourcesCommand), "loadSourcesCommand")]
[JsonDerivedType(typeof(RefreshSourceCommand), "refreshSourceCommand")]
[JsonDerivedType(typeof(StopProcessingHostCommand), "stopProcessingHostCommand")]
[JsonDerivedType(typeof(CommandAcknowledgement), "commandAcknowledgement")]
[JsonDerivedType(typeof(LoadSourcesResponse), "loadSourcesResponse")]
[JsonDerivedType(typeof(RefreshSourceResponse), "refreshSourceResponse")]
[JsonDerivedType(typeof(ProcessingHostAvailabilityEvent), "processingHostAvailabilityEvent")]
public abstract record IpcMessage(Guid MessageId, DateTimeOffset TimestampUtc);

public abstract record IpcCommand(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public abstract record IpcResponse(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public abstract record IpcEvent(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public sealed record EstablishConnectionCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid ClientInstanceId,
    int ProtocolVersion)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record ProcessingHostLivenessCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record CancelOperationCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationId OperationId)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record LoadSourcesCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    SourceSelectionKind SelectionKind,
    string Path,
    SourceLoadSettings Settings)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record RefreshSourceCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    LoadedSourceContract Source)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record StopProcessingHostCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record CommandAcknowledgement(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record LoadSourcesResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    IReadOnlyList<LoadedSourceContract> Sources,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc)
{
    public IReadOnlyList<SourceIntakeIssue> Issues { get; init; } = [];
}

public sealed record RefreshSourceResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    LoadedSourceContract Source,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record ProcessingHostAvailabilityEvent(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    ProcessingHostAvailability Availability)
    : IpcEvent(MessageId, TimestampUtc);

public sealed record IpcFailure(string Code, string Description);

public enum CommandAcceptance
{
    Accepted,
    Rejected,
    UnableToStart
}

public enum ProcessingHostAvailability
{
    Ready,
    Unavailable
}
