using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Ipc;

public static class LengthPrefixedJsonMessageFramer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async ValueTask WriteAsync(
        Stream stream,
        IpcMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanWrite)
        {
            throw new ArgumentException("The IPC stream must be writable.", nameof(stream));
        }

        IpcContractValidator.Validate(message);

        byte[] payload;

        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes<IpcMessage>(message, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new IpcProtocolException(
                IpcProtocolError.InvalidContract,
                "The IPC contract could not be serialized.",
                exception);
        }

        if (payload.Length is <= 0 or > IpcProtocol.MaximumPayloadLength)
        {
            throw new IpcProtocolException(
                IpcProtocolError.InvalidFrameLength,
                $"The IPC payload length must be between 1 and {IpcProtocol.MaximumPayloadLength} bytes.");
        }

        var header = new byte[IpcProtocol.FrameHeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IpcMessage> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException("The IPC stream must be readable.", nameof(stream));
        }

        var header = new byte[IpcProtocol.FrameHeaderLength];
        await ReadExactlyAsync(stream, header, "frame header", cancellationToken).ConfigureAwait(false);

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (payloadLength is <= 0 or > IpcProtocol.MaximumPayloadLength)
        {
            throw new IpcProtocolException(
                IpcProtocolError.InvalidFrameLength,
                $"The IPC frame declared invalid payload length {payloadLength}.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await ReadExactlyAsync(stream, payload, "frame payload", cancellationToken).ConfigureAwait(false);

        IpcMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<IpcMessage>(payload, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new IpcProtocolException(
                IpcProtocolError.MalformedJson,
                "The IPC frame did not contain a recognized JSON message contract.",
                exception);
        }

        if (message is null)
        {
            throw new IpcProtocolException(
                IpcProtocolError.MalformedJson,
                "The IPC frame contained a null JSON message.");
        }

        IpcContractValidator.Validate(message);
        return message;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        string framePart,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;

        while (totalRead < destination.Length)
        {
            var bytesRead = await stream
                .ReadAsync(destination[totalRead..], cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new IpcProtocolException(
                    IpcProtocolError.TruncatedFrame,
                    $"The IPC {framePart} ended before all expected bytes were received.");
            }

            totalRead += bytesRead;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
