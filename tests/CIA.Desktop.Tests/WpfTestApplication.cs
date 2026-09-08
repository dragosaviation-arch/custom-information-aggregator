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
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Host.Value.Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            DispatcherPriority.Normal);
        return completion.Task;
    }

    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Host.Value.Dispatcher.BeginInvoke(
            async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            DispatcherPriority.Normal);
        return completion.Task;
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
        private Application? _application;
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
                _application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                _application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/CIA;component/Themes/CiaTheme.xaml",
                        UriKind.Relative)
                });
                Dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                _application.Run();
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
