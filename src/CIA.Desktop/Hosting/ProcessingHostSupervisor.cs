using System.Diagnostics;
using System.IO;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Desktop.Ipc;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Hosting;

public sealed class ProcessingHostSupervisor : IProcessingHostSupervisor, IDisposable, IAsyncDisposable
{
    private readonly ProcessingHostSupervisorOptions _options;
    private readonly IProcessingHostProcessLauncher _processLauncher;
    private readonly ILogger<ProcessingHostSupervisor> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Guid _clientInstanceId = Guid.CreateVersion7();

    private ProcessingHostLifecycleSnapshot _current = new(
        ProcessingHostLifecycleState.Stopped,
        HostDesired: false,
        ProcessId: null,
        FailureCode: null);
    private Process? _process;
    private NamedPipeIpcConnection? _connection;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private long _generation;
    private bool _hostDesired;
    private bool _stopping;
    private bool _automaticRecreationAttempted;
    private int _disposed;

    public ProcessingHostSupervisor(
        ProcessingHostSupervisorOptions options,
        IProcessingHostProcessLauncher processLauncher,
        ILogger<ProcessingHostSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    public ProcessingHostLifecycleSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged;

    public async Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            _hostDesired = true;
            _stopping = false;

            if (IsCurrentHostReady())
            {
                return Current;
            }

            _automaticRecreationAttempted = false;
            return await StartHostAsync(isAutomaticRecreation: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestOperationCancellationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrentHostReady())
            {
                return false;
            }

            var connection = _connection!;
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var commandCancellation = new CancellationTokenSource(
                    _options.LivenessTimeout);
                var command = new CancelOperationCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    operationId);
                await connection.SendAsync(command, commandCancellation.Token).ConfigureAwait(false);
                var acknowledgement = await ReceiveAcknowledgementAsync(
                        connection,
                        command.MessageId,
                        commandCancellation.Token)
                    .ConfigureAwait(false);
                return acknowledgement.Acceptance == CommandAcceptance.Accepted;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LoadSourcesResponse> RequestSourceLoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrentHostReady())
            {
                throw new InvalidOperationException(
                    "The Processing Host is not ready for source-loading requests.");
            }

            var connection = _connection!;
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var command = new LoadSourcesCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    selectionKind,
                    path,
                    settings);
                await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
                var response = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (response is not LoadSourcesResponse sourceResponse
                    || sourceResponse.CommandMessageId != command.MessageId)
                {
                    throw new IpcProtocolException(
                        IpcProtocolError.InvalidContract,
                        "The Processing Host returned an invalid source-loading response.");
                }

                return sourceResponse;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RefreshSourceResponse> RequestSourceRefreshAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrentHostReady())
            {
                throw new InvalidOperationException(
                    "The Processing Host is not ready for source-refresh requests.");
            }

            var connection = _connection!;
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var command = new RefreshSourceCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    source);
                await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
                var response = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (response is not RefreshSourceResponse sourceResponse
                    || sourceResponse.CommandMessageId != command.MessageId
                    || sourceResponse.Source.SourceId != source.SourceId)
                {
                    throw new IpcProtocolException(
                        IpcProtocolError.InvalidContract,
                        "The Processing Host returned an invalid source-refresh response.");
                }

                return sourceResponse;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunDiscoveryResponse> RequestDiscoveryAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(sources);
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrentHostReady())
            {
                throw new InvalidOperationException(
                    "The Processing Host is not ready for Discovery requests.");
            }

            var connection = _connection!;
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var command = new RunDiscoveryCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    correlation,
                    sources);
                await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
                var response = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (response is not RunDiscoveryResponse discoveryResponse
                    || discoveryResponse.CommandMessageId != command.MessageId
                    || discoveryResponse.Completion.Correlation != correlation)
                {
                    throw new IpcProtocolException(
                        IpcProtocolError.InvalidContract,
                        "The Processing Host returned an invalid Discovery response.");
                }

                return discoveryResponse;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<GetDiscoveryOccurrenceResponse> RequestDiscoveryOccurrenceAsync(
        OperationId discoveryOperationId,
        string informationType,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsCurrentHostReady())
            {
                throw new InvalidOperationException(
                    "The Processing Host is not ready for Discovery occurrence requests.");
            }

            var connection = _connection!;
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var command = new GetDiscoveryOccurrenceCommand(
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    discoveryOperationId,
                    informationType,
                    ordinal);
                await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
                var response = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (response is not GetDiscoveryOccurrenceResponse occurrenceResponse
                    || occurrenceResponse.CommandMessageId != command.MessageId
                    || occurrenceResponse.DiscoveryOperationId != discoveryOperationId
                    || occurrenceResponse.Occurrence is { } occurrence
                    && (!string.Equals(
                            occurrence.InformationType,
                            informationType,
                            StringComparison.Ordinal)
                        || occurrence.Ordinal != ordinal))
                {
                    throw new IpcProtocolException(
                        IpcProtocolError.InvalidContract,
                        "The Processing Host returned an invalid Discovery occurrence response.");
                }

                return occurrenceResponse;
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        _requestGate.Dispose();
        _lifecycleGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _requestGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task<ProcessingHostLifecycleSnapshot> StartHostAsync(
        bool isAutomaticRecreation,
        CancellationToken cancellationToken)
    {
        var startingState = isAutomaticRecreation
            ? ProcessingHostLifecycleState.Recreating
            : ProcessingHostLifecycleState.Starting;
        SetCurrent(startingState, processId: null, failureCode: null);

        if (!File.Exists(_options.ExecutablePath))
        {
            var failure = new ProcessingHostLifecycleException(
                ProcessingHostLifecycleError.ExecutableNotFound,
                $"The companion Processing Host executable was not found at '{_options.ExecutablePath}'.");
            SetCurrent(ProcessingHostLifecycleState.Faulted, processId: null, failure.Error.ToString());
            throw failure;
        }

        var pipeName = _options.CreatePipeName();
        Process? process = null;
        NamedPipeIpcConnection? connection = null;

        try
        {
            var startInfo = CreateStartInfo(pipeName);

            try
            {
                process = _processLauncher.Start(startInfo);
            }
            catch (Exception exception) when (exception is not ProcessingHostLifecycleException)
            {
                throw new ProcessingHostLifecycleException(
                    ProcessingHostLifecycleError.ProcessLaunchFailed,
                    "The companion Processing Host process could not be launched.",
                    exception);
            }

            SetCurrent(startingState, process.Id, failureCode: null);
            _logger.LogInformation(
                "Processing Host process launched with process ID {ProcessingHostProcessId} and lifecycle state {LifecycleState}",
                process.Id,
                startingState);

            using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessCancellation.CancelAfter(_options.StartupTimeout);

            try
            {
                connection = await ProcessingHostIpcClient
                    .ConnectAsync(pipeName, readinessCancellation.Token)
                    .ConfigureAwait(false);
                await EstablishReadyConnectionAsync(connection, readinessCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw new ProcessingHostLifecycleException(
                    ProcessingHostLifecycleError.ReadinessTimedOut,
                    "The Processing Host did not establish IPC readiness within the bounded startup period.",
                    exception);
            }
            catch (Exception exception) when (exception is not ProcessingHostLifecycleException)
            {
                throw new ProcessingHostLifecycleException(
                    ProcessingHostLifecycleError.InvalidReadinessResponse,
                    "The Processing Host did not complete the expected IPC readiness handshake.",
                    exception);
            }

            _process = process;
            _connection = connection;
            process = null;
            connection = null;

            var generation = ++_generation;
            SetCurrent(ProcessingHostLifecycleState.Ready, _process.Id, failureCode: null);
            _logger.LogInformation(
                "Processing Host is ready with process ID {ProcessingHostProcessId}",
                _process.Id);

            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorHostAsync(
                _process,
                _connection,
                generation,
                _monitorCancellation.Token);

            return Current;
        }
        catch (ProcessingHostLifecycleException exception)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (process is not null)
            {
                await TerminateFailedStartAsync(process).ConfigureAwait(false);
            }

            var error = isAutomaticRecreation
                ? ProcessingHostLifecycleError.AutomaticRecreationFailed
                : exception.Error;
            SetCurrent(ProcessingHostLifecycleState.Faulted, processId: null, error.ToString());

            if (isAutomaticRecreation)
            {
                throw new ProcessingHostLifecycleException(
                    error,
                    "Automatic Processing Host recreation failed and will not be retried in a loop.",
                    exception);
            }

            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(string pipeName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add($"--{ProcessingHostRuntimeContract.PipeNameConfigurationKey}={pipeName}");
        startInfo.ArgumentList.Add(
            $"--{ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey}={Environment.ProcessId}");

        if (_options.LogDirectory is not null)
        {
            startInfo.ArgumentList.Add(
                $"--{ApplicationLogPaths.DirectoryConfigurationKey}={_options.LogDirectory}");
        }

        return startInfo;
    }

    private async Task EstablishReadyConnectionAsync(
        NamedPipeIpcConnection connection,
        CancellationToken cancellationToken)
    {
        var command = new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            _clientInstanceId,
            IpcProtocol.CurrentVersion);

        await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
        await ReceiveAcceptedAcknowledgementAsync(connection, command.MessageId, cancellationToken)
            .ConfigureAwait(false);

        var availability = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        if (availability is not ProcessingHostAvailabilityEvent
            {
                Availability: ProcessingHostAvailability.Ready
            })
        {
            throw new ProcessingHostLifecycleException(
                ProcessingHostLifecycleError.InvalidReadinessResponse,
                "The Processing Host did not report ready after the IPC connection was established.");
        }
    }

    private async Task MonitorHostAsync(
        Process process,
        NamedPipeIpcConnection connection,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var processExitTask = process.WaitForExitAsync(cancellationToken);

            while (true)
            {
                var delayTask = Task.Delay(_options.LivenessInterval, cancellationToken);
                var completedTask = await Task.WhenAny(processExitTask, delayTask).ConfigureAwait(false);

                if (completedTask == processExitTask)
                {
                    await processExitTask.ConfigureAwait(false);
                    await HandleUnexpectedLossAsync(
                        process,
                        connection,
                        generation,
                        "ProcessExited",
                        GetExitCode(process)).ConfigureAwait(false);
                    return;
                }

                await delayTask.ConfigureAwait(false);
                await VerifyLivenessAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await HandleUnexpectedLossAsync(
                process,
                connection,
                generation,
                "IpcUnavailable",
                GetExitCode(process),
                exception).ConfigureAwait(false);
        }
    }

    private async Task VerifyLivenessAsync(
        NamedPipeIpcConnection connection,
        CancellationToken monitorCancellation)
    {
        var command = new ProcessingHostLivenessCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        await _requestGate.WaitAsync(monitorCancellation).ConfigureAwait(false);

        try
        {
            using var livenessCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                monitorCancellation);
            livenessCancellation.CancelAfter(_options.LivenessTimeout);
            await connection.SendAsync(command, livenessCancellation.Token).ConfigureAwait(false);
            await ReceiveAcceptedAcknowledgementAsync(
                connection,
                command.MessageId,
                livenessCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task HandleUnexpectedLossAsync(
        Process process,
        NamedPipeIpcConnection connection,
        long generation,
        string lossReason,
        int? exitCode,
        Exception? exception = null)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (generation != _generation || !ReferenceEquals(process, _process))
            {
                return;
            }

            _logger.LogWarning(
                exception,
                "Processing Host became unavailable for reason {LossReason} with process ID {ProcessingHostProcessId} and exit code {ExitCode}",
                lossReason,
                TryGetProcessId(process),
                exitCode);

            _connection = null;
            _process = null;
            _monitorCancellation = null;
            _monitorTask = null;

            await connection.DisposeAsync().ConfigureAwait(false);

            if (!await WaitForExitAsync(process, _options.ShutdownTimeout).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Terminating unavailable owned Processing Host process ID {ProcessingHostProcessId} before recreation",
                    TryGetProcessId(process));
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, _options.ShutdownTimeout).ConfigureAwait(false);
            }

            process.Dispose();

            if (!_hostDesired || _stopping || Volatile.Read(ref _disposed) != 0)
            {
                SetCurrent(ProcessingHostLifecycleState.Stopped, processId: null, failureCode: null);
                return;
            }

            if (_automaticRecreationAttempted)
            {
                const string failureCode = "AutomaticRecreationExhausted";
                SetCurrent(ProcessingHostLifecycleState.Faulted, processId: null, failureCode);
                _logger.LogError(
                    "Processing Host automatic recreation is exhausted; no restart loop will be started");
                return;
            }

            _automaticRecreationAttempted = true;
            _logger.LogInformation(
                "Starting one clean idle Processing Host recreation after unexpected loss; no workload will be retried or resumed");

            try
            {
                await StartHostAsync(isAutomaticRecreation: true, CancellationToken.None)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Processing Host recreation completed with process ID {ProcessingHostProcessId}; host is clean and idle",
                    Current.ProcessId);
            }
            catch (ProcessingHostLifecycleException recreationException)
            {
                _logger.LogError(
                    recreationException,
                    "Processing Host recreation failed in a controlled state; no further automatic attempt will be made");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Task? monitorTask;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _hostDesired = false;
            _stopping = true;
            monitorTask = _monitorTask;
            _monitorCancellation?.Cancel();

            if (_process is not null)
            {
                SetCurrent(ProcessingHostLifecycleState.Stopping, _process.Id, failureCode: null);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(_options.LivenessTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Processing Host liveness monitor did not stop within the bounded cancellation period");
            }
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            var process = _process;
            var connection = _connection;

            if (process is null)
            {
                _stopping = false;
                _automaticRecreationAttempted = false;
                SetCurrent(ProcessingHostLifecycleState.Stopped, processId: null, failureCode: null);
                return;
            }

            var processId = TryGetProcessId(process);
            var cleanShutdownRequested = false;

            if (connection is not null && connection.IsConnected)
            {
                try
                {
                    using var shutdownCancellation = new CancellationTokenSource(_options.ShutdownTimeout);
                    var command = new StopProcessingHostCommand(
                        Guid.CreateVersion7(),
                        DateTimeOffset.UtcNow);
                    await connection.SendAsync(command, shutdownCancellation.Token).ConfigureAwait(false);
                    await ReceiveAcceptedAcknowledgementAsync(
                        connection,
                        command.MessageId,
                        shutdownCancellation.Token).ConfigureAwait(false);
                    cleanShutdownRequested = true;
                    _logger.LogInformation(
                        "Processing Host clean shutdown requested for process ID {ProcessingHostProcessId}",
                        processId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Processing Host clean shutdown request could not be acknowledged for process ID {ProcessingHostProcessId}",
                        processId);
                }
                catch (OperationCanceledException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Processing Host clean shutdown acknowledgement timed out for process ID {ProcessingHostProcessId}",
                        processId);
                }
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            var exitedCleanly = await WaitForExitAsync(process, _options.ShutdownTimeout)
                .ConfigureAwait(false);

            if (!exitedCleanly)
            {
                _logger.LogWarning(
                    "Processing Host did not exit during bounded shutdown; force-terminating owned process ID {ProcessingHostProcessId}",
                    processId);
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, _options.ShutdownTimeout).ConfigureAwait(false);
            }
            else if (cleanShutdownRequested)
            {
                _logger.LogInformation(
                    "Processing Host exited cleanly with process ID {ProcessingHostProcessId}",
                    processId);
            }

            process.Dispose();
            _connection = null;
            _process = null;
            _monitorCancellation?.Dispose();
            _monitorCancellation = null;
            _monitorTask = null;
            _stopping = false;
            _automaticRecreationAttempted = false;
            SetCurrent(ProcessingHostLifecycleState.Stopped, processId: null, failureCode: null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static async Task ReceiveAcceptedAcknowledgementAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        CancellationToken cancellationToken)
    {
        var acknowledgement = await ReceiveAcknowledgementAsync(
                connection,
                commandMessageId,
                cancellationToken)
            .ConfigureAwait(false);

        if (acknowledgement is not
            {
                Acceptance: CommandAcceptance.Accepted,
                Failure: null
            })
        {
            throw new ProcessingHostLifecycleException(
                ProcessingHostLifecycleError.InvalidReadinessResponse,
                "The Processing Host returned an invalid lifecycle-command acknowledgement.");
        }
    }

    private static async Task<CommandAcknowledgement> ReceiveAcknowledgementAsync(
        NamedPipeIpcConnection connection,
        Guid commandMessageId,
        CancellationToken cancellationToken)
    {
        var response = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        if (response is not CommandAcknowledgement acknowledgement
            || acknowledgement.CommandMessageId != commandMessageId)
        {
            throw new ProcessingHostLifecycleException(
                ProcessingHostLifecycleError.InvalidReadinessResponse,
                "The Processing Host returned an invalid lifecycle-command acknowledgement.");
        }

        return acknowledgement;
    }

    private async Task TerminateFailedStartAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, _options.ShutdownTimeout).ConfigureAwait(false);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private bool IsCurrentHostReady()
    {
        return Current.State == ProcessingHostLifecycleState.Ready
            && _process is not null
            && !_process.HasExited
            && _connection is { IsConnected: true };
    }

    private void SetCurrent(
        ProcessingHostLifecycleState state,
        int? processId,
        string? failureCode)
    {
        ProcessingHostLifecycleSnapshot snapshot;

        lock (_stateGate)
        {
            snapshot = new ProcessingHostLifecycleSnapshot(
                state,
                _hostDesired,
                processId,
                failureCode);
            _current = snapshot;
        }

        StateChanged?.Invoke(this, snapshot);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static int? GetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
