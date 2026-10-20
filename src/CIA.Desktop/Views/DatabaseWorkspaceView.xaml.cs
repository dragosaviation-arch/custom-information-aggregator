using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CIA.Desktop.Presentation;

namespace CIA.Desktop.Views;

public partial class DatabaseWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private const double DatabaseReviewRowHeight = 30;
    private bool _isCompact;
    private bool _showExcelExport;
    private bool _synchronizingReviewScroll;
    private ScrollViewer? _databaseReviewScrollViewer;
    private DatabaseColumnWidthFeedback? _columnResize;
    private readonly DispatcherTimer _reviewToolTipRestoreTimer;

    public DatabaseWorkspaceView()
    {
        InitializeComponent();
        _reviewToolTipRestoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _reviewToolTipRestoreTimer.Tick += OnReviewToolTipRestoreTimerTick;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _reviewToolTipRestoreTimer.Stop();
        ToolTipService.SetIsEnabled(DatabaseReviewRows, true);
    }

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        ColumnsPopup.IsOpen = !ColumnsPopup.IsOpen;
    }

    private void OnColumnNameEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DatabaseColumnChoicePresentation choice })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            choice.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            choice.CancelRename();
            e.Handled = true;
        }
    }

    private void OnColumnNameEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: DatabaseColumnChoicePresentation choice })
        {
            choice.CommitRename();
        }
    }

    private void OnWorkbookNameEditKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not DatabaseWorkspaceViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            viewModel.CommitWorkbookRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.CancelWorkbookRename();
            e.Handled = true;
        }
    }

    private void OnWorkbookNameEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is DatabaseWorkspaceViewModel viewModel)
        {
            viewModel.CommitWorkbookRename();
        }
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
        ExportFieldsColumn.Width = new GridLength(1, GridUnitType.Star);
        LowerPanelGapColumn.Width = new GridLength(8);
        ExcelExportColumn.Width = new GridLength(1, GridUnitType.Star);
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
        ExportFieldsColumn.Width = new GridLength(1, GridUnitType.Star);
        LowerPanelGapColumn.Width = new GridLength(0);
        ExcelExportColumn.Width = new GridLength(0);
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

    private void OnDatabaseColumnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { DataContext: { } column }
            || DataContext is not DatabaseWorkspaceViewModel viewModel)
        {
            return;
        }

        _columnResize = viewModel.GetAdjacentDatabaseColumnWidths(column);
        ShowColumnSizeFeedback(_columnResize);
    }

    private void OnDatabaseColumnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { DataContext: { } column }
            || DataContext is not DatabaseWorkspaceViewModel viewModel)
        {
            return;
        }

        _columnResize = viewModel.ResizeAdjacentDatabaseColumns(column, e.HorizontalChange);
        ShowColumnSizeFeedback(_columnResize);
    }

    private void OnDatabaseColumnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_columnResize is { } resize
            && DataContext is DatabaseWorkspaceViewModel viewModel)
        {
            viewModel.PersistDatabaseColumnWidths(resize);
        }

        _columnResize = null;
        DatabaseColumnSizeFeedback.Visibility = Visibility.Collapsed;
    }

    private async void OnDatabaseReviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = sender as ScrollViewer ?? e.OriginalSource as ScrollViewer;
        if (scrollViewer is not null)
        {
            _databaseReviewScrollViewer = scrollViewer;
            SynchronizeReviewVerticalScrollBar(scrollViewer);
            if (Math.Abs(e.VerticalChange) > double.Epsilon)
            {
                SuppressReviewToolTipsWhileScrolling();
            }
        }

        if (DataContext is not DatabaseWorkspaceViewModel viewModel
            || e.ViewportHeight <= 0)
        {
            return;
        }

        var firstVisibleOrdinal = (int)Math.Floor(e.VerticalOffset / DatabaseReviewRowHeight) + 1;
        var visibleRowCount = (int)Math.Ceiling(e.ViewportHeight / DatabaseReviewRowHeight) + 2;
        await viewModel.EnsureReviewRowsAvailableAsync(firstVisibleOrdinal, visibleRowCount);
    }

    private void OnDatabaseReviewHorizontalScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) > double.Epsilon)
        {
            SuppressReviewToolTipsWhileScrolling();
        }
    }

    private void SynchronizeReviewVerticalScrollBar(ScrollViewer scrollViewer)
    {
        _synchronizingReviewScroll = true;
        try
        {
            SetIfChanged(
                DatabaseReviewVerticalScrollBar.Maximum,
                scrollViewer.ScrollableHeight,
                value => DatabaseReviewVerticalScrollBar.Maximum = value);
            SetIfChanged(
                DatabaseReviewVerticalScrollBar.ViewportSize,
                scrollViewer.ViewportHeight,
                value => DatabaseReviewVerticalScrollBar.ViewportSize = value);
            SetIfChanged(
                DatabaseReviewVerticalScrollBar.LargeChange,
                Math.Max(DatabaseReviewRowHeight, scrollViewer.ViewportHeight),
                value => DatabaseReviewVerticalScrollBar.LargeChange = value);
            SetIfChanged(
                DatabaseReviewVerticalScrollBar.Value,
                scrollViewer.VerticalOffset,
                value => DatabaseReviewVerticalScrollBar.Value = value);
            var isEnabled = scrollViewer.ScrollableHeight > 0;
            if (DatabaseReviewVerticalScrollBar.IsEnabled != isEnabled)
            {
                DatabaseReviewVerticalScrollBar.IsEnabled = isEnabled;
            }
        }
        finally
        {
            _synchronizingReviewScroll = false;
        }
    }

    private static void SetIfChanged(double current, double next, Action<double> setter)
    {
        if (Math.Abs(current - next) > 0.01)
        {
            setter(next);
        }
    }

    private void SuppressReviewToolTipsWhileScrolling()
    {
        ToolTipService.SetIsEnabled(DatabaseReviewRows, false);
        _reviewToolTipRestoreTimer.Stop();
        _reviewToolTipRestoreTimer.Start();
    }

    private void OnReviewToolTipRestoreTimerTick(object? sender, EventArgs e)
    {
        _reviewToolTipRestoreTimer.Stop();
        if (IsLoaded)
        {
            ToolTipService.SetIsEnabled(DatabaseReviewRows, true);
        }
    }

    private void OnDatabaseReviewVerticalScrollBarValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_synchronizingReviewScroll)
        {
            _databaseReviewScrollViewer?.ScrollToVerticalOffset(e.NewValue);
        }
    }

    private void ShowColumnSizeFeedback(DatabaseColumnWidthFeedback? feedback)
    {
        if (feedback is null)
        {
            DatabaseColumnSizeFeedback.Visibility = Visibility.Collapsed;
            return;
        }

        DatabaseColumnSizeFeedbackText.Text = $"{feedback.LeftWidth:0} / {feedback.RightWidth:0}";
        DatabaseColumnSizeFeedback.Visibility = Visibility.Visible;
    }

}
