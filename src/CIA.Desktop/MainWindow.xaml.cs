using System.Windows;
using System.Windows.Input;
using CIA.Desktop.Presentation;

namespace CIA.Desktop;

public partial class MainWindow : Window
{
    private const double CompactNavigationBreakpoint = 1180;

    public MainWindow(
        MainWindowViewModel viewModel,
        GlobalStatusViewModel globalStatus,
        LoadWorkspaceViewModel loadWorkspace,
        DiscoveryWorkspaceViewModel discoveryWorkspace,
        DatabaseWorkspaceViewModel databaseWorkspace,
        SettingsWorkspaceViewModel settingsWorkspace)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(globalStatus);
        ArgumentNullException.ThrowIfNull(loadWorkspace);
        ArgumentNullException.ThrowIfNull(discoveryWorkspace);
        ArgumentNullException.ThrowIfNull(databaseWorkspace);
        ArgumentNullException.ThrowIfNull(settingsWorkspace);

        GlobalStatus = globalStatus;
        LoadWorkspace = loadWorkspace;
        DiscoveryWorkspace = discoveryWorkspace;
        DatabaseWorkspace = databaseWorkspace;
        SettingsWorkspace = settingsWorkspace;
        InitializeComponent();
        DataContext = viewModel;
    }

    public GlobalStatusViewModel GlobalStatus { get; }

    public LoadWorkspaceViewModel LoadWorkspace { get; }

    public DiscoveryWorkspaceViewModel DiscoveryWorkspace { get; }

    public DatabaseWorkspaceViewModel DatabaseWorkspace { get; }

    public SettingsWorkspaceViewModel SettingsWorkspace { get; }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkspaceTabs is null)
        {
            return;
        }

        WorkspaceTabs.Resources["CiaNavigationSubtitleVisibility"] = ActualWidth < CompactNavigationBreakpoint
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape)
        {
            return;
        }

        var cancellationCommand = GlobalStatus.CancelActiveOperationCommand;
        if (!cancellationCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        cancellationCommand.Execute(null);
    }
}
