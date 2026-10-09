using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CIA.Contracts.Sources;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;

namespace CIA.Desktop.Views;

public partial class LoadWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private const double LoadSettingsStackBreakpoint = 390;
    private const double SourceRowHeight = 33;
    private const double DropOverlaySafeSpace = 20;
    private bool? _dropOverlayVisible;

    public LoadWorkspaceView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty DiscoveryWorkspaceProperty =
        DependencyProperty.Register(
            nameof(DiscoveryWorkspace),
            typeof(DiscoveryWorkspaceViewModel),
            typeof(LoadWorkspaceView));

    public DiscoveryWorkspaceViewModel? DiscoveryWorkspace
    {
        get => (DiscoveryWorkspaceViewModel?)GetValue(DiscoveryWorkspaceProperty);
        set => SetValue(DiscoveryWorkspaceProperty, value);
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        UpdateDropOverlayVisibility();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
        UpdateDropOverlayVisibility();
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
            ApplyLoadSettingsLayout();
            return;
        }

        ApplyWideLayout();
        ApplyLoadSettingsLayout();
    }

    private void ApplyCompactLayout()
    {
        LoadLeftColumn.MinWidth = 0;
        LoadLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        LoadGapColumn.Width = new GridLength(0);
        LoadRightColumn.MinWidth = 0;
        LoadRightColumn.Width = new GridLength(0);

        LoadTopRow.Height = new GridLength(3, GridUnitType.Star);
        LoadStackGapRow.Height = new GridLength(8);
        LoadBottomRow.Height = new GridLength(2, GridUnitType.Star);

        Grid.SetRow(LoadLeftPane, 0);
        Grid.SetColumn(LoadLeftPane, 0);
        Grid.SetRow(LoadRightRail, 2);
        Grid.SetColumn(LoadRightRail, 0);

        DetailsColumn.Width = new GridLength(1, GridUnitType.Star);
        RightRailGapColumn.Width = new GridLength(8);
        SettingsColumn.Width = new GridLength(1, GridUnitType.Star);

        DetailsRow.Height = new GridLength(1, GridUnitType.Star);
        RightRailGapRow.Height = new GridLength(0);
        SettingsRow.Height = new GridLength(0);

        Grid.SetRow(DetailsPanel, 0);
        Grid.SetColumn(DetailsPanel, 0);
        Grid.SetRow(SettingsPanel, 0);
        Grid.SetColumn(SettingsPanel, 2);
    }

    private void ApplyWideLayout()
    {
        LoadLeftColumn.MinWidth = 600;
        LoadLeftColumn.Width = new GridLength(2, GridUnitType.Star);
        LoadGapColumn.Width = new GridLength(8);
        LoadRightColumn.MinWidth = 420;
        LoadRightColumn.Width = new GridLength(1, GridUnitType.Star);

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

        DetailsRow.Height = new GridLength(38, GridUnitType.Star);
        RightRailGapRow.Height = new GridLength(8);
        SettingsRow.Height = new GridLength(62, GridUnitType.Star);

        Grid.SetRow(DetailsPanel, 0);
        Grid.SetColumn(DetailsPanel, 0);
        Grid.SetRow(SettingsPanel, 2);
        Grid.SetColumn(SettingsPanel, 0);
    }

    private void ApplyLoadSettingsLayout()
    {
        var availableWidth = SettingsPanel.ActualWidth;
        if (availableWidth > 0 && availableWidth < LoadSettingsStackBreakpoint)
        {
            WorkflowSettingsColumn.Width = new GridLength(1, GridUnitType.Star);
            LoadSettingsLowerGapColumn.Width = new GridLength(0);
            FolderSettingsColumn.Width = new GridLength(0);
            LoadSettingsLowerTopRow.Height = GridLength.Auto;
            LoadSettingsLowerStackGapRow.Height = new GridLength(8);
            LoadSettingsLowerBottomRow.Height = GridLength.Auto;
            Grid.SetRow(WorkflowSettingsSection, 0);
            Grid.SetColumn(WorkflowSettingsSection, 0);
            Grid.SetRow(FolderSettingsSection, 2);
            Grid.SetColumn(FolderSettingsSection, 0);
            return;
        }

        WorkflowSettingsColumn.Width = new GridLength(1, GridUnitType.Star);
        LoadSettingsLowerGapColumn.Width = new GridLength(12);
        FolderSettingsColumn.Width = new GridLength(1, GridUnitType.Star);
        LoadSettingsLowerTopRow.Height = GridLength.Auto;
        LoadSettingsLowerStackGapRow.Height = new GridLength(0);
        LoadSettingsLowerBottomRow.Height = new GridLength(0);
        Grid.SetRow(WorkflowSettingsSection, 0);
        Grid.SetColumn(WorkflowSettingsSection, 0);
        Grid.SetRow(FolderSettingsSection, 0);
        Grid.SetColumn(FolderSettingsSection, 2);
    }

    private void OnSourceRowsLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateDropOverlayVisibility();
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LoadWorkspaceViewModel viewModel)
        {
            viewModel.SetHighlightedSources(
                SourceRowsList.SelectedItems.Cast<LoadedSourceItem>());
            if (e.AddedItems.OfType<LoadedSourceItem>().LastOrDefault() is { } current)
            {
                viewModel.SelectedSource = current;
            }
        }
    }

    private void OnSourceRowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListBoxItem { DataContext: LoadedSourceItem source }
            && DataContext is LoadWorkspaceViewModel viewModel)
        {
            viewModel.SelectedSource = source;
        }
    }

    private void OnSourceInclusionPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || ItemsControl.ContainerFromElement(SourceRowsList, checkBox)
                is not ListBoxItem { DataContext: LoadedSourceItem source } row
            || DataContext is not LoadWorkspaceViewModel viewModel)
        {
            return;
        }

        if (!row.IsSelected)
        {
            SourceRowsList.SelectedItems.Clear();
            row.IsSelected = true;
        }

        viewModel.SelectedSource = source;
        viewModel.SetHighlightedSources(SourceRowsList.SelectedItems.Cast<LoadedSourceItem>());
    }

    private void OnLoadColumnDividerDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is LoadWorkspaceViewModel viewModel
            && sender is Thumb { Tag: string tag }
            && TryParseColumnPair(tag, out var leftKey, out var rightKey))
        {
            viewModel.ResizeColumns(leftKey, rightKey, e.HorizontalChange);
        }
    }

    private void OnLoadColumnDividerDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is LoadWorkspaceViewModel viewModel
            && sender is Thumb { Tag: string tag }
            && TryParseColumnPair(tag, out var leftKey, out var rightKey))
        {
            viewModel.PersistColumnWidths(leftKey, rightKey);
        }
    }

    private static bool TryParseColumnPair(
        string value,
        out string leftKey,
        out string rightKey)
    {
        var separator = value.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator >= value.Length - 1)
        {
            leftKey = string.Empty;
            rightKey = string.Empty;
            return false;
        }

        leftKey = value[..separator];
        rightKey = value[(separator + 1)..];
        return true;
    }

    private void OnAddFilesSetMenuClick(object sender, RoutedEventArgs e)
    {
        ShowAddSourceSetMenu((Button)sender, SourceSelectionKind.XmlFile);
    }

    private void OnAddFolderSetMenuClick(object sender, RoutedEventArgs e)
    {
        ShowAddSourceSetMenu((Button)sender, SourceSelectionKind.Folder);
    }

    private void OnAddArchiveSetMenuClick(object sender, RoutedEventArgs e)
    {
        ShowAddSourceSetMenu((Button)sender, SourceSelectionKind.Archive);
    }

    private void ShowAddSourceSetMenu(Button button, SourceSelectionKind selectionKind)
    {
        if (DataContext is not LoadWorkspaceViewModel viewModel)
        {
            return;
        }

        var menu = CreateSourceSetMenu(
            viewModel,
            async sourceSet => await viewModel.AddUsingSourceSetAsync(selectionKind, sourceSet),
            "Add to ",
            "Create new Set");
        OpenMenu(button, menu);
    }

    private void OnReassignSetMenuClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LoadWorkspaceViewModel viewModel)
        {
            return;
        }

        var menu = CreateSourceSetMenu(
            viewModel,
            sourceSet =>
            {
                viewModel.ReassignHighlightedSources(sourceSet);
                return Task.CompletedTask;
            },
            "Move to ",
            "Move to new Set");
        OpenMenu((Button)sender, menu);
    }

    private static ContextMenu CreateSourceSetMenu(
        LoadWorkspaceViewModel viewModel,
        Func<SourceSetDefinition?, Task> action,
        string existingSetPrefix,
        string createNewLabel)
    {
        var menu = new ContextMenu();
        foreach (var sourceSet in viewModel.SourceSets)
        {
            var item = new MenuItem { Header = existingSetPrefix + sourceSet.Name };
            item.Click += async (_, _) => await action(sourceSet);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var createNew = new MenuItem { Header = createNewLabel };
        createNew.Click += async (_, _) => await action(null);
        menu.Items.Add(createNew);
        return menu;
    }

    private static void OpenMenu(Button button, ContextMenu menu)
    {
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void UpdateDropOverlayVisibility()
    {
        if (!IsLoaded || SourceRowsList.ActualHeight <= 0)
        {
            return;
        }

        var occupiedHeight = SourceRowsList.Items.Count * SourceRowHeight;
        var requiredEmptyHeight = DropOverlay.ActualHeight + DropOverlaySafeSpace;
        var shouldShow = SourceRowsList.Items.Count == 0
            || occupiedHeight < SourceRowsList.ActualHeight - requiredEmptyHeight;

        if (_dropOverlayVisible == shouldShow)
        {
            return;
        }

        _dropOverlayVisible = shouldShow;
        DropOverlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                shouldShow ? 1 : 0,
                TimeSpan.FromMilliseconds(140)));
    }

    private void OnSourceListDragEnter(object sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnSourceListDragOver(object sender, DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void OnSourceListDragLeave(object sender, DragEventArgs e)
    {
        SetDropOverlayDragState(isDragging: false);
    }

    private async void OnSourceListDrop(object sender, DragEventArgs e)
    {
        SetDropOverlayDragState(isDragging: false);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths
            || DataContext is not LoadWorkspaceViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        await viewModel.AddDroppedPathsAsync(paths);
        UpdateDropOverlayVisibility();
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var hasPaths = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasPaths ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetDropOverlayDragState(hasPaths);
    }

    private void SetDropOverlayDragState(bool isDragging)
    {
        DropOverlay.Background = (Brush)FindResource(
            isDragging ? "CiaAccentSoftBrush" : "CiaDropOverlayBrush");
        DropOverlay.BorderBrush = (Brush)FindResource(
            isDragging ? "CiaAccentBrush" : "CiaBorderStrongBrush");
    }
}
