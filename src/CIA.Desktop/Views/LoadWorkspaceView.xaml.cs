using System.Windows;
using System.Windows.Controls;

namespace CIA.Desktop.Views;

public partial class LoadWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1120;

    public LoadWorkspaceView()
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

    private void OnWorkspaceViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        LoadLayout.MinHeight = Math.Max(0, WorkspaceScroller.ViewportHeight - 32);

        double hostWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        if (hostWidth < CompactLayoutBreakpoint)
        {
            ApplyCompactLayout();
            return;
        }

        ApplyWideLayout();
    }

    private void ApplyCompactLayout()
    {
        LoadLeftColumn.MinWidth = 0;
        LoadLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        LoadGapColumn.Width = new GridLength(0);
        LoadRightColumn.Width = new GridLength(0);

        LoadTopRow.Height = GridLength.Auto;
        LoadStackGapRow.Height = new GridLength(12);
        LoadBottomRow.Height = GridLength.Auto;

        Grid.SetRow(LoadLeftPane, 0);
        Grid.SetColumn(LoadLeftPane, 0);
        Grid.SetRow(LoadRightRail, 2);
        Grid.SetColumn(LoadRightRail, 0);

        DetailsColumn.Width = new GridLength(1, GridUnitType.Star);
        RightRailGapColumn.Width = new GridLength(12);
        SettingsColumn.Width = new GridLength(1, GridUnitType.Star);

        DetailsRow.Height = GridLength.Auto;
        DetailsRow.MinHeight = 0;
        RightRailGapRow.Height = new GridLength(0);
        SettingsRow.Height = new GridLength(0);
        SettingsRow.MinHeight = 0;

        Grid.SetRow(DetailsPanel, 0);
        Grid.SetColumn(DetailsPanel, 0);
        Grid.SetRow(SettingsPanel, 0);
        Grid.SetColumn(SettingsPanel, 2);
    }

    private void ApplyWideLayout()
    {
        LoadLeftColumn.MinWidth = 620;
        LoadLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        LoadGapColumn.Width = new GridLength(12);
        LoadRightColumn.Width = new GridLength(360);

        LoadTopRow.Height = new GridLength(1, GridUnitType.Star);
        LoadStackGapRow.Height = new GridLength(0);
        LoadBottomRow.Height = new GridLength(0);

        Grid.SetRow(LoadLeftPane, 0);
        Grid.SetColumn(LoadLeftPane, 0);
        Grid.SetRow(LoadRightRail, 0);
        Grid.SetColumn(LoadRightRail, 2);

        DetailsColumn.Width = new GridLength(1, GridUnitType.Star);
        RightRailGapColumn.Width = new GridLength(0);
        SettingsColumn.Width = new GridLength(0);

        DetailsRow.Height = new GridLength(57, GridUnitType.Star);
        DetailsRow.MinHeight = 300;
        RightRailGapRow.Height = new GridLength(12);
        SettingsRow.Height = new GridLength(43, GridUnitType.Star);
        SettingsRow.MinHeight = 220;

        Grid.SetRow(DetailsPanel, 0);
        Grid.SetColumn(DetailsPanel, 0);
        Grid.SetRow(SettingsPanel, 2);
        Grid.SetColumn(SettingsPanel, 0);
    }
}
