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
        StringAssert.Contains(load, "Foreground=\"{StaticResource CiaAccentBrush}\"");
        StringAssert.Contains(load, "ToolTip=\"Include supported XML files found in the selected folder.\"");
        StringAssert.Contains(load, "ToolTip=\"Include supported archives found inside the selected folder. Add Archive is unaffected.\"");
        StringAssert.Contains(load, "ToolTip=\"Traverse subfolders recursively when a folder is added.\"");
        StringAssert.Contains(load, "ToolTip=\"Navigate after completion. Discovery is not started automatically.\"");
    }

    [TestMethod]
    public void MajorPanesAreResizableWithoutChangingResponsiveOrRowSelectionModels()
    {
        var load = ReadDesktopFile("Views", "LoadWorkspaceView.xaml");
        var discovery = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml");
        var database = ReadDesktopFile("Views", "DatabaseWorkspaceView.xaml");
        var loadCode = ReadDesktopFile("Views", "LoadWorkspaceView.xaml.cs");
        var discoveryCode = ReadDesktopFile("Views", "DiscoveryWorkspaceView.xaml.cs");
        var databaseCode = ReadDesktopFile("Views", "DatabaseWorkspaceView.xaml.cs");

        StringAssert.Contains(load, "x:Name=\"LoadPaneSplitter\"");
        StringAssert.Contains(discovery, "x:Name=\"DiscoveryPaneSplitter\"");
        StringAssert.Contains(database, "x:Name=\"DatabasePaneSplitter\"");
        StringAssert.Contains(load, "SelectionMode=\"Extended\"");

        StringAssert.Contains(loadCode, "LoadLeftColumn.MinWidth = 600");
        StringAssert.Contains(loadCode, "LoadRightColumn.MinWidth = 420");
        StringAssert.Contains(discoveryCode, "DiscoveryLeftColumn.MinWidth = 580");
        StringAssert.Contains(discoveryCode, "DiscoveryRightColumn.MinWidth = 360");
        StringAssert.Contains(databaseCode, "ExportFieldsColumn.MinWidth = 360");
        StringAssert.Contains(databaseCode, "ExcelExportColumn.MinWidth = 360");

        StringAssert.Contains(loadCode, "LoadPaneSplitter.Visibility = Visibility.Collapsed");
        StringAssert.Contains(discoveryCode, "DiscoveryPaneSplitter.Visibility = Visibility.Collapsed");
        StringAssert.Contains(databaseCode, "DatabasePaneSplitter.Visibility = Visibility.Collapsed");
        StringAssert.Contains(database, "x:Name=\"DatabasePaneSizeFeedback\"");
        StringAssert.Contains(databaseCode, "DatabasePaneSizeFeedback.Visibility = Visibility.Visible");
        StringAssert.Contains(databaseCode, "DatabasePaneSizeFeedback.Visibility = Visibility.Collapsed");

        // The existing numeric width is per reviewed data column, not a major-pane width.
        StringAssert.Contains(database, "Text=\"{Binding Width, UpdateSourceTrigger=LostFocus}\"");
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
