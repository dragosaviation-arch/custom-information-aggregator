using System.Windows;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = DesktopApplicationHost.Create(e.Args);
        _host.StartAsync().GetAwaiter().GetResult();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("CIA process started with role {ProcessRole}", DesktopApplicationHost.ProcessRole);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var logger = _host.Services.GetRequiredService<ILogger<App>>();
                logger.LogInformation("CIA process stopping with role {ProcessRole}", DesktopApplicationHost.ProcessRole);
                _host.StopAsync().GetAwaiter().GetResult();
            }
        }
        finally
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
