using System.Windows;
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
        DatabaseWorkspaceViewModel databaseWorkspace)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(globalStatus);
        ArgumentNullException.ThrowIfNull(loadWorkspace);
        ArgumentNullException.ThrowIfNull(discoveryWorkspace);
        ArgumentNullException.ThrowIfNull(databaseWorkspace);

        GlobalStatus = globalStatus;
        LoadWorkspace = loadWorkspace;
        DiscoveryWorkspace = discoveryWorkspace;
        DatabaseWorkspace = databaseWorkspace;
        InitializeComponent();
        DataContext = viewModel;
    }

    public GlobalStatusViewModel GlobalStatus { get; }

    public LoadWorkspaceViewModel LoadWorkspace { get; }

    public DiscoveryWorkspaceViewModel DiscoveryWorkspace { get; }

    public DatabaseWorkspaceViewModel DatabaseWorkspace { get; }

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
}
