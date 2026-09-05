using System.Windows;
using System.Windows.Controls;

namespace CIA.Desktop.Views;

public partial class DiscoveryWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;

    public DiscoveryWorkspaceView()
    {
        InitializeComponent();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
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
        DiscoveryLeftColumn.Width = new GridLength(3, GridUnitType.Star);
        DiscoveryGapColumn.Width = new GridLength(8);
        DiscoveryRightColumn.Width = new GridLength(2, GridUnitType.Star);

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
}
