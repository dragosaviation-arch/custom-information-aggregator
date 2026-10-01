using System.Windows;
using System.Windows.Controls;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.ManagedStorage;
using CIA.Core.Profiles;
using CIA.Core.Runtime;
using CIA.Desktop.Presentation;
using CIA.Desktop.Views;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class SettingsWorkspaceV03Tests
{
    private static readonly TimeSpan CleanupTestTimeout = TimeSpan.FromSeconds(20);

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
        Assert.HasCount(2, viewModel.VisibleEntries);
        Assert.IsTrue(viewModel.VisibleEntries.Any(entry => entry.IsActivity));
        Assert.IsTrue(viewModel.VisibleEntries.Any(entry => entry.IsIssue));

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
    public void ActivitySeverityReflectsTerminalOutcomeAndFiltersActivityRows()
    {
        var outcomes = new[]
        {
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.CompletedWithIssues,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled,
            OperationOutcome.InterruptedIncomplete
        };
        var attempts = outcomes
            .Select((outcome, index) => CreateAttempt(outcome, index))
            .ToArray();
        var viewModel = CreateViewModel(
            new ProcessingHistorySnapshot(attempts, [], ReadProblem: null));
        var severityByOutcome = viewModel.Entries
            .Where(entry => entry.IsActivity)
            .ToDictionary(entry => entry.Attempt!.TerminalOutcome, entry => entry.Severity);

        Assert.AreEqual("Info", severityByOutcome[OperationOutcome.CompletedSuccessfully]);
        Assert.AreEqual("Warning", severityByOutcome[OperationOutcome.CompletedWithIssues]);
        Assert.AreEqual("Error", severityByOutcome[OperationOutcome.Failed]);
        Assert.AreEqual("Warning", severityByOutcome[OperationOutcome.Cancelled]);
        Assert.AreEqual("Warning", severityByOutcome[OperationOutcome.InterruptedIncomplete]);
        Assert.IsTrue(
            viewModel.Entries
                .Where(entry => entry.IsActivity)
                .All(entry => entry.FailureCode is null && entry.FailureCodeDisplay is null));

        viewModel.SelectedEntryType = SettingsLogEntryPresentation.ActivityType;
        viewModel.SelectedSeverity = "Warning";
        CollectionAssert.AreEquivalent(
            new[]
            {
                OperationOutcome.CompletedWithIssues,
                OperationOutcome.Cancelled,
                OperationOutcome.InterruptedIncomplete
            },
            viewModel.VisibleEntries.Select(entry => entry.Attempt!.TerminalOutcome).ToArray());

        viewModel.SelectedSeverity = "Error";
        Assert.HasCount(1, viewModel.VisibleEntries);
        Assert.AreEqual(OperationOutcome.Failed, viewModel.VisibleEntries[0].Attempt!.TerminalOutcome);

        viewModel.SelectedSeverity = "Info";
        Assert.HasCount(1, viewModel.VisibleEntries);
        Assert.AreEqual(
            OperationOutcome.CompletedSuccessfully,
            viewModel.VisibleEntries[0].Attempt!.TerminalOutcome);
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
        Assert.AreEqual("parse-failed", issue.FailureCode);
        Assert.AreEqual("Failure code: parse-failed", issue.FailureCodeDisplay);
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
        Assert.IsTrue(viewModel.IsPersistentSettingsAvailable);
        Assert.IsTrue(viewModel.ResetSettingsCommand.CanExecute(null));
        Assert.IsFalse(viewModel.IsExportVisibleAvailable);
        StringAssert.Contains(viewModel.SettingsPersistenceText, "Persistent settings");
        StringAssert.Contains(viewModel.SettingsPersistenceText, "after CIA restarts");
        Assert.AreEqual(3, viewModel.DefaultArchiveNestingDepth);
        Assert.AreEqual("Not configured", viewModel.DefaultBlacklistText);
        Assert.IsFalse(viewModel.Entries.Any(entry => entry.Message.Contains("5,004", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CleanupCommandInvokesOnceAndReportsTruthfulOutcomeCounts()
    {
        var cleanup = new BlockingCleanupService(new ManagedStorageCleanupResult(
            ManagedStorageCleanupOutcome.CompletedWithItemFailures,
            [
                CreateCleanupItem(ManagedStorageCleanupItemState.Removed, "removed"),
                CreateCleanupItem(ManagedStorageCleanupItemState.SkippedNoLongerEligible, "skipped"),
                CreateCleanupItem(ManagedStorageCleanupItemState.FailedToRemove, "failed")
            ],
            FailureDescription: null));
        var localAppData = Path.Combine(Path.GetTempPath(), "CIA.Settings.Cleanup", Guid.NewGuid().ToString("N"));
        var paths = ApplicationPaths.FromLocalApplicationData(localAppData);
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(paths, paths.LogsDirectory),
            managedStorageCleanupService: cleanup);

        var firstInvocation = viewModel.CleanupManagedStorageCommand.ExecuteAsync(null);
        await cleanup.Started.Task.WaitAsync(CleanupTestTimeout);
        Assert.IsTrue(viewModel.IsCleanupRunning);
        Assert.IsFalse(viewModel.CleanupManagedStorageCommand.CanExecute(null));

        var duplicateInvocation = viewModel.CleanupManagedStorageCommand.ExecuteAsync(null);
        cleanup.Release.TrySetResult();
        await Task.WhenAll(firstInvocation, duplicateInvocation).WaitAsync(CleanupTestTimeout);

        Assert.AreEqual(1, cleanup.CallCount);
        Assert.IsFalse(viewModel.IsCleanupRunning);
        StringAssert.Contains(viewModel.CleanupStatusText, "completed with item failures");
        StringAssert.Contains(viewModel.CleanupStatusText, "1 removed");
        StringAssert.Contains(viewModel.CleanupStatusText, "1 skipped/protected");
        StringAssert.Contains(viewModel.CleanupStatusText, "1 failed");
    }

    [TestMethod]
    public void PersistentSettingsAreaValidatesSavesAndReportsRestartBoundary()
    {
        using var root = new TemporarySettingsDirectory();
        var localAppData = Path.Combine(root.Path, "LocalAppData");
        var service = new ApplicationSettingsService(new ApplicationSettingsStore(localAppData));
        var runtimePaths = new SettingsWorkspaceRuntimePaths(
            service.RuntimePaths,
            service.RuntimePaths.LogsDirectory);
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            runtimePaths,
            service);
        var startupSettingsDirectory = service.RuntimePaths.SettingsDirectory;
        Assert.AreEqual(service.Startup.SettingsFilePath, viewModel.ConfiguredSettingsFilePath);
        StringAssert.Contains(
            viewModel.SettingsPersistenceText,
            service.Startup.SettingsFilePath);
        var configuredSettingsDirectory = Path.Combine(root.Path, "Configured", "Settings");
        viewModel.TemporaryDirectory = Path.Combine(root.Path, "Configured", "Temp");
        viewModel.WorkingDirectory = Path.Combine(root.Path, "Configured", "Working");
        viewModel.ProfilesDirectory = Path.Combine(root.Path, "Configured", "Profiles");
        viewModel.SettingsDirectory = configuredSettingsDirectory;
        viewModel.TraverseSubfolders = false;
        viewModel.MaximumArchiveNestingDepth = 8;
        viewModel.PersistentArchiveExtractionEnabled = true;
        viewModel.PersistentArchiveExtractionDirectory = Path.Combine(
            root.Path,
            "Configured",
            "ArchiveExtraction");
        viewModel.SelectedPostExportBehavior = viewModel.PostExportBehaviorOptions.Single(option =>
            option.Value == PostExportBehavior.OpenExportedFile);

        viewModel.SaveSettingsCommand.Execute(null);

        StringAssert.Contains(viewModel.SettingsStatusText, "Restart CIA");
        Assert.IsTrue(viewModel.IsSettingsRestartRequired);
        var relocatedSettingsFile = Path.Combine(
            configuredSettingsDirectory,
            ApplicationSettingsStore.SettingsFileName);
        Assert.AreEqual(relocatedSettingsFile, viewModel.ConfiguredSettingsFilePath);
        StringAssert.Contains(viewModel.SettingsPersistenceText, relocatedSettingsFile);
        StringAssert.Contains(viewModel.SettingsPersistenceText, startupSettingsDirectory);
        Assert.AreEqual(startupSettingsDirectory, service.RuntimePaths.SettingsDirectory);
        var reopened = new ApplicationSettingsService(new ApplicationSettingsStore(localAppData));
        Assert.AreEqual(configuredSettingsDirectory, reopened.Current.SettingsDirectory);
        Assert.AreEqual(configuredSettingsDirectory, reopened.RuntimePaths.SettingsDirectory);
        Assert.IsFalse(reopened.Current.TraverseSubfolders);
        Assert.AreEqual(8, reopened.Current.MaximumArchiveNestingDepth.Value);
        Assert.IsTrue(reopened.Current.PersistentArchiveExtractionEnabled);
        Assert.AreEqual(PostExportBehavior.OpenExportedFile, reopened.Current.PostExportBehavior);
    }

    [TestMethod]
    public void ResetPersistsCanonicalDefaultsUpdatesPresentationAndLeavesSavedDataUntouched()
    {
        using var root = new TemporarySettingsDirectory();
        var localAppData = Path.Combine(root.Path, "LocalAppData");
        var first = new ApplicationSettingsService(new ApplicationSettingsStore(localAppData));
        var configured = first.Current with
        {
            TemporaryDirectory = Path.Combine(root.Path, "Configured", "Temp"),
            WorkingDirectory = Path.Combine(root.Path, "Configured", "Working"),
            ProfilesDirectory = Path.Combine(root.Path, "Configured", "Profiles"),
            SettingsDirectory = Path.Combine(root.Path, "Configured", "Settings"),
            TraverseSubfolders = false,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(8),
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = Path.Combine(root.Path, "Configured", "Extraction"),
            LastUsedOutputDirectory = Path.Combine(root.Path, "Configured", "Output"),
            PostExportBehavior = PostExportBehavior.OpenContainingFolder
        };
        Assert.IsTrue(first.Save(configured).Succeeded);
        var service = new ApplicationSettingsService(new ApplicationSettingsStore(localAppData));
        var cleanup = new RecordingCleanupService();
        var preservedFiles = new[]
        {
            WriteEvidence(Path.Combine(root.Path, "External", "source.xml"), "source"),
            WriteEvidence(Path.Combine(root.Path, "Exports", "completed.xlsx"), "export"),
            WriteEvidence(Path.Combine(configured.WorkingDirectory, "SavedStates", "saved.cia"), "state"),
            WriteEvidence(Path.Combine(configured.ProfilesDirectory, "selection" + ProfileArtifactStore.FileExtension), "selection-profile"),
            WriteEvidence(Path.Combine(configured.ProfilesDirectory, "blacklist" + ProfileArtifactStore.FileExtension), "blacklist-profile"),
            WriteEvidence(Path.Combine(configured.ProfilesDirectory, DefaultBlacklistProfileDesignationStore.FileName), "default-blacklist")
        };
        var preservedBytes = preservedFiles.ToDictionary(
            path => path,
            File.ReadAllBytes,
            StringComparer.OrdinalIgnoreCase);
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(
                service.RuntimePaths,
                service.RuntimePaths.LogsDirectory),
            service,
            managedStorageCleanupService: cleanup);
        var expected = ApplicationSettings.CreateDefault(localAppData);

        viewModel.ResetSettingsCommand.Execute(null);

        Assert.AreEqual(expected, service.Current);
        Assert.AreEqual(expected.TemporaryDirectory, viewModel.TemporaryDirectory);
        Assert.AreEqual(expected.WorkingDirectory, viewModel.WorkingDirectory);
        Assert.AreEqual(expected.ProfilesDirectory, viewModel.ProfilesDirectory);
        Assert.AreEqual(expected.SettingsDirectory, viewModel.SettingsDirectory);
        Assert.AreEqual(expected.TraverseSubfolders, viewModel.TraverseSubfolders);
        Assert.AreEqual(
            expected.MaximumArchiveNestingDepth.Value,
            viewModel.MaximumArchiveNestingDepth);
        Assert.AreEqual(
            expected.PersistentArchiveExtractionEnabled,
            viewModel.PersistentArchiveExtractionEnabled);
        Assert.AreEqual(string.Empty, viewModel.PersistentArchiveExtractionDirectory);
        Assert.AreEqual(
            expected.PostExportBehavior,
            viewModel.SelectedPostExportBehavior.Value);
        Assert.IsNull(service.Current.LastUsedOutputDirectory);
        Assert.IsTrue(viewModel.IsSettingsRestartRequired);
        Assert.AreEqual(configured.SettingsDirectory, service.RuntimePaths.SettingsDirectory);
        StringAssert.Contains(viewModel.SettingsStatusText, "reset to defaults and saved");
        StringAssert.Contains(viewModel.SettingsStatusText, "Restart CIA");
        Assert.AreEqual(0, cleanup.CallCount);
        foreach (var path in preservedFiles)
        {
            CollectionAssert.AreEqual(preservedBytes[path], File.ReadAllBytes(path));
        }

        Assert.AreEqual(
            expected,
            new ApplicationSettingsService(new ApplicationSettingsStore(localAppData)).Current);
    }

    [TestMethod]
    public void ResetPersistenceFailureLeavesCurrentPresentationAndStoredSettingsUnchanged()
    {
        using var root = new TemporarySettingsDirectory();
        var localAppData = Path.Combine(root.Path, "LocalAppData");
        var first = new ApplicationSettingsService(new ApplicationSettingsStore(localAppData));
        var configured = first.Current with
        {
            TraverseSubfolders = false,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(7),
            LastUsedOutputDirectory = Path.Combine(root.Path, "Output"),
            PostExportBehavior = PostExportBehavior.AskEachTime
        };
        Assert.IsTrue(first.Save(configured).Succeeded);
        var service = new ApplicationSettingsService(
            new ApplicationSettingsStore(localAppData, new AlwaysFailingSettingsWriter()));
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(
                service.RuntimePaths,
                service.RuntimePaths.LogsDirectory),
            service);

        viewModel.ResetSettingsCommand.Execute(null);

        Assert.AreEqual(configured, service.Current);
        Assert.IsFalse(viewModel.TraverseSubfolders);
        Assert.AreEqual(7, viewModel.MaximumArchiveNestingDepth);
        Assert.AreEqual(PostExportBehavior.AskEachTime, viewModel.SelectedPostExportBehavior.Value);
        StringAssert.Contains(viewModel.SettingsStatusText, "could not be saved");
        Assert.AreEqual(
            configured,
            new ApplicationSettingsService(new ApplicationSettingsStore(localAppData)).Current);
    }

    [TestMethod]
    public void PersistentExtractionWithoutDestinationShowsActionableValidation()
    {
        using var root = new TemporarySettingsDirectory();
        var service = new ApplicationSettingsService(
            new ApplicationSettingsStore(Path.Combine(root.Path, "LocalAppData")));
        var viewModel = new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(
                service.RuntimePaths,
                service.RuntimePaths.LogsDirectory),
            service)
        {
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = string.Empty
        };

        viewModel.SaveSettingsCommand.Execute(null);

        StringAssert.Contains(
            viewModel.SettingsStatusText,
            "requires a configured destination");
        Assert.IsFalse(File.Exists(service.Startup.SettingsFilePath));
    }

    internal static SettingsWorkspaceViewModel CreateViewModel()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "CIA.Settings.V03", "LocalAppData");
        var paths = ApplicationPaths.FromLocalApplicationData(localAppData);
        return new SettingsWorkspaceViewModel(
            CreateReader(),
            new SettingsWorkspaceRuntimePaths(paths, paths.LogsDirectory));
    }

    private static SettingsWorkspaceViewModel CreateViewModel(
        ProcessingHistorySnapshot snapshot)
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "CIA.Settings.V03", "LocalAppData");
        var paths = ApplicationPaths.FromLocalApplicationData(localAppData);
        return new SettingsWorkspaceViewModel(
            new StaticHistoryReader(snapshot),
            new SettingsWorkspaceRuntimePaths(paths, paths.LogsDirectory));
    }

    private static ProcessingAttemptRecord CreateAttempt(
        OperationOutcome outcome,
        int index)
    {
        var correlation = OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 8, 11, index, 0, TimeSpan.Zero));
        var item = outcome switch
        {
            OperationOutcome.CompletedSuccessfully => OperationItemStatus.ProcessedSuccessfully(
                $"item-{index}"),
            OperationOutcome.CompletedWithIssues or OperationOutcome.Failed => OperationItemStatus.Failed(
                $"item-{index}",
                "test-failure"),
            _ => OperationItemStatus.Unprocessed($"item-{index}", "not-completed")
        };
        var completion = outcome is OperationOutcome.CompletedSuccessfully
            or OperationOutcome.CompletedWithIssues
            ? OperationCompletion.FromCompletedItems(correlation, [item])
            : OperationCompletion.FromTerminalOutcome(correlation, outcome, [item]);
        return ProcessingAttemptRecord.FromCompletion(
            "Test operation",
            "Test stage",
            correlation.InitiatedAtUtc.AddSeconds(1),
            completion);
    }

    private static ManagedStorageCleanupItemResult CreateCleanupItem(
        ManagedStorageCleanupItemState state,
        string name) =>
        new(
            ManagedStorageArtifactId.CreateNew(),
            Path.GetFullPath(name),
            state,
            ProtectionReasons: [],
            Problem: null);

    private static string WriteEvidence(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class AlwaysFailingSettingsWriter : IAtomicSettingsFileWriter
    {
        public void Write(string finalPath, ReadOnlyMemory<byte> content) =>
            throw new IOException("Injected settings reset failure.");
    }

    private sealed class RecordingCleanupService : IManagedStorageCleanupService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ManagedStorageCleanupResult Cleanup()
        {
            Interlocked.Increment(ref _callCount);
            return new ManagedStorageCleanupResult(
                ManagedStorageCleanupOutcome.CompletedSuccessfully,
                Items: [],
                FailureDescription: null);
        }
    }

    private sealed class BlockingCleanupService(ManagedStorageCleanupResult result)
        : IManagedStorageCleanupService
    {
        private int _callCount;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public ManagedStorageCleanupResult Cleanup()
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return result;
        }
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

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        private static readonly string TestRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR96.SettingsUI.Tests");

        public TemporarySettingsDirectory()
        {
            Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
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
            var selectedFailureCode = (TextBlock)view.FindName("SelectedFailureCode");
            var saveSettings = (Button)view.FindName("SavePersistentSettingsButton");
            var browseButtons = new[]
            {
                (Button)view.FindName("BrowseTemporaryDirectoryButton"),
                (Button)view.FindName("BrowseWorkingDirectoryButton"),
                (Button)view.FindName("BrowseProfilesDirectoryButton"),
                (Button)view.FindName("BrowseSettingsDirectoryButton"),
                (Button)view.FindName("BrowsePersistentExtractionDirectoryButton")
            };

            Assert.AreEqual(Visibility.Collapsed, internalSwitch.Visibility);
            Assert.AreEqual(Visibility.Visible, logSide.Visibility);
            Assert.AreEqual(Visibility.Visible, settingsSide.Visibility);
            Assert.AreEqual(Visibility.Visible, aboutHelp.Visibility);
            Assert.AreEqual(2, entries.Items.Count);
            Assert.IsFalse(export.IsEnabled);
            Assert.IsTrue(reset.IsEnabled);
            Assert.IsFalse(saveState.IsEnabled);
            Assert.IsFalse(cleanTemporary.IsEnabled);
            Assert.IsTrue(saveSettings.IsEnabled);
            Assert.IsTrue(browseButtons.All(button => Equals(button.Content, "Browse...")));
            Assert.AreEqual("Failure code: parse-failed", selectedFailureCode.Text);
            Assert.AreEqual(Visibility.Visible, selectedFailureCode.Visibility);

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
