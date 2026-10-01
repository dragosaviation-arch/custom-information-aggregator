using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.Desktop.Export;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class PostExportBehaviorCoordinatorTests
{
    [TestMethod]
    public void StatusOnlyPerformsNoShellOrPromptAction()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.StatusOnly);
        var batch = CreateBatch(directory.Path, "Status.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        Assert.IsEmpty(fixture.Launcher.Paths);
        Assert.AreEqual(0, fixture.Prompt.CallCount);
    }

    [TestMethod]
    public void OpenContainingFolderUsesSuccessfulBatchDirectoryExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenContainingFolder);
        var batch = CreateBatch(directory.Path, "One.xlsx", "Two.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { batch.OutputDirectory },
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void OpenExportedFileUsesExactAuthoritativeFinalPath()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenExportedFile);
        var batch = CreateBatch(directory.Path, "Renamed by collision handling.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { batch.Workbooks[0].FinalPath },
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void OpenExportedFileUsesDeterministicBatchOrderForEveryWorkbook()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenExportedFile);
        var batch = CreateBatch(directory.Path, "Third name.xlsx", "First name.xlsx", "Second name.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            batch.Workbooks.Select(workbook => workbook.FinalPath).ToArray(),
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void MismatchedCompletedOperationDoesNotPromptOrLaunchStaleBatch()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            PostExportBehavior.AskEachTime,
            PostExportChoice.OpenExportedFiles);
        var batch = CreateBatch(directory.Path, "Stale.xlsx");

        var result = fixture.Coordinator.Apply(OperationId.CreateNew(), batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, fixture.Prompt.CallCount);
        Assert.IsEmpty(fixture.Launcher.Paths);
    }

    [TestMethod]
    public void AskEachTimeOpenFilesUsesBatchFileAction()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            PostExportBehavior.AskEachTime,
            PostExportChoice.OpenExportedFiles);
        var batch = CreateBatch(directory.Path, "One.xlsx", "Two.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, fixture.Prompt.CallCount);
        Assert.AreSame(batch, fixture.Prompt.Batch);
        CollectionAssert.AreEqual(
            batch.Workbooks.Select(workbook => workbook.FinalPath).ToArray(),
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void AskEachTimeOpenFolderUsesBatchDirectoryOnce()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            PostExportBehavior.AskEachTime,
            PostExportChoice.OpenContainingFolder);
        var batch = CreateBatch(directory.Path, "One.xlsx", "Two.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { batch.OutputDirectory },
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void AskEachTimeDismissalUsesStatusOnly()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            PostExportBehavior.AskEachTime,
            PostExportChoice.StatusOnly);
        var batch = CreateBatch(directory.Path, "One.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, fixture.Prompt.CallCount);
        Assert.IsEmpty(fixture.Launcher.Paths);
    }

    [TestMethod]
    public void AskEachTimeChoiceIsNotPersistedToSettings()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(
            directory.Path,
            PostExportBehavior.AskEachTime,
            PostExportChoice.OpenContainingFolder);
        var batch = CreateBatch(directory.Path, "One.xlsx");

        fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.AreEqual(PostExportBehavior.AskEachTime, fixture.Settings.Current.PostExportBehavior);
        var reopened = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.AreEqual(PostExportBehavior.AskEachTime, reopened.Current.PostExportBehavior);
    }

    [TestMethod]
    public void PromptFailureRetainsSuccessfulExportAndReportsActionableStatus()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.AskEachTime);
        fixture.Prompt.Failure = new InvalidOperationException("dialog unavailable");
        var batch = CreateBatch(directory.Path, "One.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.FailureDescription, "Export completed");
        StringAssert.Contains(result.FailureDescription, "dialog unavailable");
        Assert.IsEmpty(fixture.Launcher.Paths);
    }

    [TestMethod]
    public void FolderLaunchFailureReportsActionableStatus()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenContainingFolder);
        fixture.Launcher.Failures.Add(Path.GetFullPath(directory.Path));
        var batch = CreateBatch(directory.Path, "One.xlsx");

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.FailureDescription, "containing folder could not be opened");
        CollectionAssert.AreEqual(
            new[] { batch.OutputDirectory },
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void OneWorkbookLaunchFailureDoesNotPreventLaterBatchAttempts()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenExportedFile);
        var batch = CreateBatch(directory.Path, "One.xlsx", "Two.xlsx", "Three.xlsx");
        fixture.Launcher.Failures.Add(batch.Workbooks[1].FinalPath);

        var result = fixture.Coordinator.Apply(batch.OperationId, batch);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.FailureDescription, "workbook could not be opened");
        CollectionAssert.AreEqual(
            batch.Workbooks.Select(workbook => workbook.FinalPath).ToArray(),
            fixture.Launcher.Paths.ToArray());
    }

    [TestMethod]
    public void NewlyCompletedBatchIdentitySelectsOnlyThatBatch()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.OpenExportedFile);
        var first = CreateBatch(directory.Path, "First.xlsx");
        var second = CreateBatch(directory.Path, "Second.xlsx");

        fixture.Coordinator.Apply(first.OperationId, first);
        fixture.Coordinator.Apply(second.OperationId, second);

        CollectionAssert.AreEqual(
            new[] { first.Workbooks[0].FinalPath, second.Workbooks[0].FinalPath },
            fixture.Launcher.Paths.ToArray());
        Assert.AreNotEqual(first.OperationId, second.OperationId);
    }

    [TestMethod]
    public void ActiveSettingIsReadForEachSuccessfulExport()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, PostExportBehavior.StatusOnly);
        var first = CreateBatch(directory.Path, "First.xlsx");
        var second = CreateBatch(directory.Path, "Second.xlsx");

        fixture.Coordinator.Apply(first.OperationId, first);
        Assert.IsTrue(fixture.Settings.Save(fixture.Settings.Current with
        {
            PostExportBehavior = PostExportBehavior.OpenContainingFolder
        }).Succeeded);
        fixture.Coordinator.Apply(second.OperationId, second);

        CollectionAssert.AreEqual(
            new[] { second.OutputDirectory },
            fixture.Launcher.Paths.ToArray());
    }

    private static Fixture CreateFixture(
        string localApplicationData,
        PostExportBehavior behavior,
        PostExportChoice choice = PostExportChoice.StatusOnly)
    {
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(localApplicationData));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = behavior
        }).Succeeded);
        var prompt = new RecordingPrompt(choice);
        var launcher = new RecordingLauncher();
        return new Fixture(
            settings,
            prompt,
            launcher,
            new PostExportBehaviorCoordinator(settings, prompt, launcher));
    }

    private static WorkbookExportBatchSummary CreateBatch(
        string outputDirectory,
        params string[] fileNames)
    {
        var sourceSetId = SourceSetId.CreateNew();
        return new WorkbookExportBatchSummary(
            OperationId.CreateNew(),
            OperationId.CreateNew(),
            outputDirectory,
            fileNames.Select((fileName, index) => new WorkbookExportFileSummary(
                WorkbookDefinitionId.CreateNew(),
                Path.Combine(outputDirectory, fileName),
                index + 1,
                [new WorkbookExportWorksheetSummary(
                    WorksheetDefinitionId.CreateNew(),
                    sourceSetId,
                    $"Results {index + 1}",
                    1,
                    2,
                    1)])).ToArray());
    }

    private sealed record Fixture(
        ApplicationSettingsService Settings,
        RecordingPrompt Prompt,
        RecordingLauncher Launcher,
        PostExportBehaviorCoordinator Coordinator);

    private sealed class RecordingPrompt(PostExportChoice choice) : IPostExportPrompt
    {
        public int CallCount { get; private set; }

        public WorkbookExportBatchSummary? Batch { get; private set; }

        public Exception? Failure { get; set; }

        public PostExportChoice Choose(WorkbookExportBatchSummary batch)
        {
            CallCount++;
            Batch = batch;
            if (Failure is not null)
            {
                throw Failure;
            }

            return choice;
        }
    }

    private sealed class RecordingLauncher : IPostExportLauncher
    {
        public List<string> Paths { get; } = [];

        public HashSet<string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Open(string path)
        {
            Paths.Add(path);
            if (Failures.Contains(path))
            {
                throw new InvalidOperationException("shell unavailable");
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CIA.SPR89.Desktop.Tests",
                Guid.NewGuid().ToString("N"));
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
