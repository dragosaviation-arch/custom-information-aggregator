using System.Windows;
using CIA.Core;
using CIA.Desktop.Presentation;

namespace CIA.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var session = new ApplicationSession();
        var mainWindow = new MainWindow(new MainWindowViewModel(session));

        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
