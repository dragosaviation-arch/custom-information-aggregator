using System.Windows;
using System.Windows.Threading;

namespace CIA.Desktop.Tests;

internal static class WpfTestApplication
{
    private static readonly Lazy<WpfApplicationHost> Host = new(
        () => new WpfApplicationHost(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Host.Value.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal).Task;
    }

    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Host.Value.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal).Task.Unwrap();
    }

    public static void Shutdown()
    {
        if (Host.IsValueCreated)
        {
            Host.Value.Dispose();
        }
    }

    private sealed class WpfApplicationHost : IDisposable
    {
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        private readonly Thread _thread;
        private App? _application;
        private Exception? _startupFailure;
        private int _disposed;

        public WpfApplicationHost()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => RunApplication(ready))
            {
                IsBackground = true,
                Name = "CIA Desktop test application"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!ready.Wait(StartupTimeout))
            {
                throw new TimeoutException("The WPF test application did not start.");
            }

            if (_startupFailure is not null)
            {
                throw new InvalidOperationException(
                    "The WPF test application could not start.",
                    _startupFailure);
            }
        }

        public Dispatcher Dispatcher { get; private set; } = null!;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(
                    () =>
                    {
                        _application?.Shutdown();
                        Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    });
            }

            if (!_thread.Join(StartupTimeout))
            {
                throw new TimeoutException("The WPF test application did not stop.");
            }
        }

        private void RunApplication(ManualResetEventSlim ready)
        {
            try
            {
                _application = new App();
                _application.InitializeComponent();
                _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                _startupFailure = exception;
                ready.Set();
            }
        }
    }
}

[TestClass]
public sealed class WpfTestAssemblyCleanup
{
    [AssemblyCleanup]
    public static void Cleanup()
    {
        WpfTestApplication.Shutdown();
    }
}
