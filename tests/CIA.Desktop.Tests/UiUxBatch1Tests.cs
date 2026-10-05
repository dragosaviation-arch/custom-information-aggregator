using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class UiUxBatch1Tests
{
    [TestMethod]
    public void DiscoveryActionUsesTheExistingCommandFromLoadOnly()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");
        var discovery = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml");
        var allViews = Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src", "CIA.Desktop", "Views"),
                "*.xaml",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToArray();

        StringAssert.Contains(
            load,
            "Command=\"{Binding DiscoveryWorkspace.RunDiscoveryCommand, ElementName=LoadViewRoot}\"");
        StringAssert.Contains(
            load,
            "Content=\"{Binding DiscoveryWorkspace.RunButtonText, ElementName=LoadViewRoot}\"");
        Assert.IsFalse(discovery.Contains(
            "Command=\"{Binding RunDiscoveryCommand}\"",
            StringComparison.Ordinal));
        Assert.AreEqual(
            1,
            allViews.Sum(view => Regex.Matches(view, "RunDiscoveryCommand").Count),
            "The principal Discovery command must have exactly one view binding.");
        StringAssert.Contains(discovery, "Text=\"{Binding DiscoveryStateText}\"");
        StringAssert.Contains(discovery, "Command=\"{Binding BuildDatabaseCommand}\"");
    }

    [TestMethod]
    public void WorkflowForwardButtonsShareOnlyTheirPresentationTreatment()
    {
        var loadButton = FindNamedElement(
            ReadDesktopFile("Views", "LoadWorkspaceView.xaml"),
            "RunDiscoveryButton");
        var databaseButton = FindNamedElement(
            ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml"),
            "BuildDatabaseButton");

        Assert.AreEqual(loadButton.Attribute("Style")?.Value, databaseButton.Attribute("Style")?.Value);
        Assert.AreEqual(loadButton.Attribute("MinHeight")?.Value, databaseButton.Attribute("MinHeight")?.Value);
        Assert.AreEqual(loadButton.Attribute("Padding")?.Value, databaseButton.Attribute("Padding")?.Value);
        Assert.AreEqual(loadButton.Attribute("FontWeight")?.Value, databaseButton.Attribute("FontWeight")?.Value);
    }

    [TestMethod]
    public void LoadProgressAndSettingsRemainCompactAndTruthful()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");

        StringAssert.Contains(load, "Maximum=\"{Binding ProgressMaximum, Mode=OneWay}\"");
        StringAssert.Contains(load, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");
        StringAssert.Contains(load, "IsIndeterminate=\"{Binding IsProgressIndeterminate, Mode=OneWay}\"");
        StringAssert.Contains(load, "Style=\"{StaticResource CiaDeterminateProgressBarStyle}\"");
        StringAssert.Contains(load, "ToolTip=\"Load supported XML files found in the selected folder.\"");
        StringAssert.Contains(load, "ToolTip=\"Load supported archives found inside the selected folder.\"");
        StringAssert.Contains(load, "ToolTip=\"Traverse subfolders recursively while loading a folder.\"");
        StringAssert.Contains(load, "Content=\"Open Discovery after run\"");
        StringAssert.Contains(load, "ToolTip=\"Open the Discovery tab only after Discovery generation/update completes successfully.\"");
        StringAssert.Contains(load, "Content=\"Skip remove warning\"");
        StringAssert.Contains(load, "ToolTip=\"Remove checked entries from the current session without showing the confirmation dialog.\"");
        Assert.IsFalse(load.Contains("Skip the confirmation modal; files are never deleted.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApprovedConvergenceStructureIsPresentWithoutDuplicateControls()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");
        var loadCode = ReadDesktopFile("Views", "LoadWorkspaceView.xaml.cs");
        var loadViewModel = ReadDesktopFile("Presentation", "LoadWorkspaceViewModel.cs");
        var discovery = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml");
        var database = ReadDesktopFile("Views", "DatabaseWorkspaceView.xaml");
        var databaseViewModel = ReadDesktopFile("Presentation", "DatabaseWorkspaceViewModel.cs");
        var discoveryDocument = XDocument.Parse(discovery);
        var presentationNamespace = discoveryDocument.Root!.Name.Namespace;

        StringAssert.Contains(load, "x:Name=\"SourceSetSettingsSection\"");
        StringAssert.Contains(load, "x:Name=\"LoadSettingsLowerGrid\" Grid.Row=\"2\"");
        StringAssert.Contains(load, "x:Name=\"WorkflowSettingsSection\"");
        StringAssert.Contains(load, "x:Name=\"FolderSettingsSection\" Grid.Column=\"2\"");
        StringAssert.Contains(loadCode, "LoadSettingsStackBreakpoint");
        StringAssert.Contains(loadCode, "Grid.SetRow(FolderSettingsSection, 2)");
        StringAssert.Contains(load, "x:Name=\"ReassignSetMenuButton\"");
        Assert.IsFalse(load.Contains("Content=\"Refresh selected\"", StringComparison.Ordinal));
        Assert.IsFalse(load.Contains("Content=\"Refresh\"", StringComparison.Ordinal));
        Assert.IsFalse(loadViewModel.Contains("RefreshSelectedCommand", StringComparison.Ordinal));
        StringAssert.Contains(load, "Content=\"Relink...\"");
        StringAssert.Contains(loadCode, "DetailsRow.Height = new GridLength(34");
        StringAssert.Contains(loadCode, "SettingsRow.Height = new GridLength(66");

        var layoutTab = discoveryDocument.Descendants(presentationNamespace + "TabItem")
            .Single(tab => string.Equals(tab.Attribute("Header")?.Value, "Layout", StringComparison.Ordinal));
        var generalTab = discoveryDocument.Descendants(presentationNamespace + "TabItem")
            .Single(tab => string.Equals(tab.Attribute("Header")?.Value, "General", StringComparison.Ordinal));
        Assert.IsTrue(layoutTab.Descendants().Any(element =>
            string.Equals(element.Attribute("Text")?.Value, "REPEATED DATA LAYOUT", StringComparison.Ordinal)));
        Assert.IsFalse(generalTab.Descendants().Any(element =>
            string.Equals(element.Attribute("Text")?.Value, "REPEATED DATA LAYOUT", StringComparison.Ordinal)));
        StringAssert.Contains(discovery, "Command=\"{Binding DataContext.ToggleBlacklistCommand");
        StringAssert.Contains(discovery, "Content=\"{Binding BlacklistActionText}\"");
        StringAssert.Contains(discovery, "x:Name=\"PreviewRow\" Height=\"11*\"");
        StringAssert.Contains(discovery, "Text=\"{Binding OccurrencePreviewText}\"");
        StringAssert.Contains(discovery, "Command=\"{Binding PreviousOccurrenceCommand}\"");
        StringAssert.Contains(discovery, "Command=\"{Binding NextOccurrenceCommand}\"");
        StringAssert.Contains(discovery, "AutomationProperties.Name=\"Database Tag Name Override\"");

        StringAssert.Contains(database, "x:Name=\"DatabaseSourceSetTabs\"");
        StringAssert.Contains(database, "SelectedItem=\"{Binding SelectedDataset, Mode=TwoWay}\"");
        StringAssert.Contains(database, "x:Name=\"DatabaseReviewRows\"");
        StringAssert.Contains(database, "VirtualizingPanel.IsVirtualizing=\"True\"");
        StringAssert.Contains(database, "VirtualizingPanel.VirtualizationMode=\"Recycling\"");
        StringAssert.Contains(database, "HorizontalScrollBarVisibility=\"Auto\"");
        Assert.IsFalse(database.Contains("PreviousReviewPageCommand", StringComparison.Ordinal));
        Assert.IsFalse(database.Contains("NextReviewPageCommand", StringComparison.Ordinal));
        Assert.IsFalse(database.Contains("ReviewPageText", StringComparison.Ordinal));
        StringAssert.Contains(database, "ItemsSource=\"{Binding ColumnChoices}\"");
        StringAssert.Contains(database, "Text=\"{Binding Category}\"");
        Assert.IsFalse(database.Contains("OPTIONAL METADATA COLUMNS", StringComparison.Ordinal));
        Assert.IsFalse(database.Contains("AutomationProperties.Name=\"Export Database column\"", StringComparison.Ordinal));
        StringAssert.Contains(databaseViewModel, "DatabaseMetadataVisibilityMode.None");
        StringAssert.Contains(databaseViewModel, "VirtualizedDatabaseReviewCollection");
    }

    [TestMethod]
    public void ActualDataColumnsAreResizableWithoutMajorPaneSplittersOrNumericWidthEditor()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");
        var discovery = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml");
        var database = ReadDesktopFile("Views", "DatabaseWorkspaceView.xaml");
        var settings = ReadDesktopFile("Views", "SettingsWorkspaceView.xaml");
        var theme = ReadDesktopFile("Themes", "CiaTheme.xaml");
        var loadCode = ReadDesktopFile("Views", "LoadWorkspaceView.xaml.cs");
        var discoveryCode = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml.cs");
        var databaseCode = ReadDesktopFile("Views", "DatabaseWorkspaceView.xaml.cs");
        var settingsCode = ReadDesktopFile("Views", "SettingsWorkspaceView.xaml.cs");
        var loadViewModel = ReadDesktopFile("Presentation", "LoadWorkspaceViewModel.cs");
        var discoveryViewModel = ReadDesktopFile("Presentation", "DiscoveryWorkspaceViewModel.cs");
        var databaseViewModel = ReadDesktopFile("Presentation", "DatabaseWorkspaceViewModel.cs");

        Assert.IsFalse(load.Contains("LoadPaneSplitter", StringComparison.Ordinal));
        Assert.IsFalse(discovery.Contains("DiscoveryPaneSplitter", StringComparison.Ordinal));
        Assert.IsFalse(database.Contains("DatabasePaneSplitter", StringComparison.Ordinal));
        StringAssert.Contains(load, "SelectionMode=\"Extended\"");

        StringAssert.Contains(loadCode, "LoadLeftColumn.MinWidth = 600");
        StringAssert.Contains(loadCode, "LoadRightColumn.MinWidth = 420");
        StringAssert.Contains(discoveryCode, "DiscoveryLeftColumn.MinWidth = 580");
        StringAssert.Contains(discoveryCode, "DiscoveryRightColumn.MinWidth = 360");
        StringAssert.Contains(load, "LoadColumnResizeThumbStyle");
        StringAssert.Contains(discovery, "DiscoveryColumnResizeThumbStyle");
        Assert.HasCount(5, Regex.Matches(load, "Tag=\"load\\.").Cast<Match>());
        Assert.HasCount(5, Regex.Matches(discovery, "Tag=\"discovery\\.").Cast<Match>());
        StringAssert.Contains(theme, "x:Key=\"CiaColumnResizeThumbStyle\"");
        StringAssert.Contains(theme, "x:Name=\"ResizeGuide\"");
        StringAssert.Contains(theme, "Opacity=\"0\"");
        StringAssert.Contains(theme, "Property=\"IsMouseOver\" Value=\"True\"");
        StringAssert.Contains(theme, "Property=\"IsDragging\" Value=\"True\"");
        StringAssert.Contains(database, "OnDatabaseColumnResizeDelta");
        StringAssert.Contains(database, "x:Name=\"DatabaseMetadataHeaders\"");
        StringAssert.Contains(database, "Width=\"{Binding Column.Width}\"");
        StringAssert.Contains(database, "Style=\"{StaticResource DatabaseColumnResizeThumbStyle}\"");
        StringAssert.Contains(database, "x:Name=\"DatabaseColumnSizeFeedback\"");
        StringAssert.Contains(databaseCode, "DatabaseColumnSizeFeedback.Visibility = Visibility.Visible");
        StringAssert.Contains(databaseCode, "DatabaseColumnSizeFeedback.Visibility = Visibility.Collapsed");
        StringAssert.Contains(settings, "<GridViewColumn Width=\"110\"");
        StringAssert.Contains(settings, "OnLogEntriesPreviewMouseLeftButtonUp");
        StringAssert.Contains(settingsCode, "PersistLogColumnWidths");
        Assert.IsFalse(database.Contains("Text=\"{Binding Width, UpdateSourceTrigger=LostFocus}\"", StringComparison.Ordinal));
        Assert.IsFalse(load.Contains("<Thumb Width=", StringComparison.Ordinal));
        Assert.IsFalse(discovery.Contains("<Thumb Width=", StringComparison.Ordinal));
        Assert.IsFalse(database.Contains("<Thumb Width=", StringComparison.Ordinal));
        StringAssert.Contains(loadViewModel, "MaximumColumnWidth = 2000");
        StringAssert.Contains(discoveryViewModel, "MaximumColumnWidth = 2000");
        StringAssert.Contains(databaseViewModel, "MaximumWidth = 2000");
        Assert.IsFalse(loadCode.Contains("PaneSplitRatio", StringComparison.Ordinal));
        Assert.IsFalse(discoveryCode.Contains("PaneSplitRatio", StringComparison.Ordinal));
        Assert.IsFalse(databaseCode.Contains("PaneSplitRatio", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProgressBarsAreDeterminateBlueAndDiscoveryDatabasePreferenceIsEnabled()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");
        var discovery = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml");
        var theme = ReadDesktopFile("Themes", "CiaTheme.xaml");

        StringAssert.Contains(theme, "x:Key=\"CiaDeterminateProgressBarStyle\"");
        StringAssert.Contains(theme, "Foreground\" Value=\"{StaticResource CiaAccentBrush}\"");
        Assert.IsFalse(theme.Contains("CiaSuccessBrush", StringComparison.Ordinal)
            && theme.Contains("CiaDeterminateProgressBarStyle", StringComparison.Ordinal)
            && theme.IndexOf("CiaSuccessBrush", StringComparison.Ordinal)
                > theme.IndexOf("CiaDeterminateProgressBarStyle", StringComparison.Ordinal));
        StringAssert.Contains(load, "IsIndeterminate=\"{Binding IsProgressIndeterminate, Mode=OneWay}\"");
        StringAssert.Contains(discovery, "IsIndeterminate=\"{Binding IsProgressIndeterminate}\"");
        StringAssert.Contains(discovery, "Maximum=\"{Binding ProgressMaximum}\"");
        StringAssert.Contains(discovery, "Value=\"{Binding ProgressValue, Mode=OneWay}\"");
        Assert.IsFalse(discovery.Contains("IsIndeterminate=\"{Binding IsBusy}\"", StringComparison.Ordinal));
        StringAssert.Contains(discovery, "IsChecked=\"{Binding OpenDatabaseWhenCreationCompletes}\"");
        Assert.IsNotNull(FindNamedElement(discovery, "BuildDatabaseButton"));
    }

    [TestMethod]
    public void NativePickersExposeMultiSelectionWithoutChangingIngestionValidation()
    {
        var picker = ReadDesktopFile("Sources", "WindowsSourcePathPicker.cs");

        StringAssert.Contains(picker, "Multiselect = multiselect");
        StringAssert.Contains(picker, "dialog.FileNames");
        StringAssert.Contains(picker, "dialog.FolderNames");
        StringAssert.Contains(picker, "Filter = \"XML files (*.xml)|*.xml\"");
        StringAssert.Contains(picker, "Filter = \"Archive files|*.*\"");
    }

    private static XElement FindNamedElement(string xaml, string name)
    {
        var document = XDocument.Parse(xaml);
        var nameAttribute = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        return document
            .Descendants()
            .Single(element => string.Equals(
                element.Attribute(nameAttribute)?.Value,
                name,
                StringComparison.Ordinal));
    }

    private static string ReadDesktopFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(
            [FindRepositoryRoot(), "src", "CIA.Desktop", .. segments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CIA.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root containing CIA.slnx was not found.");
    }
}
