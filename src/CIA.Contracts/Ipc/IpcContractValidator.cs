namespace CIA.Contracts.Ipc;

using CIA.Contracts.Sources;

public static class IpcContractValidator
{
    private const int MaximumFailureCodeLength = 100;
    private const int MaximumFailureDescriptionLength = 1024;
    private const int MaximumPathLength = 32767;

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
            case ProcessingHostLivenessCommand:
            case StopProcessingHostCommand:
                break;
            case CancelOperationCommand command:
                ValidateCancelOperationCommand(command);
                break;
            case LoadSourcesCommand command:
                ValidateLoadSourcesCommand(command);
                break;
            case RefreshSourceCommand command:
                ValidateRefreshSourceCommand(command);
                break;
            case CommandAcknowledgement acknowledgement:
                ValidateCommandAcknowledgement(acknowledgement);
                break;
            case LoadSourcesResponse response:
                ValidateLoadSourcesResponse(response);
                break;
            case RefreshSourceResponse response:
                ValidateRefreshSourceResponse(response);
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

    private static void ValidateCancelOperationCommand(CancelOperationCommand command)
    {
        if (!CIA.Contracts.Operations.OperationId.IsValid(command.OperationId.Value))
        {
            throw InvalidContract(
                "A cancellation command requires a non-empty UUIDv7 Operation ID.");
        }
    }

    private static void ValidateLoadSourcesCommand(LoadSourcesCommand command)
    {
        if (!Enum.IsDefined(command.SelectionKind))
        {
            throw InvalidContract("The source-selection kind is not supported.");
        }

        ValidatePath(command.Path);

        if (command.Settings is null)
        {
            throw InvalidContract("Source-load settings are required.");
        }

        if (!ArchiveNestingDepth.IsValid(command.Settings.MaximumArchiveNestingDepth.Value))
        {
            throw InvalidContract("The maximum archive nesting depth must be at least 1.");
        }
    }

    private static void ValidateLoadSourcesResponse(LoadSourcesResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract("The source-load response has an unsupported acceptance value.");
        }

        if (response.Sources is null)
        {
            throw InvalidContract("A source-load response requires a source collection.");
        }

        foreach (var source in response.Sources)
        {
            if (source is null)
            {
                throw InvalidContract("A source-load response cannot contain null source items.");
            }

            ValidateLoadedSource(source);
        }

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null)
            {
                throw InvalidContract("An accepted source-load response cannot include failure information.");
            }

            return;
        }

        if (response.Sources.Count != 0 || response.Failure is null)
        {
            throw InvalidContract(
                "A rejected source-load response must contain no sources and include controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateRefreshSourceCommand(RefreshSourceCommand command)
    {
        if (command.Source is null)
        {
            throw InvalidContract("A source-refresh command requires a loaded source.");
        }

        ValidateLoadedSource(command.Source);
    }

    private static void ValidateRefreshSourceResponse(RefreshSourceResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract("The source-refresh response has an unsupported acceptance value.");
        }

        if (response.Source is null)
        {
            throw InvalidContract("A source-refresh response requires the retained loaded source.");
        }

        ValidateLoadedSource(response.Source);

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Source.Status != LoadedSourceStatus.Ready || response.Failure is not null)
            {
                throw InvalidContract(
                    "An accepted source-refresh response requires ready status and no failure information.");
            }

            return;
        }

        if (response.Source.Status == LoadedSourceStatus.Ready || response.Failure is null)
        {
            throw InvalidContract(
                "An unsuccessful source-refresh response requires a non-ready status and controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateLoadedSource(LoadedSourceContract source)
    {
        if (!SourceId.IsValid(source.SourceId.Value))
        {
            throw InvalidContract("A loaded source requires a non-empty Source ID.");
        }

        ValidatePath(source.Path);

        if (!Enum.IsDefined(source.Status) || !Enum.IsDefined(source.Kind))
        {
            throw InvalidContract("A loaded source has an unsupported kind or status.");
        }
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

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw InvalidContract($"Source paths must contain 1 to {MaximumPathLength} non-whitespace characters.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw InvalidContract("Source paths must be fully qualified.");
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
