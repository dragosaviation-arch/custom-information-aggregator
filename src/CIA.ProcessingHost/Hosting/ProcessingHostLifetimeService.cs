using System.Diagnostics;
using System.IO;
using CIA.Contracts.Ipc;
using CIA.ProcessingHost.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Hosting;

public sealed class ProcessingHostLifetimeService(
    ProcessingHostRuntimeOptions options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ProcessingHostLifetimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Process parentProcess;

        try
        {
            parentProcess = Process.GetProcessById(options.ParentProcessId);
        }
        catch (ArgumentException exception)
        {
            logger.LogError(
                exception,
                "Processing Host parent process {ParentProcessId} is unavailable; stopping without becoming resident",
                options.ParentProcessId);
            applicationLifetime.StopApplication();
            return;
        }

        using (parentProcess)
        using (var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            var parentExitTask = MonitorParentAsync(
                parentProcess,
                runtimeCancellation,
                stoppingToken);

            try
            {
                logger.LogInformation(
                    "Processing Host waiting for its supervised IPC connection with parent process {ParentProcessId}",
                    options.ParentProcessId);

                await using var connection = await ProcessingHostIpcServer
                    .AcceptConnectionAsync(options.PipeName, runtimeCancellation.Token)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Processing Host IPC connection established with parent process {ParentProcessId}",
                    options.ParentProcessId);

                await ServeLifecycleAsync(connection, runtimeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
            {
            }
            catch (IpcProtocolException exception)
                when (exception.Error == IpcProtocolError.TruncatedFrame)
            {
                logger.LogWarning(
                    exception,
                    "Processing Host lifecycle IPC connection was lost or ended with a truncated frame");
            }
            catch (IpcProtocolException exception)
            {
                logger.LogWarning(
                    exception,
                    "Processing Host rejected invalid lifecycle IPC with protocol error {ProtocolError}",
                    exception.Error);
            }
            catch (IOException exception)
            {
                logger.LogWarning(
                    exception,
                    "Processing Host lifecycle IPC connection was lost");
            }
            finally
            {
                runtimeCancellation.Cancel();
                await IgnoreCancellationAsync(parentExitTask).ConfigureAwait(false);
                applicationLifetime.StopApplication();
            }
        }
    }

    private async Task ServeLifecycleAsync(
        NamedPipeIpcConnection connection,
        CancellationToken cancellationToken)
    {
        var established = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            switch (message)
            {
                case EstablishConnectionCommand command when !established:
                    await SendAcceptedAsync(connection, command.MessageId, cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                        new ProcessingHostAvailabilityEvent(
                            Guid.CreateVersion7(),
                            DateTimeOffset.UtcNow,
                            ProcessingHostAvailability.Ready),
                        cancellationToken).ConfigureAwait(false);
                    established = true;
                    logger.LogInformation(
                        "Processing Host reported IPC readiness to client instance {ClientInstanceId}",
                        command.ClientInstanceId);
                    break;

                case ProcessingHostLivenessCommand command when established:
                    await SendAcceptedAsync(connection, command.MessageId, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case StopProcessingHostCommand command when established:
                    await SendAcceptedAsync(connection, command.MessageId, cancellationToken)
                        .ConfigureAwait(false);
                    logger.LogInformation(
                        "Processing Host accepted a controlled shutdown request from its supervising UI process");
                    applicationLifetime.StopApplication();
                    return;

                default:
                    await SendRejectedAsync(connection, message.MessageId, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task MonitorParentAsync(
        Process parentProcess,
        CancellationTokenSource runtimeCancellation,
        CancellationToken stoppingToken)
    {
        try
        {
            await parentProcess.WaitForExitAsync(runtimeCancellation.Token).ConfigureAwait(false);

            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Processing Host detected termination of parent process {ParentProcessId}; stopping to avoid an orphan process",
                    options.ParentProcessId);
                runtimeCancellation.Cancel();
                applicationLifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
        {
        }
    }

    private static ValueTask SendAcceptedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        CancellationToken cancellationToken)
    {
        return connection.SendAsync(
            new CommandAcknowledgement(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                commandMessageId,
                CommandAcceptance.Accepted,
                Failure: null),
            cancellationToken);
    }

    private static ValueTask SendRejectedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        CancellationToken cancellationToken)
    {
        return connection.SendAsync(
            new CommandAcknowledgement(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                commandMessageId,
                CommandAcceptance.Rejected,
                new IpcFailure(
                    "invalid-lifecycle-sequence",
                    "The message is not valid in the current Processing Host lifecycle state.")),
            cancellationToken);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
