using CIA.Core.Diagnostics;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CIA.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationStartupSmokeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task DesktopResourcesAndPersistentShellComposeOnStaThread()
    {
        using var logs = new TemporaryStartupLogDirectory();
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () => RunDesktopComposition(logs.Path, completion))
        {
            IsBackground = true,
            Name = "CIA SPR-60 desktop startup smoke test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var failure = await completion.Task.WaitAsync(TestTimeout);

        Assert.IsTrue(thread.Join(TestTimeout), "The desktop startup STA thread did not exit.");
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void RunDesktopComposition(
        string logDirectory,
        TaskCompletionSource<Exception?> completion)
    {
        App? application = null;
        IHost? host = null;
        MainWindow? window = null;
        Exception? failure = null;

        try
        {
            application = new App();
            application.InitializeComponent();
            host = DesktopApplicationHost.Create(
                [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}"]);
            host.StartAsync().GetAwaiter().GetResult();

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            window = host.Services.GetRequiredService<MainWindow>();
            var viewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            var globalStatus = host.Services.GetRequiredService<GlobalStatusViewModel>();
            application.MainWindow = window;

            Assert.IsTrue(lifetime.ApplicationStarted.IsCancellationRequested);
            Assert.IsNotNull(window.Content);
            Assert.AreSame(viewModel, window.DataContext);
            Assert.AreSame(globalStatus, window.GlobalStatus);
            Assert.HasCount(4, viewModel.Workspaces);
            Assert.AreEqual(WorkspaceArea.Load, viewModel.SelectedWorkspace.Area);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            failure = CaptureCleanupFailure(failure, () => window?.Close());
            failure = CaptureCleanupFailure(
                failure,
                () =>
                {
                    try
                    {
                        host?.StopAsync().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        host?.Dispose();
                    }
                });
            failure = CaptureCleanupFailure(failure, () => application?.Shutdown());
            completion.TrySetResult(failure);
        }
    }

    private static Exception? CaptureCleanupFailure(Exception? currentFailure, Action cleanup)
    {
        try
        {
            cleanup();
            return currentFailure;
        }
        catch (Exception exception)
        {
            return currentFailure is null
                ? exception
                : new AggregateException(currentFailure, exception);
        }
    }

    private sealed class TemporaryStartupLogDirectory : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR60.Tests");

        public TemporaryStartupLogDirectory()
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
                    "Refusing to delete a startup test directory outside the test root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
