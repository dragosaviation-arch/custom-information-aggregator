using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CIA.Core.Runtime;

namespace CIA.Desktop.Views;

public partial class DatabaseWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private const double DefaultPaneSplitRatio = 0.5;
    private bool _isCompact;
    private bool _showExcelExport;

    public DatabaseWorkspaceView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty SettingsServiceProperty =
        DependencyProperty.Register(
            nameof(SettingsService),
            typeof(ApplicationSettingsService),
            typeof(DatabaseWorkspaceView));

    public ApplicationSettingsService? SettingsService
    {
        get => (ApplicationSettingsService?)GetValue(SettingsServiceProperty);
        set => SetValue(SettingsServiceProperty, value);
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        ColumnsPopup.IsOpen = !ColumnsPopup.IsOpen;
    }

    private void OnMetadataClick(object sender, RoutedEventArgs e)
    {
        MetadataPopup.IsOpen = !MetadataPopup.IsOpen;
    }

    private void OnExportFieldsTabClick(object sender, RoutedEventArgs e)
    {
        _showExcelExport = false;
        ApplyCompactPanelSelection();
    }

    private void OnExcelExportTabClick(object sender, RoutedEventArgs e)
    {
        _showExcelExport = true;
        ApplyCompactPanelSelection();
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var hostWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        _isCompact = hostWidth < CompactLayoutBreakpoint;
        if (_isCompact)
        {
            ApplyCompactLayout();
            return;
        }

        ApplyWideLayout();
    }

    private void ApplyWideLayout()
    {
        LowerTabs.Visibility = Visibility.Collapsed;
        var splitRatio = ResolvePaneSplitRatio(
            SettingsService?.Current.DatabasePaneSplitRatio,
            DefaultPaneSplitRatio);
        ExportFieldsColumn.MinWidth = 360;
        ExportFieldsColumn.Width = new GridLength(splitRatio, GridUnitType.Star);
        LowerPanelGapColumn.Width = new GridLength(8);
        ExcelExportColumn.MinWidth = 360;
        ExcelExportColumn.Width = new GridLength(1 - splitRatio, GridUnitType.Star);
        DatabasePaneSplitter.Visibility = Visibility.Visible;
        Grid.SetColumn(ExportFieldsPanel, 0);
        Grid.SetColumn(ExcelExportPanel, 2);
        ExportFieldsPanel.Visibility = Visibility.Visible;
        ExcelExportPanel.Visibility = Visibility.Visible;

        ExcelLocalColumn.Width = new GridLength(1, GridUnitType.Star);
        ExcelSettingsGapColumn.Width = new GridLength(12);
        ExcelInfoColumn.Width = new GridLength(1, GridUnitType.Star);
        ExcelTopRow.Height = GridLength.Auto;
        ExcelStackGapRow.Height = new GridLength(0);
        ExcelBottomRow.Height = new GridLength(0);
        Grid.SetRow(ExcelLocalSettings, 0);
        Grid.SetColumn(ExcelLocalSettings, 0);
        Grid.SetRow(ExcelInfo, 0);
        Grid.SetColumn(ExcelInfo, 2);
    }

    private void ApplyCompactLayout()
    {
        LowerTabs.Visibility = Visibility.Visible;
        ExportFieldsColumn.MinWidth = 0;
        ExportFieldsColumn.Width = new GridLength(1, GridUnitType.Star);
        LowerPanelGapColumn.Width = new GridLength(0);
        ExcelExportColumn.MinWidth = 0;
        ExcelExportColumn.Width = new GridLength(0);
        DatabasePaneSplitter.Visibility = Visibility.Collapsed;
        DatabasePaneSizeFeedback.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ExportFieldsPanel, 0);
        Grid.SetColumn(ExcelExportPanel, 0);

        ExcelLocalColumn.Width = new GridLength(1, GridUnitType.Star);
        ExcelSettingsGapColumn.Width = new GridLength(0);
        ExcelInfoColumn.Width = new GridLength(0);
        ExcelTopRow.Height = GridLength.Auto;
        ExcelStackGapRow.Height = new GridLength(8);
        ExcelBottomRow.Height = GridLength.Auto;
        Grid.SetRow(ExcelLocalSettings, 0);
        Grid.SetColumn(ExcelLocalSettings, 0);
        Grid.SetRow(ExcelInfo, 2);
        Grid.SetColumn(ExcelInfo, 0);
        ApplyCompactPanelSelection();
    }

    private void ApplyCompactPanelSelection()
    {
        if (!_isCompact)
        {
            return;
        }

        ExportFieldsPanel.Visibility = _showExcelExport
            ? Visibility.Collapsed
            : Visibility.Visible;
        ExcelExportPanel.Visibility = _showExcelExport
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyTabAppearance(ExportFieldsTabButton, isActive: !_showExcelExport);
        ApplyTabAppearance(ExcelExportTabButton, isActive: _showExcelExport);
    }

    private void ApplyTabAppearance(Button button, bool isActive)
    {
        button.Background = (Brush)FindResource(
            isActive ? "CiaAccentSoftBrush" : "CiaPanelBrush");
        button.Foreground = (Brush)FindResource(
            isActive ? "CiaAccentBrush" : "CiaTextSecondaryBrush");
    }

    private void OnDatabasePaneSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        if (_isCompact)
        {
            return;
        }

        UpdateDatabasePaneSizeFeedback();
        DatabasePaneSizeFeedback.Visibility = Visibility.Visible;
    }

    private void OnDatabasePaneSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isCompact)
        {
            UpdateDatabasePaneSizeFeedback();
        }
    }

    private void OnDatabasePaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_isCompact)
        {
            return;
        }

        UpdateDatabasePaneSizeFeedback();
        DatabasePaneSizeFeedback.Visibility = Visibility.Collapsed;
        var totalWidth = ExportFieldsColumn.ActualWidth + ExcelExportColumn.ActualWidth;
        if (SettingsService is null || totalWidth <= 0)
        {
            return;
        }

        var ratio = ResolvePaneSplitRatio(
            ExportFieldsColumn.ActualWidth / totalWidth,
            DefaultPaneSplitRatio);
        SettingsService.Save(
            SettingsService.Current with { DatabasePaneSplitRatio = ratio });
    }

    private void UpdateDatabasePaneSizeFeedback()
    {
        DatabasePaneSizeFeedbackText.Text =
            $"{ExportFieldsColumn.ActualWidth:0} / {ExcelExportColumn.ActualWidth:0}";
    }

    private static double ResolvePaneSplitRatio(double? ratio, double fallback) =>
        ratio is >= 0.2 and <= 0.8 && double.IsFinite(ratio.Value)
            ? ratio.Value
            : fallback;
}
