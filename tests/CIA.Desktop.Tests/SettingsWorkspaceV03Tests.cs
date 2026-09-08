using System.Windows;
using System.Windows.Controls;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Presentation;
using CIA.Desktop.Views;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class SettingsWorkspaceV03Tests
{
    [TestMethod]
    public void UnifiedViewerUsesRealActivityAndIssueRecordsWithoutFabricatingRawLogs()
    {
        var viewModel = CreateViewModel();

        Assert.HasCount(2, viewModel.Entries);
        Assert.HasCount(2, viewModel.VisibleEntries);
        Assert.IsTrue(viewModel.Entries.Any(
            entry => entry.EntryType == SettingsLogEntryPresentation.ActivityType));
        Assert.IsTrue(viewModel.Entries.Any(
            entry => entry.EntryType == SettingsLogEntryPresentation.IssueType));
        Assert.IsFalse(viewModel.HasRawLogEntries);
        Assert.IsFalse(viewModel.IsRawLogCollectionAvailable);
        CollectionAssert.Contains(
            viewModel.EntryTypeOptions.ToArray(),
            SettingsLogEntryPresentation.RawLogType);
        Assert.IsFalse(viewModel.Entries.Any(
            entry => entry.EntryType == SettingsLogEntryPresentation.RawLogType));
    }

    [TestMethod]
    public void PresentationFiltersRestrictOnlyTheUnifiedView()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedEntryType = SettingsLogEntryPresentation.ActivityType;
        Assert.HasCount(1, viewModel.VisibleEntries);
        Assert.AreEqual(SettingsLogEntryPresentation.ActivityType, viewModel.VisibleEntries[0].EntryType);

        viewModel.SelectedEntryType = SettingsWorkspaceViewModel.AllEntryTypes;
        viewModel.SelectedSeverity = "Warning";
        Assert.HasCount(1, viewModel.VisibleEntries);
        Assert.AreEqual(SettingsLogEntryPresentation.IssueType, viewModel.VisibleEntries[0].EntryType);

        viewModel.SelectedSeverity = SettingsWorkspaceViewModel.AllSeverities;
        viewModel.SelectedArea = "Discovery";
        Assert.HasCount(2, viewModel.VisibleEntries);

        viewModel.SelectedOutcome = "Completed with issues";
        Assert.HasCount(2, viewModel.VisibleEntries);

        viewModel.SelectedStream = "Desktop";
        Assert.IsEmpty(viewModel.VisibleEntries);
        viewModel.SelectedStream = SettingsLogEntryPresentation.StructuredStream;
        Assert.HasCount(2, viewModel.VisibleEntries);

        viewModel.SearchText = "member.xml";
        Assert.HasCount(1, viewModel.VisibleEntries);
        Assert.AreEqual(SettingsLogEntryPresentation.IssueType, viewModel.VisibleEntries[0].EntryType);

        Assert.HasCount(1, viewModel.Attempts);
        Assert.HasCount(1, viewModel.Issues);
    }

    [TestMethod]
    public void SelectedEntryPreservesActivityItemsAndSecondaryIssueDetail()
    {
        var viewModel = CreateViewModel();
        var activity = viewModel.Entries.Single(entry => entry.IsActivity);
        var issue = viewModel.Entries.Single(entry => entry.IsIssue);

        viewModel.SelectedEntry = activity;
        Assert.IsNotNull(viewModel.SelectedAttempt);
        Assert.AreEqual(1, viewModel.SelectedAttempt.SuccessfulCount);
        Assert.AreEqual(1, viewModel.SelectedAttempt.FailedCount);
        Assert.AreEqual(1, viewModel.SelectedAttempt.UnprocessedCount);
        Assert.AreEqual("source-a", viewModel.SelectedAttempt.SuccessfulItems[0].Identity);

        viewModel.SelectedEntry = issue;
        Assert.IsNotNull(viewModel.SelectedIssue);
        Assert.AreEqual("One source item could not be processed.", issue.Message);
        Assert.AreEqual("XmlException at line 42.", issue.TechnicalDetail);
        Assert.AreEqual("member.xml", issue.ItemOrSource);
        Assert.AreNotEqual(issue.Message, issue.TechnicalDetail);
    }

    [TestMethod]
    public void SettingsUsesManagedPathsAndTruthfulUnavailableFutureState()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "CIA.Settings.V03", "LocalAppData");
        var logDirectory = Path.Combine(localAppData, "configured-logs");
        var paths = ApplicationPaths.FromLocalApplicationData(localAppData);
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(paths, logDirectory));

        CollectionAssert.AreEqual(
            new[] { "Temporary", "Working", "Database", "Logs", "Profiles", "Settings" },
            viewModel.ManagedStoragePaths.Select(path => path.Name).ToArray());
        Assert.AreEqual(paths.TempDirectory, FindPath(viewModel, "Temporary"));
        Assert.AreEqual(paths.WorkingDirectory, FindPath(viewModel, "Working"));
        Assert.AreEqual(paths.DatabaseDirectory, FindPath(viewModel, "Database"));
        Assert.AreEqual(logDirectory, FindPath(viewModel, "Logs"));
        Assert.AreEqual(paths.ProfilesDirectory, FindPath(viewModel, "Profiles"));
        Assert.AreEqual(paths.SettingsDirectory, FindPath(viewModel, "Settings"));
        Assert.IsTrue(viewModel.ManagedStoragePaths.Single(path => path.Name == "Logs").CanOpen);
        Assert.IsFalse(viewModel.ManagedStoragePaths
            .Where(path => path.Name != "Logs")
            .Any(path => path.CanOpen));
        Assert.IsFalse(viewModel.IsPersistentSettingsAvailable);
        Assert.IsFalse(viewModel.AreFutureSettingsActionsAvailable);
        Assert.IsFalse(viewModel.IsExportVisibleAvailable);
        StringAssert.Contains(viewModel.SettingsPersistenceText, "not available yet");
        Assert.IsFalse(viewModel.SettingsPersistenceText.Contains(
            "survive restart",
            StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(3, viewModel.DefaultArchiveNestingDepth);
        Assert.AreEqual("Not configured", viewModel.DefaultBlacklistText);
        Assert.IsFalse(viewModel.Entries.Any(entry => entry.Message.Contains("5,004", StringComparison.Ordinal)));
    }

    internal static SettingsWorkspaceViewModel CreateViewModel()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "CIA.Settings.V03", "LocalAppData");
        var paths = ApplicationPaths.FromLocalApplicationData(localAppData);
        return new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(paths, paths.LogsDirectory));
    }

    private static IProcessingHistoryReader CreateReader()
    {
        var correlation = OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed"),
                OperationItemStatus.Unprocessed("source-c", "not-scheduled")
            ]);
        var attempt = ProcessingAttemptRecord.FromCompletion(
            "Discovery",
            "Source interpretation",
            correlation.InitiatedAtUtc.AddMinutes(1),
            completion);
        var diagnostic = new ProcessingDiagnosticRecord(
            DiagnosticRecordId.CreateNew(),
            correlation,
            "Discovery",
            "Source interpretation",
            "source-b",
            "member.xml",
            OperationItemState.Failed,
            correlation.InitiatedAtUtc.AddMinutes(2),
            OperationOutcome.CompletedWithIssues,
            "One source item could not be processed.",
            "parse-failed",
            "XmlException at line 42.");
        return new StaticHistoryReader(
            new ProcessingHistorySnapshot([attempt], [diagnostic], ReadProblem: null));
    }

    private static string FindPath(SettingsWorkspaceViewModel viewModel, string name)
    {
        return viewModel.ManagedStoragePaths.Single(path => path.Name == name).Path;
    }

    private sealed class StaticHistoryReader(ProcessingHistorySnapshot snapshot)
        : IProcessingHistoryReader
    {
        public ProcessingHistorySnapshot Read()
        {
            return snapshot;
        }
    }
}

