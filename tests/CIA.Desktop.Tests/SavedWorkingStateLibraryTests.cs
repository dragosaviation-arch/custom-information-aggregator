using CIA.Contracts.Discovery;
using CIA.Contracts.WorkingState;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;
using CIA.Desktop.WorkingState;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class SavedWorkingStateLibraryTests
{
    [TestMethod]
    public void LibraryResolvesBeneathRuntimeWorkingDirectoryAndCreatesOnlyManagedChild()
    {
        using var environment = new SavedStateTestEnvironment();
        var expected = Path.Combine(
            environment.Paths.WorkingDirectory,
            SavedWorkingStateLibrary.DirectoryName);

        Assert.AreEqual(expected, environment.Library.DirectoryPath);
        Assert.IsFalse(Directory.Exists(expected));

        var target = environment.Library.ResolveNewTarget("Alpha state");

        Assert.IsTrue(target.Succeeded, target.Problem);
        Assert.AreEqual(Path.Combine(expected, "Alpha state.cia"), target.Path);
        Assert.IsTrue(Directory.Exists(expected));
        CollectionAssert.AreEquivalent(
            new[] { expected },
            Directory.GetDirectories(environment.Paths.WorkingDirectory));
    }

    [TestMethod]
    public void InventoryListsOnlyDirectCiaFilesWithoutModifyingWorkingContent()
    {
        using var environment = new SavedStateTestEnvironment();
        Directory.CreateDirectory(environment.Library.DirectoryPath);
        var direct = Path.Combine(environment.Library.DirectoryPath, "Direct.cia");
        var unrelated = Path.Combine(environment.Paths.WorkingDirectory, "unrelated.txt");
        var incomplete = Path.Combine(environment.Library.DirectoryPath, "partial.cia.incomplete");
        var nestedDirectory = Path.Combine(environment.Library.DirectoryPath, "Nested");
        var nested = Path.Combine(nestedDirectory, "Nested.cia");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllBytes(direct, [1, 2, 3]);
        File.WriteAllText(unrelated, "unrelated");
        File.WriteAllBytes(incomplete, [4, 5]);
        File.WriteAllBytes(nested, [6, 7]);
        var before = Directory.GetFiles(
                environment.Paths.WorkingDirectory,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

        var inventory = environment.Library.CreateInventory();

        Assert.HasCount(1, inventory.States);
        Assert.AreEqual("Direct", inventory.States[0].Name);
        Assert.AreEqual(Path.GetFullPath(direct), inventory.States[0].Path);
        Assert.AreEqual(3, inventory.States[0].SizeBytes);
        Assert.IsEmpty(inventory.Problems);
        var after = Directory.GetFiles(
                environment.Paths.WorkingDirectory,
                "*",
                SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (var item in before)
        {
            CollectionAssert.AreEqual(item.Value, after[item.Key]);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("nested/state")]
    [DataRow("nested\\state")]
    [DataRow("C:\\state")]
    [DataRow("CON")]
    [DataRow("LPT1.report")]
    [DataRow("state.cia")]
    [DataRow("trailing.")]
    [DataRow(" leading")]
    public void InvalidOrUnsafeStateNamesAreRejected(string? stateName)
    {
        using var environment = new SavedStateTestEnvironment();

        var result = environment.Library.ResolveNewTarget(stateName);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Problem);
        Assert.IsFalse(Directory.Exists(environment.Library.DirectoryPath));
    }

    [TestMethod]
    public void OverlongStateNameIsRejectedWithoutCreatingLibrary()
    {
        using var environment = new SavedStateTestEnvironment();

        var result = environment.Library.ResolveNewTarget(
            new string('a', SavedWorkingStateLibrary.MaximumStateNameLength + 1));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Problem, "cannot exceed");
        Assert.IsFalse(Directory.Exists(environment.Library.DirectoryPath));
    }

    [TestMethod]
    public void DuplicateTargetIsRejectedWithoutChangingExistingBytes()
    {
        using var environment = new SavedStateTestEnvironment();
        var target = environment.Library.ResolveNewTarget("Existing");
        Assert.IsTrue(target.Succeeded);
        byte[] original = [8, 6, 7, 5, 3, 0, 9];
        File.WriteAllBytes(target.Path!, original);

        var duplicate = environment.Library.ResolveNewTarget("Existing");

        Assert.IsFalse(duplicate.Succeeded);
        StringAssert.Contains(duplicate.Problem, "already exists");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target.Path!));
    }

    [TestMethod]
    public void StaleSelectionAndExternalPackageCannotBeDeleted()
    {
        using var environment = new SavedStateTestEnvironment();
        var selected = environment.CreateState("Selected", [1, 2, 3]);
        var external = Path.Combine(environment.Root, "external.cia");
        File.WriteAllBytes(external, [9, 9, 9]);
        File.WriteAllBytes(selected.Path, [1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(selected.Path, DateTime.UtcNow.AddSeconds(2));
        var forgedExternal = selected with
        {
            Name = "external",
            Path = external,
            Identity = selected.Identity with { CanonicalPath = external }
        };

        var staleDelete = environment.Library.Delete(selected);
        var externalDelete = environment.Library.Delete(forgedExternal);

        Assert.IsFalse(staleDelete.Succeeded);
        StringAssert.Contains(staleDelete.Problem, "changed after it was listed");
        Assert.IsFalse(externalDelete.Succeeded);
        Assert.IsTrue(File.Exists(selected.Path));
        Assert.IsTrue(File.Exists(external));
    }

    [TestMethod]
    public void SameLengthReplacementWithPreservedModifiedTimeFailsStaleSelectionCheck()
    {
        using var environment = new SavedStateTestEnvironment();
        var selected = environment.CreateState("Replaced", [1, 2, 3, 4]);
        var originalModified = File.GetLastWriteTimeUtc(selected.Path);
        File.WriteAllBytes(selected.Path, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(selected.Path, originalModified);

        var result = environment.Library.Delete(selected);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Problem, "changed after it was listed");
        CollectionAssert.AreEqual(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(selected.Path));
    }

    [TestMethod]
    public void ReparsePackageIsNotListedOrDeletedAndExternalBytesRemainUntouched()
    {
        using var environment = new SavedStateTestEnvironment();
        Directory.CreateDirectory(environment.Library.DirectoryPath);
        var external = Path.Combine(environment.Root, "external.cia");
        byte[] bytes = [4, 3, 2, 1];
        File.WriteAllBytes(external, bytes);
        var link = Path.Combine(environment.Library.DirectoryPath, "linked.cia");
        try
        {
            File.CreateSymbolicLink(link, external);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
        {
            return;
        }

        var inventory = environment.Library.CreateInventory();

        Assert.IsEmpty(inventory.States);
        Assert.HasCount(1, inventory.Problems);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(external));
    }

    [TestMethod]
    public void DeleteFailurePreservesSelectedAndUnrelatedSavedStates()
    {
        using var environment = new SavedStateTestEnvironment();
        var selected = environment.CreateState("Locked", [1, 2]);
        var unrelated = environment.CreateState("Unrelated", [3, 4]);
        using var lockStream = new FileStream(
            selected.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var result = environment.Library.Delete(selected);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(File.Exists(selected.Path));
        Assert.IsTrue(File.Exists(unrelated.Path));
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, File.ReadAllBytes(unrelated.Path));
    }

    [TestMethod]
    public async Task SettingsSaveDelegatesToCoordinatorRefreshesInventoryAndSelectsNewState()
    {
        using var environment = new SavedStateTestEnvironment();
        var coordinator = new StubWorkingStateCoordinator();
        var viewModel = environment.CreateViewModel(coordinator);
        viewModel.SavedStateName = "Flight review";

        Assert.IsTrue(viewModel.SaveStateCommand.CanExecute(null));
        await viewModel.SaveStateCommand.ExecuteAsync(null);

        Assert.AreEqual(1, coordinator.SaveCallCount);
        Assert.AreEqual(
            Path.Combine(environment.Library.DirectoryPath, "Flight review.cia"),
            coordinator.LastSavePath);
        Assert.HasCount(1, viewModel.SavedStates);
        Assert.AreEqual("Flight review", viewModel.SelectedSavedState?.Name);
        Assert.IsTrue(File.Exists(viewModel.SelectedSavedState?.Path));
        StringAssert.Contains(viewModel.SavedStateStatusText, "Saved state");
    }

    [TestMethod]
    public async Task FailedOrCancelledSaveAddsNothingAndPreservesExistingState()
    {
        using var environment = new SavedStateTestEnvironment();
        var existing = environment.CreateState("Existing", [1, 4, 9]);
        var original = File.ReadAllBytes(existing.Path);
        var coordinator = new StubWorkingStateCoordinator
        {
            SaveResult = WorkingStateCoordinatorResult.Reject(
                "Working-state save was cancelled before publication.")
        };
        var viewModel = environment.CreateViewModel(coordinator);
        viewModel.SavedStateName = "Cancelled";

        await viewModel.SaveStateCommand.ExecuteAsync(null);

        Assert.AreEqual(1, coordinator.SaveCallCount);
        Assert.HasCount(1, viewModel.SavedStates);
        Assert.AreEqual("Existing", viewModel.SavedStates[0].Name);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(existing.Path));
        Assert.IsFalse(File.Exists(Path.Combine(
            environment.Library.DirectoryPath,
            "Cancelled.cia")));
        StringAssert.Contains(viewModel.SavedStateStatusText, "cancelled");
    }

    [TestMethod]
    public void SaveCommandConsumesCoordinatorReadinessWithoutCallingSave()
    {
        using var environment = new SavedStateTestEnvironment();
        var coordinator = new StubWorkingStateCoordinator
        {
            SaveReadiness = WorkflowOperationReadiness.Unavailable(
                workflowPrerequisitesSatisfied: true,
                WorkflowRejectionCode.OperationFailed,
                "The published Database is out of date.")
        };
        var viewModel = environment.CreateViewModel(coordinator);
        viewModel.SavedStateName = "Stale Database";

        var canSave = viewModel.SaveStateCommand.CanExecute(null);

        Assert.IsFalse(canSave);
        Assert.AreEqual(0, coordinator.SaveCallCount);
    }

    [TestMethod]
    public async Task RestoreRequiresManagedSelectionAndDelegatesExistingPath()
    {
        using var environment = new SavedStateTestEnvironment();
        var coordinator = new StubWorkingStateCoordinator();
        var viewModel = environment.CreateViewModel(coordinator);

        Assert.IsFalse(viewModel.RestoreStateCommand.CanExecute(null));
        var state = environment.CreateState("Restore me", [7, 7]);
        viewModel.RefreshSavedStatesCommand.Execute(null);
        viewModel.SelectedSavedState = viewModel.SavedStates.Single();

        Assert.IsTrue(viewModel.RestoreStateCommand.CanExecute(null));
        await viewModel.RestoreStateCommand.ExecuteAsync(null);

        Assert.AreEqual(1, coordinator.RestoreCallCount);
        Assert.AreEqual(state.Path, coordinator.LastRestorePath);
        Assert.IsTrue(File.Exists(state.Path));
        StringAssert.Contains(viewModel.SavedStateStatusText, "Restored");
    }

    [TestMethod]
    public async Task DeleteRequiresConfirmationAndDeclinePreservesExactBytes()
    {
        using var environment = new SavedStateTestEnvironment();
        var state = environment.CreateState("Keep", [2, 4, 6, 8]);
        var original = File.ReadAllBytes(state.Path);
        var confirmation = new StubDeleteConfirmation(accepted: false);
        var viewModel = environment.CreateViewModel(
            new StubWorkingStateCoordinator(),
            confirmation);

        await viewModel.DeleteStateCommand.ExecuteAsync(null);

        Assert.AreEqual("Keep", confirmation.RequestedStateName);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(state.Path));
        Assert.HasCount(1, viewModel.SavedStates);
        StringAssert.Contains(viewModel.SavedStateStatusText, "cancelled");
    }

    [TestMethod]
    public async Task InApplicationConfirmationExposesNamedDecisionAndCompletesExplicitly()
    {
        var confirmation = new InApplicationSavedWorkingStateDeleteConfirmation();
        var changedCount = 0;
        confirmation.Changed += (_, _) => changedCount++;

        var declinedTask = confirmation.ConfirmAsync("Decline me");
        Assert.IsTrue(confirmation.IsOpen);
        Assert.AreEqual("Decline me", confirmation.StateName);
        confirmation.Decline();
        Assert.IsFalse(await declinedTask);
        Assert.IsFalse(confirmation.IsOpen);

        var acceptedTask = confirmation.ConfirmAsync("Delete me");
        confirmation.Accept();
        Assert.IsTrue(await acceptedTask);
        Assert.IsFalse(confirmation.IsOpen);
        Assert.AreEqual(4, changedCount);
    }

    [TestMethod]
    public async Task ConfirmedDeleteRemovesOnlySelectedManagedPackageAndRefreshesInventory()
    {
        using var environment = new SavedStateTestEnvironment();
        var first = environment.CreateState("First", [1]);
        var second = environment.CreateState("Second", [2]);
        var viewModel = environment.CreateViewModel(
            new StubWorkingStateCoordinator(),
            new StubDeleteConfirmation(accepted: true));
        viewModel.SelectedSavedState = viewModel.SavedStates.Single(state => state.Name == "First");

        await viewModel.DeleteStateCommand.ExecuteAsync(null);

        Assert.IsFalse(File.Exists(first.Path));
        Assert.IsTrue(File.Exists(second.Path));
        Assert.HasCount(1, viewModel.SavedStates);
        Assert.AreEqual("Second", viewModel.SelectedSavedState?.Name);
    }

    [TestMethod]
    public void EditingWorkingDirectorySettingDoesNotRelocateRuntimeSavedStateLibrary()
    {
        using var environment = new SavedStateTestEnvironment();
        var viewModel = environment.CreateViewModel(new StubWorkingStateCoordinator());
        var runtimeLibrary = viewModel.SavedStatesDirectory;
        var futureDirectory = Path.Combine(environment.Root, "future-working");

        viewModel.WorkingDirectory = futureDirectory;
        viewModel.SavedStateName = "Runtime location";
        var canSave = viewModel.SaveStateCommand.CanExecute(null);

        Assert.IsTrue(canSave);
        Assert.AreEqual(runtimeLibrary, viewModel.SavedStatesDirectory);
        Assert.AreEqual(
            Path.Combine(environment.Paths.WorkingDirectory, SavedWorkingStateLibrary.DirectoryName),
            viewModel.SavedStatesDirectory);
        Assert.IsFalse(viewModel.SavedStatesDirectory.StartsWith(
            futureDirectory,
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SettingsViewExposesManagedSavedStateWorkflowWithoutSpr95Actions()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "CIA.Desktop",
            "Views",
            "SettingsWorkspaceView.xaml"));

        StringAssert.Contains(xaml, "SavedStateNameInput");
        StringAssert.Contains(xaml, "SavedStatesList");
        StringAssert.Contains(xaml, "SaveStateCommand");
        StringAssert.Contains(xaml, "RestoreStateCommand");
        StringAssert.Contains(xaml, "DeleteStateCommand");
        StringAssert.Contains(xaml, "DeleteSavedStateConfirmation");
        Assert.IsFalse(xaml.Contains("Content=\"Relink", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("RelinkCommand", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("Browse for replacement", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkingStateManifest CreateManifest()
    {
        var snapshot = new WorkingStateSnapshot(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            databaseGeneration: null,
            sourceSets: [],
            activeSourceSetId: null,
            sources: [],
            new DiscoveryConfigurationSnapshot([]),
            databaseTagOverrides: []);
        return new WorkingStateManifest(
            WorkingStatePackageFormat.CurrentSchemaVersion,
            repositorySchemaVersion: 1,
            new string('0', 64),
            snapshot);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CIA.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private sealed class StubWorkingStateCoordinator : IWorkingStateCoordinator
    {
        public WorkflowOperationReadiness SaveReadiness { get; set; } =
            WorkflowOperationReadiness.Ready();

        public WorkingStateCoordinatorResult SaveResult { get; set; } =
            WorkingStateCoordinatorResult.Accept(CreateManifest());

        public WorkingStateCoordinatorResult RestoreResult { get; set; } =
            WorkingStateCoordinatorResult.Accept(CreateManifest());

        public int SaveCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public string? LastSavePath { get; private set; }

        public string? LastRestorePath { get; private set; }

        public WorkflowOperationReadiness EvaluateSaveReadiness() =>
            SaveReadiness;

        public WorkflowOperationReadiness EvaluateRestoreReadiness(string? packagePath) =>
            string.IsNullOrWhiteSpace(packagePath)
                ? WorkflowOperationReadiness.RequiresUserInput("Select a saved state.")
                : WorkflowOperationReadiness.Ready();

        public Task<WorkingStateCoordinatorResult> SaveAsync(
            string targetPath,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            LastSavePath = targetPath;
            if (SaveResult.Accepted)
            {
                File.WriteAllBytes(targetPath, [1, 2, 3, 4]);
            }

            return Task.FromResult(SaveResult);
        }

        public Task<WorkingStateCoordinatorResult> RestoreAsync(
            string packagePath,
            CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            LastRestorePath = packagePath;
            return Task.FromResult(RestoreResult);
        }
    }

    private sealed class StubDeleteConfirmation(bool accepted) :
        ISavedWorkingStateDeleteConfirmation
    {
        public bool IsOpen => false;

        public string? StateName => null;

        public string? RequestedStateName { get; private set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<bool> ConfirmAsync(
            string stateName,
            CancellationToken cancellationToken = default)
        {
            RequestedStateName = stateName;
            return Task.FromResult(accepted);
        }

        public void Accept()
        {
        }

        public void Decline()
        {
        }
    }

    private sealed class EmptyHistoryReader : IProcessingHistoryReader
    {
        public ProcessingHistorySnapshot Read() => new([], [], ReadProblem: null);
    }

    private sealed class SavedStateTestEnvironment : IDisposable
    {
        public SavedStateTestEnvironment()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CIA.SPR93.Desktop.Tests",
                Guid.NewGuid().ToString("N"));
            Paths = ApplicationPaths.FromLocalApplicationData(Root);
            Library = new SavedWorkingStateLibrary(Paths);
        }

        public string Root { get; }

        public ApplicationPaths Paths { get; }

        public SavedWorkingStateLibrary Library { get; }

        public SavedWorkingStateEntry CreateState(string name, byte[] bytes)
        {
            var target = Library.ResolveNewTarget(name);
            Assert.IsTrue(target.Succeeded, target.Problem);
            File.WriteAllBytes(target.Path!, bytes);
            return Library.CreateInventory().States.Single(state => state.Name == name);
        }

        public SettingsWorkspaceViewModel CreateViewModel(
            IWorkingStateCoordinator coordinator,
            ISavedWorkingStateDeleteConfirmation? confirmation = null) =>
            new(
                new EmptyHistoryReader(),
                new SettingsWorkspaceRuntimePaths(Paths, Paths.LogsDirectory),
                savedStateLibrary: Library,
                workingStateCoordinator: coordinator,
                deleteConfirmation: confirmation);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
