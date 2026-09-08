using System.Diagnostics;
using System.IO;
using CIA.Contracts.Database;
using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Extraction;
using CIA.ProcessingHost.Ipc;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.SourceIntake;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Hosting;

public sealed class ProcessingHostLifetimeService(
    ProcessingHostRuntimeOptions options,
    CooperativeOperationCancellation operationCancellation,
    SourceIntakeService sourceIntake,
    SourceRefreshService sourceRefresh,
    DiscoveryService discovery,
    DatabaseGenerationService databaseGeneration,
    DatabaseReviewService databaseReview,
    DatabaseExtractionService databaseExtraction,
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
        Task? activeProcessingRequest = null;
        using var processingResponseSendGate = new SemaphoreSlim(1, 1);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (activeProcessingRequest?.IsCompleted == true)
            {
                await ObserveProcessingRequestAsync(activeProcessingRequest).ConfigureAwait(false);
                activeProcessingRequest = null;
            }

            var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (activeProcessingRequest?.IsCompleted == true)
            {
                await ObserveProcessingRequestAsync(activeProcessingRequest).ConfigureAwait(false);
                activeProcessingRequest = null;
            }

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

                case CancelOperationCommand command when established:
                    var cancellation = operationCancellation.RequestCancellation(
                        command.OperationId);

                    if (cancellation.Accepted)
                    {
                        await SendAcceptedSerializedAsync(
                                connection,
                                command.MessageId,
                                processingResponseSendGate,
                                cancellationToken)
                            .ConfigureAwait(false);
                        logger.LogInformation(
                            "Processing Host accepted cooperative cancellation for operation {OperationId}",
                            command.OperationId);
                    }
                    else
                    {
                        await SendRejectedSerializedAsync(
                                connection,
                                command.MessageId,
                                "operation-not-active",
                                "The requested operation is not active in the Processing Host.",
                                processingResponseSendGate,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case BuildDatabaseCommand command
                    when established && activeProcessingRequest is null:
                    activeProcessingRequest = ProcessDatabaseBuildAsync(
                        connection,
                        command,
                        processingResponseSendGate,
                        cancellationToken);
                    break;

                case BuildDatabaseCommand command when established:
                    await SendRejectedSerializedAsync(
                            connection,
                            command.MessageId,
                            "database-build-already-active",
                            "A Database build is already active in the Processing Host.",
                            processingResponseSendGate,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case RunExtractionCommand command
                    when established && activeProcessingRequest is null:
                    activeProcessingRequest = ProcessExtractionAsync(
                        connection,
                        command,
                        processingResponseSendGate,
                        cancellationToken);
                    break;

                case RunExtractionCommand command when established:
                    await SendRejectedSerializedAsync(
                            connection,
                            command.MessageId,
                            "extraction-already-active",
                            "A processing operation is already active in the Processing Host.",
                            processingResponseSendGate,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case GetDatabaseReviewPageCommand command when established:
                    var reviewResult = await databaseReview.ReadPageAsync(
                            command.GenerationId,
                            command.StartRowOrdinal,
                            command.RowCount,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                            new GetDatabaseReviewPageResponse(
                                Guid.CreateVersion7(),
                                DateTimeOffset.UtcNow,
                                command.MessageId,
                                command.GenerationId,
                                reviewResult.Accepted
                                    ? CommandAcceptance.Accepted
                                    : CommandAcceptance.Rejected,
                                reviewResult.Page,
                                reviewResult.Failure),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case LoadSourcesCommand command when established:
                    var progress = new InlineProgress<SourceIntakeProgressSnapshot>(snapshot =>
                        connection.SendAsync(
                                new SourceIntakeProgressEvent(
                                    Guid.CreateVersion7(),
                                    DateTimeOffset.UtcNow,
                                    command.MessageId,
                                    snapshot),
                                cancellationToken)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult());
                    var result = await sourceIntake.LoadAsync(
                            command.SelectionKind,
                            command.Path,
                            command.Settings,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                            new LoadSourcesResponse(
                                Guid.CreateVersion7(),
                                DateTimeOffset.UtcNow,
                                command.MessageId,
                                result.Accepted
                                    ? CommandAcceptance.Accepted
                                    : CommandAcceptance.Rejected,
                                result.Sources,
                                result.Failure)
                            {
                                Issues = result.Issues
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case RefreshSourceCommand command when established:
                    var refreshResult = await sourceRefresh
                        .RefreshAsync(command.Source, cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                            new RefreshSourceResponse(
                                Guid.CreateVersion7(),
                                DateTimeOffset.UtcNow,
                                command.MessageId,
                                refreshResult.Accepted
                                    ? CommandAcceptance.Accepted
                                    : CommandAcceptance.Rejected,
                                refreshResult.Source,
                                refreshResult.Failure),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case RunDiscoveryCommand command when established:
                    var discoveryResult = await discovery
                        .RunAsync(command.Correlation, command.Sources, cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                            new RunDiscoveryResponse(
                                Guid.CreateVersion7(),
                                DateTimeOffset.UtcNow,
                                command.MessageId,
                                discoveryResult.Accepted
                                    ? CommandAcceptance.Accepted
                                    : CommandAcceptance.Rejected,
                                discoveryResult.Completion,
                                discoveryResult.Information,
                                discoveryResult.Issues,
                                discoveryResult.Failure),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case GetDiscoveryOccurrenceCommand command when established:
                    var occurrenceResult = await discovery
                        .GetOccurrenceAsync(
                            command.Lookup,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await connection.SendAsync(
                            new GetDiscoveryOccurrenceResponse(
                                Guid.CreateVersion7(),
                                DateTimeOffset.UtcNow,
                                command.MessageId,
                                command.Lookup.DiscoveryOperationId,
                                occurrenceResult.Accepted
                                    ? CommandAcceptance.Accepted
                                    : CommandAcceptance.Rejected,
                                occurrenceResult.Occurrence,
                                occurrenceResult.Failure),
                            cancellationToken)
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

        if (activeProcessingRequest is not null)
        {
            await ObserveProcessingRequestAsync(activeProcessingRequest).ConfigureAwait(false);
        }
    }

    private async Task ProcessDatabaseBuildAsync(
        NamedPipeIpcConnection connection,
        BuildDatabaseCommand command,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        var result = await databaseGeneration
            .BuildAsync(
                command.Correlation,
                command.Sources,
                command.Mapping,
                cancellationToken)
            .ConfigureAwait(false);
        var response = new BuildDatabaseResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            result.Accepted
                ? CommandAcceptance.Accepted
                : CommandAcceptance.Rejected,
            result.Completion,
            result.PublishedGeneration,
            result.Failure);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task ProcessExtractionAsync(
        NamedPipeIpcConnection connection,
        RunExtractionCommand command,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        var result = await databaseExtraction
            .ExtractAsync(
                command.Correlation,
                command.DatabaseGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        var response = new RunExtractionResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            result.Accepted
                ? CommandAcceptance.Accepted
                : CommandAcceptance.Rejected,
            result.Completion,
            result.PublishedResult,
            result.Failure);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task ObserveProcessingRequestAsync(Task processingRequest)
    {
        try
        {
            await processingRequest.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The active processing request failed unexpectedly");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
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

    private static async ValueTask SendAcceptedSerializedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAcceptedAsync(connection, commandMessageId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async ValueTask SendRejectedSerializedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        string failureCode,
        string failureDescription,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendRejectedAsync(
                    connection,
                    commandMessageId,
                    failureCode,
                    failureDescription,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static ValueTask SendRejectedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        CancellationToken cancellationToken)
    {
        return SendRejectedAsync(
            connection,
            commandMessageId,
            "invalid-lifecycle-sequence",
            "The message is not valid in the current Processing Host lifecycle state.",
            cancellationToken);
    }

    private static ValueTask SendRejectedAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        string failureCode,
        string failureDescription,
        CancellationToken cancellationToken)
    {
        return connection.SendAsync(
            new CommandAcknowledgement(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                commandMessageId,
                CommandAcceptance.Rejected,
                new IpcFailure(
                    failureCode,
                    failureDescription)),
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
