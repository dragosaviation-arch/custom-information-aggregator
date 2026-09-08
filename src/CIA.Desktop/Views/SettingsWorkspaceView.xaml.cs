using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CIA.Desktop.Views;

public partial class SettingsWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private bool _isCompact;
    private bool _showSettings;
    private bool? _appliedCompactLayout;

    public SettingsWorkspaceView()
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

    private void OnLogViewClick(object sender, RoutedEventArgs e)
    {
        _showSettings = false;
        ApplyCompactSelection();
    }

    private void OnSettingsViewClick(object sender, RoutedEventArgs e)
    {
        _showSettings = true;
        ApplyCompactSelection();
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var hostWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        _isCompact = hostWidth < CompactLayoutBreakpoint;
        if (_appliedCompactLayout == _isCompact)
        {
            return;
        }

        _appliedCompactLayout = _isCompact;
        if (_isCompact)
        {
            ApplyCompactLayout();
        }
        else
        {
            ApplyWideLayout();
        }
    }

    private void ApplyWideLayout()
    {
        InternalSwitch.Visibility = Visibility.Collapsed;
        InternalSwitchRow.Height = new GridLength(0);
        InternalSwitchGapRow.Height = new GridLength(0);
        LogColumn.Width = new GridLength(1, GridUnitType.Star);
        SettingsGapColumn.Width = new GridLength(8);
        SettingsColumn.Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(LogDiagnosticsSide, 0);
        Grid.SetColumn(SettingsSide, 2);
        LogDiagnosticsSide.Visibility = Visibility.Visible;
        SettingsSide.Visibility = Visibility.Visible;
    }

    private void ApplyCompactLayout()
    {
        InternalSwitch.Visibility = Visibility.Visible;
        InternalSwitchRow.Height = new GridLength(34);
        InternalSwitchGapRow.Height = new GridLength(8);
        LogColumn.Width = new GridLength(1, GridUnitType.Star);
        SettingsGapColumn.Width = new GridLength(0);
        SettingsColumn.Width = new GridLength(0);
        Grid.SetColumn(LogDiagnosticsSide, 0);
        Grid.SetColumn(SettingsSide, 0);
        ApplyCompactSelection();
    }

    private void ApplyCompactSelection()
    {
        if (!_isCompact)
        {
            return;
        }

        LogDiagnosticsSide.Visibility = _showSettings
            ? Visibility.Collapsed
            : Visibility.Visible;
        SettingsSide.Visibility = _showSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySwitchAppearance(LogViewButton, isActive: !_showSettings);
        ApplySwitchAppearance(SettingsViewButton, isActive: _showSettings);
    }

    private void ApplySwitchAppearance(Button button, bool isActive)
    {
        button.Background = (Brush)FindResource(
            isActive ? "CiaAccentSoftBrush" : "CiaPanelBrush");
        button.Foreground = (Brush)FindResource(
            isActive ? "CiaAccentBrush" : "CiaTextSecondaryBrush");
    }
}
