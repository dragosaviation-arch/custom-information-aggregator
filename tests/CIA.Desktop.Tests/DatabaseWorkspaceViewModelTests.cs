using CIA.Contracts.Operations;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DatabaseWorkspaceViewModelTests
{
    [TestMethod]
    public void DynamicColumnsConsumeSelectedEffectiveDatabaseTagsWithoutInterpretation()
    {
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(["tag_z", "descr", "tag_a"]);
        configuration.SetSelection(["tag_z", "descr", "tag_a"], isSelected: true);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);

        CollectionAssert.AreEqual(
            new[] { "descr", "tag_a", "tag_z" },
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());
        Assert.AreEqual(
            "descr",
            viewModel.Columns.Single(column => column.InformationType == "descr").DatabaseField);

        configuration.SetDatabaseTagOverride("descr", "Description");

        Assert.AreEqual(
            "Description",
            viewModel.Columns.Single(column => column.InformationType == "descr").DatabaseField);
        Assert.IsEmpty(viewModel.Records);
        Assert.IsFalse(viewModel.IsExportAvailable);
        Assert.IsNull(typeof(DatabaseColumnPresentation).GetProperty("ValueContent"));
        Assert.IsNull(typeof(DatabaseColumnPresentation).GetProperty("DatabaseTagOverride"));
    }

    [TestMethod]
    public void ColumnLayoutAndExcelConfigurationRemainIndependentSessionState()
    {
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(["alpha", "beta", "gamma"]);
        configuration.SetSelection(["alpha", "beta", "gamma"], isSelected: true);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        var alpha = viewModel.Columns.Single(column => column.InformationType == "alpha");
        var beta = viewModel.Columns.Single(column => column.InformationType == "beta");
        var gamma = viewModel.Columns.Single(column => column.InformationType == "gamma");
        var workflowState = workflow.Current;

        alpha.IsVisible = false;
        alpha.Width = 240;
        beta.IsExported = false;
        alpha.ExcelHeader = "Alpha heading";
        viewModel.MoveColumnUpCommand.Execute(gamma);

        Assert.IsFalse(alpha.IsVisible);
        Assert.IsTrue(alpha.IsExported);
        Assert.IsTrue(beta.IsVisible);
        Assert.IsFalse(beta.IsExported);
        Assert.AreEqual("alpha", alpha.DatabaseField);
        Assert.AreEqual("Alpha heading", alpha.ExcelHeader);
        Assert.IsTrue(alpha.HasExcelHeaderOverride);
        CollectionAssert.AreEqual(
            new[] { "alpha", "gamma", "beta" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            viewModel.Columns.Select(column => column.Position).ToArray());
        Assert.AreEqual(workflowState, workflow.Current);
        Assert.AreEqual(3, configuration.Current.SelectedCount);

        alpha.ExcelHeader = " ";
        Assert.AreEqual("alpha", alpha.ExcelHeader);
        Assert.IsFalse(alpha.HasExcelHeaderOverride);
        viewModel.ResetColumnLayoutCommand.Execute(null);
        viewModel.ResetHeadersCommand.Execute(null);
        viewModel.ResetExportCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "alpha", "beta", "gamma" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        Assert.IsTrue(viewModel.Columns.All(column => column.IsVisible));
        Assert.IsTrue(viewModel.Columns.All(column => column.IsExported));
        Assert.IsTrue(viewModel.Columns.All(
            column => column.Width == DatabaseColumnPresentation.DefaultWidth));
        Assert.IsTrue(viewModel.Columns.All(
            column => string.Equals(
                column.ExcelHeader,
                column.DatabaseField,
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExistingWorkflowDatabaseStateDrivesTruthfulHeaderState()
    {
        var configuration = new ActiveDiscoveryConfiguration();
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);

        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, viewModel.DatabaseStatus);
        Assert.AreEqual("Database not available", viewModel.DatabaseStateText);

        workflow.RecordSourceSelectionChanged(true);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.Discovery);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        Assert.AreEqual("Database current", viewModel.DatabaseStateText);

        workflow.RecordDiscoveryConfigurationChanged();

        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        Assert.AreEqual("Database out of date", viewModel.DatabaseStateText);
        StringAssert.Contains(viewModel.DatabaseStateContext, "out of date");
    }

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator()
    {
        return new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
    }

    private static async Task CompleteSuccessfullyAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind)
    {
        var begin = await workflow.BeginOperationAsync(operationKind);
        Assert.IsTrue(begin.Accepted);
        var completion = workflow.CompleteOperation(
            begin.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);
        Assert.IsTrue(completion.Accepted);
    }

    private sealed class ReadyProcessingHostSupervisor : IProcessingHostSupervisor
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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Current);
        }

        public Task<bool> RequestOperationCancellationAsync(
            CIA.Contracts.Operations.OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
