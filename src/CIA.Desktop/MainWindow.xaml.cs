using System.Windows;
using CIA.Desktop.Presentation;

namespace CIA.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(
        MainWindowViewModel viewModel,
        GlobalStatusViewModel globalStatus)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(globalStatus);

        GlobalStatus = globalStatus;
        InitializeComponent();
        DataContext = viewModel;
    }

    public GlobalStatusViewModel GlobalStatus { get; }
}