[TestClass]
[DoNotParallelize]
public sealed class SettingsWorkspaceV03InteractionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task RealViewSwitchesResponsivelyAndKeepsAboutWithSettings()
    {
        await WpfTestApplication.RunAsync(VerifyInteraction).WaitAsync(TestTimeout);
    }

    private static void VerifyInteraction()
    {
        var view = new SettingsWorkspaceView
        {
            DataContext = SettingsWorkspaceV03Tests.CreateViewModel()
        };
        var window = new Window
        {
            Width = 1400,
            Height = 760,
            Content = view,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var internalSwitch = (Border)view.FindName("InternalSwitch");
            var logSide = (Grid)view.FindName("LogDiagnosticsSide");
            var settingsSide = (Border)view.FindName("SettingsSide");
            var aboutHelp = (Grid)view.FindName("AboutHelpPanel");
            var entries = (ListView)view.FindName("LogEntriesList");
            var export = (Button)view.FindName("ExportVisibleButton");
            var reset = (Button)view.FindName("ResetDefaultsButton");
            var saveState = (Button)view.FindName("SaveStateButton");
            var cleanTemporary = (Button)view.FindName("CleanTemporaryDataButton");

            Assert.AreEqual(Visibility.Collapsed, internalSwitch.Visibility);
            Assert.AreEqual(Visibility.Visible, logSide.Visibility);
            Assert.AreEqual(Visibility.Visible, settingsSide.Visibility);
            Assert.AreEqual(Visibility.Visible, aboutHelp.Visibility);
            Assert.AreEqual(2, entries.Items.Count);
            Assert.IsFalse(export.IsEnabled);
            Assert.IsFalse(reset.IsEnabled);
            Assert.IsFalse(saveState.IsEnabled);
            Assert.IsFalse(cleanTemporary.IsEnabled);

            window.Width = 1100;
            window.UpdateLayout();

            Assert.AreEqual(Visibility.Visible, internalSwitch.Visibility);
            Assert.AreEqual(Visibility.Visible, logSide.Visibility);
            Assert.AreEqual(Visibility.Collapsed, settingsSide.Visibility);

            var settingsButton = (Button)view.FindName("SettingsViewButton");
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();

            Assert.AreEqual(Visibility.Collapsed, logSide.Visibility);
            Assert.AreEqual(Visibility.Visible, settingsSide.Visibility);
            Assert.AreEqual(Visibility.Visible, aboutHelp.Visibility);
        }
        finally
        {
            window.Close();
        }
    }
}
