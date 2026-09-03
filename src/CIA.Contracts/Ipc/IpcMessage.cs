using System.Text.Json.Serialization;

namespace CIA.Contracts.Ipc;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "messageType",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EstablishConnectionCommand), "establishConnectionCommand")]
[JsonDerivedType(typeof(ProcessingHostLivenessCommand), "processingHostLivenessCommand")]
[JsonDerivedType(typeof(StopProcessingHostCommand), "stopProcessingHostCommand")]
[JsonDerivedType(typeof(CommandAcknowledgement), "commandAcknowledgement")]
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
