using System.Windows;
using CIA.Contracts.Export;
using CIA.Desktop.Export;

namespace CIA.Desktop.Views;

public partial class PostExportPromptDialog : Window
{
    public PostExportPromptDialog(WorkbookExportBatchSummary batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        InitializeComponent();
        SummaryText = batch.Workbooks.Count == 1
            ? "1 workbook was published."
            : $"{batch.Workbooks.Count:N0} workbooks were published.";
        OutputDirectory = batch.OutputDirectory;
        DataContext = this;
    }

    public string SummaryText { get; }

    public string OutputDirectory { get; }

    public PostExportChoice Choice { get; private set; } = PostExportChoice.StatusOnly;

    private void OnOpenFilesClick(object sender, RoutedEventArgs e)
    {
        Choice = PostExportChoice.OpenExportedFiles;
        DialogResult = true;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Choice = PostExportChoice.OpenContainingFolder;
        DialogResult = true;
    }

    private void OnStatusOnlyClick(object sender, RoutedEventArgs e)
    {
        Choice = PostExportChoice.StatusOnly;
        DialogResult = false;
    }
}
