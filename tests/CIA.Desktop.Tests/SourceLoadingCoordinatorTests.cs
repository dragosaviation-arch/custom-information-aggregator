using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
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
        var viewModel = new LoadWorkspaceViewModel(
            new StubSourcePathPicker(path),
            loadingCoordinator,
            sourceSet);

        await viewModel.AddXmlFileCommand.ExecuteAsync(null);

        Assert.HasCount(1, viewModel.Sources);
        Assert.IsTrue(viewModel.HasSources);
        Assert.AreEqual("Source loaded", viewModel.StatusTitle);
        Assert.AreSame(viewModel.Sources[0], sourceSet.Items[0]);
        Assert.AreEqual(loadedSource.SourceId, viewModel.Sources[0].SourceId);
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

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator()
    {
        return new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
    }

    private sealed class StubSourceIntakeClient(SourceIntakeClientResult result) : ISourceIntakeClient
    {
        public int CallCount { get; private set; }

        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.AreEqual(SourceLoadSettings.Default, settings);
            return Task.FromResult(result);
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
    }

    private sealed class StubSourcePathPicker(string path) : ISourcePathPicker
    {
        public string? PickXmlFile() => path;

        public string? PickFolder() => path;

        public string? PickArchive() => path;
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
