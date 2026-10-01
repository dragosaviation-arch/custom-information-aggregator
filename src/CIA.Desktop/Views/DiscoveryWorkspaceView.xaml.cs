using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CIA.Core.Runtime;
using CIA.Desktop.Presentation;

namespace CIA.Desktop.Views;

public partial class DiscoveryWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private const double DefaultPaneSplitRatio = 3d / 5d;

    public DiscoveryWorkspaceView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty SettingsServiceProperty =
        DependencyProperty.Register(
            nameof(SettingsService),
            typeof(ApplicationSettingsService),
            typeof(DiscoveryWorkspaceView));

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

    private void OnOccurrenceOrdinalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitOccurrenceOrdinal();
        e.Handled = true;
    }

    private void OnDiscoveryRowPreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListBoxItem item
            && !item.IsSelected)
        {
            item.IsSelected = true;
        }
    }

    private void OnOccurrenceOrdinalLostFocus(object sender, RoutedEventArgs e)
    {
        CommitOccurrenceOrdinal();
    }

    private void CommitOccurrenceOrdinal()
    {
        if (DataContext is DiscoveryWorkspaceViewModel viewModel
            && viewModel.JumpToOccurrenceCommand.CanExecute(null))
        {
            viewModel.JumpToOccurrenceCommand.Execute(null);
        }
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var hostWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        if (hostWidth < CompactLayoutBreakpoint)
        {
            ApplyCompactLayout();
            return;
        }

        ApplyWideLayout();
    }

    private void ApplyCompactLayout()
    {
        DiscoveryLeftColumn.MinWidth = 0;
        DiscoveryLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        DiscoveryGapColumn.Width = new GridLength(0);
        DiscoveryPaneSplitter.Visibility = Visibility.Collapsed;
        DiscoveryRightColumn.MinWidth = 0;
        DiscoveryRightColumn.Width = new GridLength(0);

        DiscoveryTopRow.Height = new GridLength(3, GridUnitType.Star);
        DiscoveryStackGapRow.Height = new GridLength(8);
        DiscoveryBottomRow.Height = new GridLength(2, GridUnitType.Star);

        Grid.SetRow(DiscoveryLeftPane, 0);
        Grid.SetColumn(DiscoveryLeftPane, 0);
        Grid.SetRow(DiscoveryRightRail, 2);
        Grid.SetColumn(DiscoveryRightRail, 0);

        PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
        RightRailGapColumn.Width = new GridLength(8);
        SettingsColumn.Width = new GridLength(2, GridUnitType.Star);
        PreviewRow.Height = new GridLength(1, GridUnitType.Star);
        RightRailGapRow.Height = new GridLength(0);
        SettingsRow.Height = new GridLength(0);

        Grid.SetRow(PreviewPanel, 0);
        Grid.SetColumn(PreviewPanel, 0);
        Grid.SetRow(SettingsPanel, 0);
        Grid.SetColumn(SettingsPanel, 2);
    }

    private void ApplyWideLayout()
    {
        DiscoveryLeftColumn.MinWidth = 580;
        var splitRatio = ResolvePaneSplitRatio(
            SettingsService?.Current.DiscoveryPaneSplitRatio,
            DefaultPaneSplitRatio);
        DiscoveryLeftColumn.Width = new GridLength(splitRatio, GridUnitType.Star);
        DiscoveryGapColumn.Width = new GridLength(8);
        DiscoveryRightColumn.MinWidth = 360;
        DiscoveryRightColumn.Width = new GridLength(1 - splitRatio, GridUnitType.Star);
        DiscoveryPaneSplitter.Visibility = Visibility.Visible;

        DiscoveryTopRow.Height = new GridLength(1, GridUnitType.Star);
        DiscoveryStackGapRow.Height = new GridLength(0);
        DiscoveryBottomRow.Height = new GridLength(0);

        Grid.SetRow(DiscoveryLeftPane, 0);
        Grid.SetColumn(DiscoveryLeftPane, 0);
        Grid.SetRow(DiscoveryRightRail, 0);
        Grid.SetColumn(DiscoveryRightRail, 2);

        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        RightRailGapColumn.Width = new GridLength(0);
        SettingsColumn.Width = new GridLength(0);
        PreviewRow.Height = new GridLength(3, GridUnitType.Star);
        RightRailGapRow.Height = new GridLength(8);
        SettingsRow.Height = new GridLength(2, GridUnitType.Star);

        Grid.SetRow(PreviewPanel, 0);
        Grid.SetColumn(PreviewPanel, 0);
        Grid.SetRow(SettingsPanel, 2);
        Grid.SetColumn(SettingsPanel, 0);
    }

    private void OnDiscoveryPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var totalWidth = DiscoveryLeftColumn.ActualWidth + DiscoveryRightColumn.ActualWidth;
        if (SettingsService is null || totalWidth <= 0)
        {
            return;
        }

        var ratio = ResolvePaneSplitRatio(
            DiscoveryLeftColumn.ActualWidth / totalWidth,
            DefaultPaneSplitRatio);
        SettingsService.Save(
            SettingsService.Current with { DiscoveryPaneSplitRatio = ratio });
    }

    private static double ResolvePaneSplitRatio(double? ratio, double fallback) =>
        ratio is >= 0.2 and <= 0.8 && double.IsFinite(ratio.Value)
            ? ratio.Value
            : fallback;
}
