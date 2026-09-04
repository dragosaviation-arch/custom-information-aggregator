using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class SourceLoadingCoordinatorTests
{
    [TestMethod]
    public async Task LoadWorkspaceCommandUsesPickerAndSurfacesTheRealSourceSet()
    {
        var path = Path.GetFullPath("source.xml");
        var loadedSource = CreateXml(path);
        var client = new StubSourceIntakeClient(Accept(loadedSource));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var loadingCoordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
        var shell = new MainWindowViewModel(new ApplicationSession());
        var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            new StubSourceRemovalConfirmation(),
            loadingCoordinator,
            sourceSet,
            workflow,
            shell);

        await viewModel.AddXmlFileCommand.ExecuteAsync(null);

        Assert.HasCount(1, viewModel.Sources);
        Assert.IsTrue(viewModel.HasSources);
        Assert.AreEqual("Source loaded", viewModel.StatusTitle);
        Assert.AreSame(viewModel.Sources[0], sourceSet.Items[0]);
        Assert.AreEqual(loadedSource.SourceId, viewModel.Sources[0].SourceId);
        Assert.AreEqual(SourceLoadSettings.Default, client.LastSettings);
    }

    [TestMethod]
    public async Task FilteredBulkInclusionUsesVisibleRowsAndUpdatesCompactCount()
    {
        var alphaPath = Path.GetFullPath("alpha.xml");
        var betaPath = Path.GetFullPath("beta.xml");
        var client = new StubSourceIntakeClient(
            Accept(CreateXml(alphaPath), CreateXml(betaPath)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var shell = new MainWindowViewModel(new ApplicationSession());
        using var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(alphaPath),
            new StubSourceRemovalConfirmation(),
            new SourceLoadingCoordinator(client, sourceSet, workflow),
            sourceSet,
            workflow,
            shell);

        await viewModel.AddXmlFileCommand.ExecuteAsync(null);
        viewModel.FilterText = "alpha";
        viewModel.ExcludeVisibleCommand.Execute(null);

        Assert.IsFalse(sourceSet.Items.Single(source => source.Path == alphaPath).IsIncluded);
        Assert.IsTrue(sourceSet.Items.Single(source => source.Path == betaPath).IsIncluded);
        Assert.AreEqual("1 / 2 included", viewModel.IncludedSummary);
        Assert.IsTrue(workflow.Current.HasValidSourceSelection);

        viewModel.FilterText = string.Empty;
        viewModel.ExcludeVisibleCommand.Execute(null);

        Assert.IsTrue(sourceSet.Items.All(source => !source.IsIncluded));
        Assert.AreEqual("0 / 2 included", viewModel.IncludedSummary);
        Assert.IsFalse(workflow.Current.HasValidSourceSelection);
    }

    [TestMethod]
    public async Task FolderSettingsApplyOnlyToFolderIntake()
    {
        var path = Path.GetFullPath("selected-source");
        var folderClient = new StubSourceIntakeClient(Accept(CreateXml(Path.GetFullPath("folder.xml"))));
        var folderSources = new ActiveLoadedSourceSet();
        using var folderWorkflow = CreateWorkflowCoordinator();
        using var folderViewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            new StubSourceRemovalConfirmation(),
            new SourceLoadingCoordinator(folderClient, folderSources, folderWorkflow),
            folderSources,
            folderWorkflow,
            new MainWindowViewModel(new ApplicationSession()))
        {
            IncludeXmlFiles = false,
            IncludeArchives = true,
            SearchSubfolders = false
        };

        await folderViewModel.AddFolderCommand.ExecuteAsync(null);

        Assert.AreEqual(
            new SourceLoadSettings(false, true, false),
            folderClient.LastSettings);

        var archiveClient = new StubSourceIntakeClient(
            Accept(new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("archive.zip"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.Archive)));
        var archiveSources = new ActiveLoadedSourceSet();
        using var archiveWorkflow = CreateWorkflowCoordinator();
        using var archiveViewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            new StubSourceRemovalConfirmation(),
            new SourceLoadingCoordinator(archiveClient, archiveSources, archiveWorkflow),
            archiveSources,
            archiveWorkflow,
            new MainWindowViewModel(new ApplicationSession()))
        {
            IncludeXmlFiles = false,
            IncludeArchives = false,
            SearchSubfolders = false
        };

        await archiveViewModel.AddArchiveCommand.ExecuteAsync(null);

        Assert.AreEqual(SourceLoadSettings.Default, archiveClient.LastSettings);
    }

    [TestMethod]
    public async Task SuccessfulLoadCanNavigateToDiscoveryWithoutStartingIt()
    {
        var path = Path.GetFullPath("source.xml");
        var client = new StubSourceIntakeClient(Accept(CreateXml(path)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var shell = new MainWindowViewModel(new ApplicationSession());
        using var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            new StubSourceRemovalConfirmation(),
            new SourceLoadingCoordinator(client, sourceSet, workflow),
            sourceSet,
            workflow,
            shell)
        {
            OpenDiscoveryWhenLoadingCompletes = true
        };

        await viewModel.AddXmlFileCommand.ExecuteAsync(null);

        Assert.AreEqual(WorkspaceArea.Discovery, shell.SelectedWorkspace.Area);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    [TestMethod]
    public async Task AcceptedSourcePopulatesActiveSetAndWorkflowSelectionState()
    {
        var path = Path.GetFullPath("source.xml");
        var client = new StubSourceIntakeClient(
            Accept(new LoadedSourceContract(
                SourceId.CreateNew(),
                path,
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);

        var result = await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(1, result.AddedCount);
        Assert.HasCount(1, sourceSet.Items);
        Assert.AreEqual(path, sourceSet.Items[0].Path);
        Assert.IsTrue(sourceSet.Items[0].IsIncluded);
        Assert.AreEqual(LoadedSourceStatus.Ready, sourceSet.Items[0].Status);
        Assert.IsTrue(workflow.Current.HasValidSourceSelection);
    }

    [TestMethod]
    public async Task DuplicatePathIsBlockedBeforeASecondHostRequest()
    {
        var path = Path.GetFullPath("source.xml");
        var client = new StubSourceIntakeClient(
            Accept(new LoadedSourceContract(
                SourceId.CreateNew(),
                path,
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(
            client,
            sourceSet,
            workflow);

        var first = await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);
        var duplicate = await coordinator.AddAsync(
            SourceSelectionKind.XmlFile,
            path.ToUpperInvariant());

        Assert.IsTrue(first.Accepted);
        Assert.IsFalse(duplicate.Accepted);
        Assert.AreEqual("duplicate-path", duplicate.FailureCode);
        Assert.AreEqual(1, client.CallCount);
        Assert.HasCount(1, sourceSet.Items);
    }

    [TestMethod]
    public async Task FolderAddsNewSourcesAndSkipsAlreadyLoadedPaths()
    {
        var firstPath = Path.GetFullPath("first.xml");
        var secondPath = Path.GetFullPath("second.xml");
        var client = new SequencedSourceIntakeClient(
            Accept(CreateXml(firstPath)),
            Accept(CreateXml(firstPath), CreateXml(secondPath)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(
            client,
            sourceSet,
            workflow);

        await coordinator.AddAsync(SourceSelectionKind.XmlFile, firstPath);
        var folder = await coordinator.AddAsync(
            SourceSelectionKind.Folder,
            Path.GetFullPath("folder"));

        Assert.IsTrue(folder.Accepted);
        Assert.AreEqual(1, folder.AddedCount);
        Assert.AreEqual(1, folder.DuplicateCount);
        Assert.HasCount(2, sourceSet.Items);
    }

    [TestMethod]
    public async Task RejectedIntakeDoesNotChangeSourceSetOrWorkflowState()
    {
        var client = new StubSourceIntakeClient(
            new SourceIntakeClientResult(
                false,
                Array.Empty<LoadedSourceContract>(),
                "source-unreadable",
                "The selected source path could not be read."));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);

        var result = await coordinator.AddAsync(
            SourceSelectionKind.XmlFile,
            Path.GetFullPath("unreadable.xml"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("source-unreadable", result.FailureCode);
        Assert.HasCount(0, sourceSet.Items);
        Assert.IsFalse(workflow.Current.HasValidSourceSelection);
    }

    [TestMethod]
    public async Task ActiveWorkflowRejectsSourceChangeBeforeHostIntake()
    {
        var client = new StubSourceIntakeClient(Accept(CreateXml(Path.GetFullPath("source.xml"))));
        using var workflow = CreateWorkflowCoordinator();
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        Assert.IsTrue((await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery)).Accepted);
        var coordinator = new SourceLoadingCoordinator(
            client,
            new ActiveLoadedSourceSet(),
            workflow);

        var result = await coordinator.AddAsync(
            SourceSelectionKind.XmlFile,
            Path.GetFullPath("source.xml"));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("conflicting-operation", result.FailureCode);
        Assert.AreEqual(0, client.CallCount);
    }

    [TestMethod]
    public async Task RemoveTargetsCheckedEntriesRatherThanHighlightedRowsAndNeverDeletesFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("cia-spr62-remove-");

        try
        {
            var checkedPath = Path.Combine(tempDirectory.FullName, "checked.xml");
            var highlightedPath = Path.Combine(tempDirectory.FullName, "highlighted.xml");
            await File.WriteAllTextAsync(checkedPath, "<catalog />");
            await File.WriteAllTextAsync(highlightedPath, "<catalog />");
            var client = new StubSourceIntakeClient(
                Accept(CreateXml(checkedPath), CreateXml(highlightedPath)));
            var sourceSet = new ActiveLoadedSourceSet();
            using var workflow = CreateWorkflowCoordinator();
            var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
            var confirmation = new StubSourceRemovalConfirmation();
            using var viewModel = new LoadWorkspaceViewModel(
                new StubSourcePathPicker(tempDirectory.FullName),
                confirmation,
                coordinator,
                sourceSet,
                workflow,
                new MainWindowViewModel(new ApplicationSession()));

            await viewModel.AddFolderCommand.ExecuteAsync(null);
            var checkedSource = sourceSet.Items.Single(source => source.Path == checkedPath);
            var highlightedSource = sourceSet.Items.Single(source => source.Path == highlightedPath);
            coordinator.SetInclusion([highlightedSource], isIncluded: false);
            viewModel.SelectedSource = highlightedSource;
            viewModel.SetHighlightedSources([highlightedSource]);

            viewModel.RemoveCheckedCommand.Execute(null);

            Assert.AreEqual(1, confirmation.LastEntryCount);
            Assert.HasCount(1, sourceSet.Items);
            Assert.AreSame(highlightedSource, sourceSet.Items[0]);
            Assert.IsTrue(File.Exists(checkedPath));
            Assert.IsTrue(File.Exists(highlightedPath));
            Assert.IsFalse(workflow.Current.HasValidSourceSelection);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task RemovalWarningCanBeDeclinedOrSkippedForTheCurrentViewModelSession()
    {
        var path = Path.GetFullPath("source.xml");
        var client = new StubSourceIntakeClient(Accept(CreateXml(path)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var confirmation = new StubSourceRemovalConfirmation(result: false);
        using var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            confirmation,
            new SourceLoadingCoordinator(client, sourceSet, workflow),
            sourceSet,
            workflow,
            new MainWindowViewModel(new ApplicationSession()));
        await viewModel.AddXmlFileCommand.ExecuteAsync(null);

        viewModel.RemoveCheckedCommand.Execute(null);
        Assert.HasCount(1, sourceSet.Items);
        Assert.AreEqual(1, confirmation.CallCount);

        viewModel.DontWarnWhenRemovingEntries = true;
        viewModel.RemoveCheckedCommand.Execute(null);

        Assert.HasCount(0, sourceSet.Items);
        Assert.AreEqual(1, confirmation.CallCount);
        Assert.IsNull(viewModel.SelectedSource);
        Assert.AreEqual("0 / 0 included", viewModel.IncludedSummary);
    }

    [TestMethod]
    public async Task CombinedStatusAndSizeFilterScopesVisibleBulkInclusionOnly()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("cia-spr62-filter-");

        try
        {
            var smallPath = Path.Combine(tempDirectory.FullName, "small.xml");
            var largePath = Path.Combine(tempDirectory.FullName, "large.xml");
            var unavailablePath = Path.Combine(tempDirectory.FullName, "unavailable.xml");
            await File.WriteAllBytesAsync(smallPath, new byte[128]);
            await File.WriteAllBytesAsync(largePath, new byte[2 * 1024 * 1024]);
            var sourceSet = new ActiveLoadedSourceSet();
            using var workflow = CreateWorkflowCoordinator();
            var client = new StubSourceIntakeClient(
                Accept(
                    CreateXml(smallPath),
                    CreateXml(largePath),
                    new LoadedSourceContract(
                        SourceId.CreateNew(),
                        unavailablePath,
                        IsIncluded: true,
                        LoadedSourceStatus.Unavailable,
                        LoadedSourceKind.XmlFile)));
            using var viewModel = new LoadWorkspaceViewModel(
                new StubSourcePathPicker(tempDirectory.FullName),
                new StubSourceRemovalConfirmation(),
                new SourceLoadingCoordinator(client, sourceSet, workflow),
                sourceSet,
                workflow,
                new MainWindowViewModel(new ApplicationSession()));
            await viewModel.AddFolderCommand.ExecuteAsync(null);

            viewModel.FilterText = "large";
            viewModel.FilterStatus = "Ready";
            viewModel.MinimumSizeMb = "1";
            viewModel.ApplyFilterOptionsCommand.Execute(null);
            Assert.AreEqual(largePath, viewModel.VisibleSources.Cast<LoadedSourceItem>().Single().Path);

            viewModel.ExcludeVisibleCommand.Execute(null);

            Assert.IsFalse(sourceSet.Items.Single(source => source.Path == largePath).IsIncluded);
            Assert.IsTrue(sourceSet.Items.Single(source => source.Path == smallPath).IsIncluded);
            Assert.IsTrue(sourceSet.Items.Single(source => source.Path == unavailablePath).IsIncluded);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitRefreshUsesHighlightedRowsAndPreservesSourceIdentityInPlace()
    {
        var firstPath = Path.GetFullPath("first.xml");
        var secondPath = Path.GetFullPath("second.xml");
        var originalFirst = CreateXml(firstPath);
        var originalSecond = CreateXml(secondPath);
        var client = new StubSourceIntakeClient(Accept(originalFirst, originalSecond));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
        using var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(Path.GetFullPath("folder")),
            new StubSourceRemovalConfirmation(),
            coordinator,
            sourceSet,
            workflow,
            new MainWindowViewModel(new ApplicationSession()));
        await viewModel.AddFolderCommand.ExecuteAsync(null);
        var firstItem = sourceSet.Items[0];
        var secondItem = sourceSet.Items[1];
        coordinator.SetInclusion([firstItem], isIncluded: false);
        viewModel.SetHighlightedSources([firstItem]);

        await viewModel.RefreshSelectedCommand.ExecuteAsync(null);

        Assert.AreEqual(2, client.CallCount);
        Assert.AreSame(firstItem, sourceSet.Items[0]);
        Assert.AreEqual(originalFirst.SourceId, firstItem.SourceId);
        Assert.IsFalse(firstItem.IsIncluded);
        Assert.AreEqual(originalSecond.SourceId, secondItem.SourceId);
    }

    [TestMethod]
    public async Task RefreshFailureRetainsItemAndIdentityWithControlledUnavailableStatus()
    {
        var path = Path.GetFullPath("source.xml");
        var original = CreateXml(path);
        var client = new SequencedSourceIntakeClient(
            Accept(original),
            new SourceIntakeClientResult(
                false,
                Array.Empty<LoadedSourceContract>(),
                "source-unreadable",
                "The source is no longer readable."));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
        await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);
        var item = sourceSet.Items.Single();

        var result = await coordinator.RefreshAsync(item);

        Assert.IsFalse(result.Accepted);
        Assert.IsTrue(result.SourceUpdated);
        Assert.HasCount(1, sourceSet.Items);
        Assert.AreSame(item, sourceSet.Items[0]);
        Assert.AreEqual(original.SourceId, item.SourceId);
        Assert.AreEqual(LoadedSourceStatus.Unavailable, item.Status);
        Assert.AreEqual("The source is no longer readable.", item.StatusDetail);
        Assert.IsFalse(workflow.Current.HasValidSourceSelection);
    }

    [TestMethod]
    public async Task SuccessfulRefreshMakesCurrentDiscoveryResultStale()
    {
        var path = Path.GetFullPath("source.xml");
        var client = new StubSourceIntakeClient(Accept(CreateXml(path)));
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
        await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);
        var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);

        var result = await coordinator.RefreshAsync(sourceSet.Items.Single());

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
    }

    [TestMethod]
    public async Task RemovalMakesCurrentDiscoveryStaleWithoutStartingAnotherOperation()
    {
        var path = Path.GetFullPath("source.xml");
        var sourceSet = new ActiveLoadedSourceSet();
        using var workflow = CreateWorkflowCoordinator();
        var coordinator = new SourceLoadingCoordinator(
            new StubSourceIntakeClient(Accept(CreateXml(path))),
            sourceSet,
            workflow);
        await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);
        var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);

        var result = coordinator.Remove(sourceSet.Items);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.IsFalse(workflow.Current.HasValidSourceSelection);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    [TestMethod]
    public async Task SourceDoesNotRefreshUntilTheExplicitCommandIsInvoked()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("cia-spr62-manual-refresh-");

        try
        {
            var path = Path.Combine(tempDirectory.FullName, "source.xml");
            await File.WriteAllTextAsync(path, "<catalog />");
            var client = new StubSourceIntakeClient(Accept(CreateXml(path)));
            var sourceSet = new ActiveLoadedSourceSet();
            using var workflow = CreateWorkflowCoordinator();
            var coordinator = new SourceLoadingCoordinator(client, sourceSet, workflow);
            await coordinator.AddAsync(SourceSelectionKind.XmlFile, path);
            var item = sourceSet.Items.Single();
            var originalSize = item.SizeBytes;

            await File.AppendAllTextAsync(path, new string('x', 1024));

            Assert.AreEqual(1, client.CallCount);
            Assert.AreEqual(originalSize, item.SizeBytes);

            await coordinator.RefreshAsync(item);

            Assert.AreEqual(2, client.CallCount);
            Assert.IsGreaterThan(originalSize!.Value, item.SizeBytes!.Value);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static LoadedSourceContract CreateXml(string path)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
    }

    private static SourceIntakeClientResult Accept(params LoadedSourceContract[] sources)
    {
        return new SourceIntakeClientResult(true, sources, null, null);
    }

    private static SourceRefreshClientResult ToRefreshResult(
        SourceIntakeClientResult result,
        LoadedSourceContract source)
    {
        var returned = result.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase));
        var status = result.Accepted
            ? returned?.Status ?? LoadedSourceStatus.Ready
            : LoadedSourceStatus.Unavailable;
        return new SourceRefreshClientResult(
            result.Accepted,
            source with { Status = status },
            result.FailureCode,
            result.FailureDescription);
    }

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator()
    {
        return new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
    }

    private sealed class StubSourceIntakeClient(SourceIntakeClientResult result) : ISourceIntakeClient
    {
        public int CallCount { get; private set; }

        public SourceLoadSettings? LastSettings { get; private set; }

        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSettings = settings;
            return Task.FromResult(result);
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ToRefreshResult(result, source));
        }
    }

    private sealed class SequencedSourceIntakeClient(params SourceIntakeClientResult[] results)
        : ISourceIntakeClient
    {
        private int _index;

        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(results[_index++]);
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToRefreshResult(results[_index++], source));
        }
    }

    private sealed class StubSourcePathPicker(string path) : ISourcePathPicker
    {
        public string? PickXmlFile() => path;

        public string? PickFolder() => path;

        public string? PickArchive() => path;
    }

    private sealed class StubSourceRemovalConfirmation(bool result = true)
        : ISourceRemovalConfirmation
    {
        public int CallCount { get; private set; }

        public int? LastEntryCount { get; private set; }

        public bool Confirm(int entryCount)
        {
            CallCount++;
            LastEntryCount = entryCount;
            return result;
        }
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
