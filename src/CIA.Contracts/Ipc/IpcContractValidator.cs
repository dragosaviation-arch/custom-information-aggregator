namespace CIA.Contracts.Ipc;

public static class IpcContractValidator
{
    private const int MaximumFailureCodeLength = 100;
    private const int MaximumFailureDescriptionLength = 1024;

    public static void Validate(IpcMessage message)
    {
        if (message is null)
        {
            throw InvalidContract("An IPC message is required.");
        }

        ValidateVersionSevenId(message.MessageId, nameof(message.MessageId));

        if (message.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw InvalidContract("IPC message timestamps must use UTC DateTimeOffset values.");
        }

        switch (message)
        {
            case EstablishConnectionCommand command:
                ValidateEstablishConnectionCommand(command);
                break;
            case CommandAcknowledgement acknowledgement:
                ValidateCommandAcknowledgement(acknowledgement);
                break;
            case ProcessingHostAvailabilityEvent availabilityEvent:
                ValidateProcessingHostAvailabilityEvent(availabilityEvent);
                break;
            default:
                throw InvalidContract($"Unsupported IPC contract type '{message.GetType().FullName}'.");
        }
    }

    private static void ValidateEstablishConnectionCommand(EstablishConnectionCommand command)
    {
        ValidateVersionSevenId(command.ClientInstanceId, nameof(command.ClientInstanceId));

        if (command.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            throw InvalidContract(
                $"Protocol version {command.ProtocolVersion} is not supported. Expected {IpcProtocol.CurrentVersion}.");
        }
    }

    private static void ValidateCommandAcknowledgement(CommandAcknowledgement acknowledgement)
    {
        ValidateVersionSevenId(acknowledgement.CommandMessageId, nameof(acknowledgement.CommandMessageId));

        if (!Enum.IsDefined(acknowledgement.Acceptance))
        {
            throw InvalidContract("The command acknowledgement has an unsupported acceptance value.");
        }

        if (acknowledgement.Acceptance == CommandAcceptance.Accepted)
        {
            if (acknowledgement.Failure is not null)
            {
                throw InvalidContract("An accepted command acknowledgement cannot include failure information.");
            }

            return;
        }

        if (acknowledgement.Failure is null)
        {
            throw InvalidContract("A rejected command acknowledgement must include controlled failure information.");
        }

        ValidateFailure(acknowledgement.Failure);
    }

    private static void ValidateProcessingHostAvailabilityEvent(
        ProcessingHostAvailabilityEvent availabilityEvent)
    {
        if (!Enum.IsDefined(availabilityEvent.Availability))
        {
            throw InvalidContract("The Processing Host availability event has an unsupported availability value.");
        }
    }

    private static void ValidateFailure(IpcFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > MaximumFailureCodeLength)
        {
            throw InvalidContract(
                $"Failure codes must contain 1 to {MaximumFailureCodeLength} non-whitespace characters.");
        }

        if (string.IsNullOrWhiteSpace(failure.Description)
            || failure.Description.Length > MaximumFailureDescriptionLength)
        {
            throw InvalidContract(
                $"Failure descriptions must contain 1 to {MaximumFailureDescriptionLength} non-whitespace characters.");
        }
    }

    private static void ValidateVersionSevenId(Guid id, string fieldName)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw InvalidContract($"{fieldName} must be a non-empty UUIDv7 value.");
        }
    }

    private static IpcProtocolException InvalidContract(string message)
    {
        return new IpcProtocolException(IpcProtocolError.InvalidContract, message);
    }
}
