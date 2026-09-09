using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CIA.Contracts.Sources;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;

namespace CIA.Desktop.Views;

public partial class LoadWorkspaceView : UserControl
{
    private const double CompactLayoutBreakpoint = 1180;
    private const double SourceRowHeight = 33;
    private const double DropOverlaySafeSpace = 20;
    private bool? _dropOverlayVisible;

    public LoadWorkspaceView()
    {
        InitializeComponent();
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
            return;
        }

        ApplyWideLayout();
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

        DetailsRow.Height = new GridLength(56, GridUnitType.Star);
        RightRailGapRow.Height = new GridLength(8);
        SettingsRow.Height = new GridLength(44, GridUnitType.Star);

        Grid.SetRow(DetailsPanel, 0);
        Grid.SetColumn(DetailsPanel, 0);
        Grid.SetRow(SettingsPanel, 2);
        Grid.SetColumn(SettingsPanel, 0);
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
        }
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
