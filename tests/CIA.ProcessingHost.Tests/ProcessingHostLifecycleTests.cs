using System.Collections.Concurrent;
using System.Diagnostics;
using CIA.Contracts.Ipc;
using CIA.Core.Diagnostics;
using CIA.Desktop.Hosting;
using CIA.ProcessingHost.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingHostLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task DesktopStartupLeavesProcessingHostStoppedUntilExplicitlyRequired()
    {
        using var logs = new TemporaryLifecycleLogDirectory();
        using var host = DesktopApplicationHost.Create(CreateLogArguments(logs.Path));

        await host.StartAsync();
        var supervisor = host.Services.GetRequiredService<IProcessingHostSupervisor>();

        Assert.AreEqual(ProcessingHostLifecycleState.Stopped, supervisor.Current.State);
        Assert.IsFalse(supervisor.Current.HostDesired);
        Assert.IsNull(supervisor.Current.ProcessId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task OnDemandRequestLaunchesRealHostAndWaitsForNamedPipeReadiness()
    {
        await using var fixture = new SupervisorFixture();
        var states = new List<ProcessingHostLifecycleSnapshot>();
        fixture.Supervisor.StateChanged += (_, state) => states.Add(state);

        var ready = await fixture.Supervisor.EnsureAvailableAsync();

        Assert.AreEqual(ProcessingHostLifecycleState.Ready, ready.State);
        Assert.IsTrue(ready.HostDesired);
        Assert.IsNotNull(ready.ProcessId);
        Assert.AreEqual(1, fixture.Launcher.AttemptCount);
        AssertProcessIsRunning(ready.ProcessId.Value);

        var processStartedIndex = states.FindIndex(
            state => state.State == ProcessingHostLifecycleState.Starting
                && state.ProcessId == ready.ProcessId);
        var readyIndex = states.FindIndex(
            state => state.State == ProcessingHostLifecycleState.Ready
                && state.ProcessId == ready.ProcessId);

        Assert.IsGreaterThanOrEqualTo(0, processStartedIndex);
        Assert.IsGreaterThan(processStartedIndex, readyIndex);

        await Task.Delay(TimeSpan.FromMilliseconds(350));
        AssertProcessIsRunning(ready.ProcessId.Value);
    }

    [TestMethod]
    public async Task ConcurrentAvailabilityRequestsDoNotCreateDuplicateHosts()
    {
        await using var fixture = new SupervisorFixture();

        var requests = Enumerable.Range(0, 8)
            .Select(_ => fixture.Supervisor.EnsureAvailableAsync())
            .ToArray();
        var results = await Task.WhenAll(requests);

        Assert.AreEqual(1, fixture.Launcher.AttemptCount);
        Assert.AreEqual(1, results.Select(result => result.ProcessId).Distinct().Count());
        Assert.IsTrue(results.All(result => result.State == ProcessingHostLifecycleState.Ready));
    }

    [TestMethod]
    public async Task IntentionalSupervisorStopDoesNotRecreateTheHost()
    {
        await using var fixture = new SupervisorFixture();
        var ready = await fixture.Supervisor.EnsureAvailableAsync();
        var processId = ready.ProcessId!.Value;

        await fixture.Supervisor.StopAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(1, fixture.Launcher.AttemptCount);
        Assert.AreEqual(ProcessingHostLifecycleState.Stopped, fixture.Supervisor.Current.State);
        Assert.IsFalse(fixture.Supervisor.Current.HostDesired);
        AssertProcessHasExited(processId);
    }

    [TestMethod]
    public async Task DesktopGenericHostShutdownStopsChildCleanlyWithoutRecreation()
    {
        using var logs = new TemporaryLifecycleLogDirectory();
        using var host = DesktopApplicationHost.Create(CreateLogArguments(logs.Path));
        await host.StartAsync();

        var supervisor = host.Services.GetRequiredService<IProcessingHostSupervisor>();
        var ready = await supervisor.EnsureAvailableAsync();
        var processId = ready.ProcessId!.Value;
        using var observedProcess = Process.GetProcessById(processId);

        await host.StopAsync();
        await observedProcess.WaitForExitAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(ProcessingHostLifecycleState.Stopped, supervisor.Current.State);
        Assert.IsFalse(supervisor.Current.HostDesired);
        Assert.IsNull(supervisor.Current.ProcessId);
        host.Dispose();

        var uiLog = ReadSingleLog(logs.Path, "cia-ui-*.clef");
        var processingHostLog = ReadSingleLog(logs.Path, "cia-processing-host-*.clef");
        StringAssert.Contains(uiLog, "Processing Host clean shutdown requested");
        StringAssert.Contains(uiLog, "Processing Host exited cleanly");
        Assert.IsFalse(uiLog.Contains("force-terminating", StringComparison.Ordinal));
        StringAssert.Contains(processingHostLog, "accepted a controlled shutdown request");

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        AssertProcessHasExited(processId);
    }

    [TestMethod]
    public async Task UnexpectedHostTerminationCreatesOneNewCleanReadyHostWithoutWorkloadArguments()
    {
        await using var fixture = new SupervisorFixture();
        var first = await fixture.Supervisor.EnsureAvailableAsync();
        var firstProcessId = first.ProcessId!.Value;

        KillOwnedProcess(firstProcessId);

        var recreated = await WaitForStateAsync(
            fixture.Supervisor,
            state => state.State == ProcessingHostLifecycleState.Ready
                && state.ProcessId is not null
                && state.ProcessId != firstProcessId);

        Assert.AreEqual(2, fixture.Launcher.AttemptCount);
        Assert.IsTrue(recreated.HostDesired);
        AssertProcessIsRunning(recreated.ProcessId!.Value);

        foreach (var arguments in fixture.Launcher.StartArguments)
        {
            Assert.HasCount(3, arguments);
            Assert.IsTrue(arguments.Any(
                argument => argument.StartsWith(
                    $"--{ProcessingHostRuntimeContract.PipeNameConfigurationKey}=",
                    StringComparison.Ordinal)));
            Assert.IsTrue(arguments.Any(
                argument => argument.StartsWith(
                    $"--{ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey}=",
                    StringComparison.Ordinal)));
            Assert.IsTrue(arguments.Any(
                argument => argument.StartsWith(
                    $"--{ApplicationLogPaths.DirectoryConfigurationKey}=",
                    StringComparison.Ordinal)));
            Assert.IsFalse(arguments.Any(argument =>
                argument.Contains("workload", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("retry", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("resume", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("operation", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task FailedAutomaticRecreationEntersControlledFaultWithoutRestartLoop()
    {
        await using var fixture = new SupervisorFixture(failLaunchAttempt: 2);
        var first = await fixture.Supervisor.EnsureAvailableAsync();

        KillOwnedProcess(first.ProcessId!.Value);

        var faulted = await WaitForStateAsync(
            fixture.Supervisor,
            state => state.State == ProcessingHostLifecycleState.Faulted);

        Assert.AreEqual("AutomaticRecreationFailed", faulted.FailureCode);
        Assert.AreEqual(2, fixture.Launcher.AttemptCount);
        Assert.IsNull(faulted.ProcessId);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, fixture.Launcher.AttemptCount);
        Assert.AreEqual(ProcessingHostLifecycleState.Faulted, fixture.Supervisor.Current.State);
    }

    [TestMethod]
    public async Task BrokenLifecyclePipeStopsManagedHostInsteadOfLeavingItResident()
    {
        using var logs = new TemporaryLifecycleLogDirectory();
        using var timeout = new CancellationTokenSource(TestTimeout);
        var pipeName = $"CIA.Tests.SPR55.{Guid.NewGuid():N}";
        string[] arguments =
        [
            $"--{ProcessingHostRuntimeContract.PipeNameConfigurationKey}={pipeName}",
            $"--{ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey}={Environment.ProcessId}",
            $"--{ApplicationLogPaths.DirectoryConfigurationKey}={logs.Path}"
        ];
        using var host = ProcessingHostApplicationHost.Create(arguments);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(timeout.Token);
        await using (var connection = await CIA.Desktop.Ipc.ProcessingHostIpcClient
                         .ConnectAsync(pipeName, timeout.Token))
        {
            await CompleteReadinessHandshakeAsync(connection, timeout.Token);
        }

        await WaitForCancellationAsync(lifetime.ApplicationStopping, timeout.Token);
        await host.StopAsync(timeout.Token);
        host.Dispose();

        var processingHostLog = ReadSingleLog(logs.Path, "cia-processing-host-*.clef");
        StringAssert.Contains(processingHostLog, "lifecycle IPC connection was lost");
    }

    [TestMethod]
    public void ManagedProcessingHostUsesGenericHostedLifetimeWithoutChangingProjectBoundaries()
    {
        using var logs = new TemporaryLifecycleLogDirectory();
        var pipeName = $"CIA.Tests.SPR55.{Guid.NewGuid():N}";
        string[] arguments =
        [
            $"--{ProcessingHostRuntimeContract.PipeNameConfigurationKey}={pipeName}",
            $"--{ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey}={Environment.ProcessId}",
            $"--{ApplicationLogPaths.DirectoryConfigurationKey}={logs.Path}"
        ];
        using var host = ProcessingHostApplicationHost.Create(arguments);

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        Assert.IsTrue(hostedServices.Any(service => service is ProcessingHostLifetimeService));

        var desktopReferences = typeof(ProcessingHostSupervisor).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var processingHostReferences = typeof(ProcessingHostLifetimeService).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(desktopReferences, "CIA.ProcessingHost");
        CollectionAssert.DoesNotContain(processingHostReferences, "CIA.Desktop");
        CollectionAssert.DoesNotContain(desktopReferences, "System.ServiceProcess.ServiceController");
        CollectionAssert.DoesNotContain(processingHostReferences, "System.ServiceProcess.ServiceController");
    }

    [TestMethod]
    public async Task DirectProcessingHostInvocationWithoutSupervisorContractExitsNormally()
    {
        using var logs = new TemporaryLifecycleLogDirectory();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveProcessingHostExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(
            $"--{ApplicationLogPaths.DirectoryConfigurationKey}={logs.Path}");

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);

        await process.WaitForExitAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(0, process.ExitCode);
    }

    private static async Task CompleteReadinessHandshakeAsync(
        NamedPipeIpcConnection connection,
        CancellationToken cancellationToken)
    {
        var command = new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);
        await connection.SendAsync(command, cancellationToken);

        var acknowledgement = await connection.ReceiveAsync(cancellationToken);
        Assert.IsInstanceOfType<CommandAcknowledgement>(acknowledgement);
        Assert.AreEqual(command.MessageId, ((CommandAcknowledgement)acknowledgement).CommandMessageId);

        var availability = await connection.ReceiveAsync(cancellationToken);
        Assert.IsInstanceOfType<ProcessingHostAvailabilityEvent>(availability);
        Assert.AreEqual(
            ProcessingHostAvailability.Ready,
            ((ProcessingHostAvailabilityEvent)availability).Availability);
    }

    private static async Task<ProcessingHostLifecycleSnapshot> WaitForStateAsync(
        IProcessingHostSupervisor supervisor,
        Func<ProcessingHostLifecycleSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = supervisor.Current;

            if (predicate(current))
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"Processing Host did not reach the expected state. Current state: {supervisor.Current}.");
        return supervisor.Current;
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken signal,
        CancellationToken timeout)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => completion.TrySetResult());
        await completion.Task.WaitAsync(timeout);
    }

    private static string ResolveProcessingHostExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CIA.ProcessingHost.exe");
        Assert.IsTrue(File.Exists(path), $"Processing Host test executable not found at '{path}'.");
        return path;
    }

    private static string[] CreateLogArguments(string logDirectory)
    {
        return [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}"];
    }

    private static string ReadSingleLog(string directory, string searchPattern)
    {
        var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, files);
        return File.ReadAllText(files[0]);
    }

    private static void AssertProcessIsRunning(int processId)
    {
        using var process = Process.GetProcessById(processId);
        Assert.IsFalse(process.HasExited);
    }

    private static void AssertProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.IsTrue(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void KillOwnedProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private sealed class SupervisorFixture : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly TemporaryLifecycleLogDirectory _logs = new();

        public SupervisorFixture(int? failLaunchAttempt = null)
        {
            Launcher = new TrackingProcessLauncher(failLaunchAttempt);
            _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace));
            Supervisor = new ProcessingHostSupervisor(
                new ProcessingHostSupervisorOptions(
                    ResolveProcessingHostExecutable(),
                    _logs.Path,
                    startupTimeout: TimeSpan.FromSeconds(10),
                    livenessInterval: TimeSpan.FromMilliseconds(100),
                    livenessTimeout: TimeSpan.FromSeconds(2),
                    shutdownTimeout: TimeSpan.FromSeconds(5)),
                Launcher,
                _loggerFactory.CreateLogger<ProcessingHostSupervisor>());
        }

        public TrackingProcessLauncher Launcher { get; }

        public ProcessingHostSupervisor Supervisor { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Supervisor.DisposeAsync();
            }
            finally
            {
                await Launcher.TerminateTrackedProcessesAsync();
                _loggerFactory.Dispose();
                _logs.Dispose();
            }
        }
    }

    private sealed class TrackingProcessLauncher(int? failLaunchAttempt)
        : IProcessingHostProcessLauncher
    {
        private readonly SystemProcessingHostProcessLauncher _inner = new();
        private readonly ConcurrentBag<int> _startedProcessIds = [];
        private readonly ConcurrentQueue<string[]> _startArguments = [];
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public IReadOnlyCollection<string[]> StartArguments => _startArguments.ToArray();

        public Process Start(ProcessStartInfo startInfo)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            _startArguments.Enqueue(startInfo.ArgumentList.ToArray());

            if (attempt == failLaunchAttempt)
            {
                throw new InvalidOperationException("Controlled test launch failure.");
            }

            var process = _inner.Start(startInfo);
            _startedProcessIds.Add(process.Id);
            return process;
        }

        public async Task TerminateTrackedProcessesAsync()
        {
            foreach (var processId in _startedProcessIds.Distinct())
            {
                try
                {
                    using var process = Process.GetProcessById(processId);

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TestTimeout);
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }

    private sealed class TemporaryLifecycleLogDirectory : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR55.Tests");

        public TemporaryLifecycleLogDirectory()
        {
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_testRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a lifecycle test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
