using System.Windows;
using CIA.Desktop.Presentation;

namespace CIA.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(
        MainWindowViewModel viewModel,
        GlobalStatusViewModel globalStatus,
        LoadWorkspaceViewModel loadWorkspace)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(globalStatus);
        ArgumentNullException.ThrowIfNull(loadWorkspace);

        GlobalStatus = globalStatus;
        LoadWorkspace = loadWorkspace;
        InitializeComponent();
        DataContext = viewModel;
    }

    public GlobalStatusViewModel GlobalStatus { get; }

    public LoadWorkspaceViewModel LoadWorkspace { get; }
}
