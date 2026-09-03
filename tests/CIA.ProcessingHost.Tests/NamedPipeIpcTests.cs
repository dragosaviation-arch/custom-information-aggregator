using System.Buffers.Binary;
using System.Text;
using CIA.Contracts.Ipc;
using CIA.Desktop.Ipc;
using CIA.ProcessingHost.Ipc;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class NamedPipeIpcTests
{
    [TestMethod]
    public async Task DesktopAndProcessingHostExchangeTypedContractsOverNamedPipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = $"CIA.Tests.{Guid.NewGuid():N}";

        var acceptTask = ProcessingHostIpcServer
            .AcceptConnectionAsync(pipeName, timeout.Token)
            .AsTask();

        await using var client = await ProcessingHostIpcClient
            .ConnectAsync(pipeName, timeout.Token);
        await using var server = await acceptTask;

        Assert.IsTrue(client.IsConnected);
        Assert.IsTrue(server.IsConnected);

        var command = new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);

        var receiveCommandTask = server.ReceiveAsync(timeout.Token).AsTask();
        await client.SendAsync(command, timeout.Token);
        var receivedCommand = await receiveCommandTask;

        Assert.IsInstanceOfType(receivedCommand, typeof(EstablishConnectionCommand));
        Assert.AreEqual(command, receivedCommand);

        var acknowledgement = new CommandAcknowledgement(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Accepted,
            Failure: null);
        var availabilityEvent = new ProcessingHostAvailabilityEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            ProcessingHostAvailability.Ready);

        var receiveAcknowledgementTask = client.ReceiveAsync(timeout.Token).AsTask();
        await server.SendAsync(acknowledgement, timeout.Token);
        var receivedAcknowledgement = await receiveAcknowledgementTask;

        var receiveAvailabilityEventTask = client.ReceiveAsync(timeout.Token).AsTask();
        await server.SendAsync(availabilityEvent, timeout.Token);
        var receivedAvailabilityEvent = await receiveAvailabilityEventTask;

        Assert.IsInstanceOfType(receivedAcknowledgement, typeof(CommandAcknowledgement));
        Assert.AreEqual(acknowledgement, receivedAcknowledgement);
        Assert.IsInstanceOfType(receivedAvailabilityEvent, typeof(ProcessingHostAvailabilityEvent));
        Assert.AreEqual(availabilityEvent, receivedAvailabilityEvent);
    }

    [TestMethod]
    public async Task FramerUsesLittleEndianLengthPrefixAndPreservesMessageBoundaries()
    {
        var first = CreateConnectionCommand();
        var second = new ProcessingHostAvailabilityEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            ProcessingHostAvailability.Ready);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, first);
        var secondFrameStart = checked((int)stream.Position);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, second);

        var bytes = stream.ToArray();
        var firstPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(0, IpcProtocol.FrameHeaderLength));
        var secondPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(secondFrameStart, IpcProtocol.FrameHeaderLength));

        Assert.AreEqual(secondFrameStart - IpcProtocol.FrameHeaderLength, firstPayloadLength);
        Assert.AreEqual(bytes.Length - secondFrameStart - IpcProtocol.FrameHeaderLength, secondPayloadLength);

        stream.Position = 0;
        Assert.AreEqual(first, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        Assert.AreEqual(second, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    public async Task LifecycleCommandsRoundTripAsTypedLengthPrefixedJsonContracts()
    {
        IpcMessage[] messages =
        [
            new ProcessingHostLivenessCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow),
            new StopProcessingHostCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow)
        ];

        await using var stream = new MemoryStream();

        foreach (var message in messages)
        {
            await LengthPrefixedJsonMessageFramer.WriteAsync(stream, message);
        }

        stream.Position = 0;

        foreach (var expected in messages)
        {
            Assert.AreEqual(expected, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        }

        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(IpcProtocol.MaximumPayloadLength + 1)]
    public async Task FramerRejectsInvalidDeclaredLengths(int declaredLength)
    {
        await using var stream = CreateFrame([], declaredLength);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.InvalidFrameLength);
    }

    [TestMethod]
    public async Task FramerRejectsTruncatedPayload()
    {
        await using var stream = CreateFrame("{}"u8.ToArray(), declaredLength: 8);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.TruncatedFrame);
    }

    [TestMethod]
    public async Task FramerRejectsTruncatedHeader()
    {
        await using var stream = new MemoryStream([1, 0], writable: false);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.TruncatedFrame);
    }

    [TestMethod]
    public async Task FramerRejectsMalformedJson()
    {
        await using var stream = CreateFrame("{not-json}"u8.ToArray());

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.MalformedJson);
    }

    [TestMethod]
    public async Task FramerRejectsUnknownMessageContract()
    {
        const string unknownMessage = """
            {
              "messageType": "unknownMessage",
              "messageId": "00000000-0000-0000-0000-000000000000",
              "timestampUtc": "2026-09-03T00:00:00.0000000+00:00"
            }
            """;
        await using var stream = CreateFrame(Encoding.UTF8.GetBytes(unknownMessage));

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.MalformedJson);
    }

    [TestMethod]
    public async Task FramerRejectsStructurallyInvalidTypedContract()
    {
        var invalidContract = $$"""
            {
              "messageType": "establishConnectionCommand",
              "messageId": "00000000-0000-0000-0000-000000000000",
              "timestampUtc": "{{DateTimeOffset.UtcNow:O}}",
              "clientInstanceId": "{{Guid.CreateVersion7()}}",
              "protocolVersion": {{IpcProtocol.CurrentVersion}}
            }
            """;
        await using var stream = CreateFrame(Encoding.UTF8.GetBytes(invalidContract));

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.InvalidContract);
    }

    [TestMethod]
    public void ProcessBoundaryContractsExcludeForbiddenObjectTypes()
    {
        var contractsAssembly = typeof(IpcMessage).Assembly;
        var referencedAssemblies = contractsAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        string[] forbiddenAssemblies =
        [
            "CIA.Core",
            "CIA.Desktop",
            "Microsoft.Data.Sqlite",
            "PresentationFramework",
            "WindowsBase"
        ];

        foreach (var forbiddenAssembly in forbiddenAssemblies)
        {
            CollectionAssert.DoesNotContain(referencedAssemblies, forbiddenAssembly);
        }

        var payloadTypes = contractsAssembly
            .GetExportedTypes()
            .Where(type => typeof(IpcMessage).IsAssignableFrom(type) || type == typeof(IpcFailure));

        foreach (var payloadType in payloadTypes)
        {
            Assert.IsFalse(typeof(Exception).IsAssignableFrom(payloadType));
            Assert.IsFalse(payloadType.Name.Contains("ViewModel", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Control", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Parser", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Storage", StringComparison.Ordinal));

            foreach (var property in payloadType.GetProperties())
            {
                Assert.AreNotEqual(typeof(object), property.PropertyType);
                Assert.IsFalse(typeof(Exception).IsAssignableFrom(property.PropertyType));
                Assert.AreNotEqual(
                    true,
                    property.PropertyType.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal));
            }
        }
    }

    private static EstablishConnectionCommand CreateConnectionCommand()
    {
        return new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);
    }

    private static MemoryStream CreateFrame(byte[] payload, int? declaredLength = null)
    {
        var frame = new byte[IpcProtocol.FrameHeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            frame.AsSpan(0, IpcProtocol.FrameHeaderLength),
            declaredLength ?? payload.Length);
        payload.CopyTo(frame, IpcProtocol.FrameHeaderLength);
        return new MemoryStream(frame, writable: false);
    }

    private static async Task AssertProtocolErrorAsync(
        Func<Task> action,
        IpcProtocolError expectedError)
    {
        try
        {
            await action();
            Assert.Fail($"Expected {nameof(IpcProtocolException)} with error {expectedError}.");
        }
        catch (IpcProtocolException exception)
        {
            Assert.AreEqual(expectedError, exception.Error);
        }
    }
}
