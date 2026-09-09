using System.Text.Json;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Core;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DatabaseWorkspaceViewModelTests
{
    [TestMethod]
    public void UnavailableDatabaseDoesNotProjectSelectedDiscoveryConfiguration()
    {
        var configuration = CreateConfiguration(["alpha", "beta"], ["alpha", "beta"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);

        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, viewModel.DatabaseStatus);
        Assert.IsEmpty(viewModel.Columns);
        Assert.IsEmpty(viewModel.VisibleColumns);
        Assert.IsEmpty(viewModel.Records);
        Assert.IsFalse(viewModel.IsExportAvailable);
    }

    [TestMethod]
    public async Task SuccessfulDatabaseBuildPublishesEffectiveConfigurationSnapshot()
    {
        var configuration = CreateConfiguration(
            ["toolnbr", "descr", "qty"],
            ["toolnbr", "descr", "qty"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);

        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        CollectionAssert.AreEqual(
            new[] { "descr", "qty", "toolnbr" },
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());

        Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(configuration.SetDatabaseTagOverride("descr", "Description"));
        Assert.AreEqual(1, configuration.SetSelection(["qty"], isSelected: false));

        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        CollectionAssert.AreEqual(
            new[] { "descr", "qty", "toolnbr" },
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());

        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        CollectionAssert.AreEqual(
            new[] { "Description", "toolnbr" },
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());
        CollectionAssert.AreEqual(
            new[] { "descr", "toolnbr" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        Assert.IsNull(typeof(DatabaseColumnPresentation).GetProperty("ValueContent"));
        Assert.IsNull(typeof(DatabaseColumnPresentation).GetProperty("DatabaseTagOverride"));
    }

    [TestMethod]
    public async Task SharedEffectiveNamePublishesOneColumnForAllStableSourceTags()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta", "gamma"],
            ["alpha", "beta", "gamma"]);
        Assert.IsTrue(configuration.SetDatabaseTagOverride("alpha", "Shared"));
        Assert.IsTrue(configuration.SetDatabaseTagOverride("beta", "Shared"));
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);

        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.HasCount(2, viewModel.Columns);
        var shared = viewModel.Columns.Single(column => column.DatabaseField == "Shared");
        CollectionAssert.AreEqual(
            new[] { "alpha", "beta" },
            shared.SourceInformationTypes.ToArray());
        Assert.AreEqual("alpha", shared.InformationType);
        var gamma = viewModel.Columns.Single(column => column.DatabaseField == "gamma");
        CollectionAssert.AreEqual(
            new[] { "gamma" },
            gamma.SourceInformationTypes.ToArray());
    }

    [TestMethod]
    public async Task StalePublishedColumnsDoNotMergeUntilAReplacementBuildSucceeds()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta"],
            ["alpha", "beta"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(configuration.SetDatabaseTagOverride("beta", "alpha"));

        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        Assert.HasCount(2, viewModel.Columns);
        CollectionAssert.AreEqual(
            new[] { "alpha", "beta" },
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());

        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        Assert.HasCount(1, viewModel.Columns);
        Assert.AreEqual("alpha", viewModel.Columns[0].DatabaseField);
        CollectionAssert.AreEqual(
            new[] { "alpha", "beta" },
            viewModel.Columns[0].SourceInformationTypes.ToArray());
    }

    [TestMethod]
    public async Task FailedAndCancelledReplacementKeepPriorPublishedSchema()
    {
        var configuration = CreateConfiguration(["alpha", "beta"], ["alpha"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.AreEqual(1, configuration.SetSelection(["beta"], isSelected: true));
        Assert.IsTrue(configuration.SetDatabaseTagOverride("alpha", "Alpha updated"));

        await CompleteWithOutcomeAsync(
            workflow,
            WorkflowOperationKind.DatabaseBuild,
            OperationOutcome.Failed);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        AssertPublishedColumns(viewModel, "alpha");

        await CompleteWithOutcomeAsync(
            workflow,
            WorkflowOperationKind.DatabaseBuild,
            OperationOutcome.Cancelled);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        AssertPublishedColumns(viewModel, "alpha");
    }

    [TestMethod]
    public async Task StaleGenerationPreservesPresentationAndResetUsesPublishedOrder()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta", "gamma"],
            ["alpha", "beta", "gamma"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        var alpha = viewModel.Columns.Single(column => column.InformationType == "alpha");
        var beta = viewModel.Columns.Single(column => column.InformationType == "beta");
        var gamma = viewModel.Columns.Single(column => column.InformationType == "gamma");

        alpha.IsVisible = false;
        alpha.Width = 240;
        alpha.IsExported = false;
        alpha.ExcelHeader = "Alpha heading";
        viewModel.MoveColumnUpCommand.Execute(gamma);
        viewModel.MoveColumnUpCommand.Execute(gamma);

        Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.AreEqual(1, configuration.SetSelection(["alpha"], isSelected: false));
        Assert.IsTrue(configuration.SetDatabaseTagOverride("beta", "Beta updated"));

        CollectionAssert.AreEqual(
            new[] { "gamma", "alpha", "beta" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        Assert.IsFalse(alpha.IsVisible);
        Assert.AreEqual(240, alpha.Width);
        Assert.IsFalse(alpha.IsExported);
        Assert.AreEqual("Alpha heading", alpha.ExcelHeader);
        Assert.AreEqual("beta", beta.DatabaseField);

        viewModel.ResetColumnLayoutCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "alpha", "beta", "gamma" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        Assert.IsTrue(viewModel.Columns.All(column => column.IsVisible));
        Assert.IsTrue(viewModel.Columns.All(
            column => column.Width == DatabaseColumnPresentation.DefaultWidth));
        Assert.IsFalse(alpha.IsExported);
        Assert.AreEqual("Alpha heading", alpha.ExcelHeader);
    }

    [TestMethod]
    public async Task ExportConfigurationOwnsCompanionsAndOrdersIndependentlyFromDatabase()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta", "gamma"],
            ["alpha", "beta", "gamma"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        var alpha = viewModel.ExportColumns.Single(
            column => column.InformationType == "alpha");
        var beta = viewModel.ExportColumns.Single(
            column => column.InformationType == "beta");
        var gamma = viewModel.ExportColumns.Single(
            column => column.InformationType == "gamma");

        Assert.IsTrue(viewModel.ExportColumns.All(column => column.IsExported));
        Assert.IsTrue(viewModel.ExportColumns.All(
            column => !column.IsSourceIdExported));

        alpha.ExcelHeader = "Alpha heading";
        alpha.IsSourceIdExported = true;
        beta.IsExported = false;
        beta.IsSourceIdExported = true;
        viewModel.MoveExportFieldUpCommand.Execute(gamma);
        viewModel.MoveExportFieldUpCommand.Execute(gamma);
        viewModel.MoveColumnDownCommand.Execute(
            viewModel.Columns.Single(column => column.InformationType == "alpha"));
        alpha.IsVisible = false;
        alpha.Width = 275;

        CollectionAssert.AreEqual(
            new[] { "beta", "alpha", "gamma" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
        CollectionAssert.AreEqual(
            new[] { "gamma", "alpha", "beta" },
            viewModel.ExportColumns.Select(column => column.InformationType).ToArray());
        Assert.IsFalse(beta.IsSourceIdExported);

        var snapshot = viewModel.CaptureExportConfiguration();
        var output = snapshot.CreateIncludedOutputColumns();
        CollectionAssert.AreEqual(
            new[] { "gamma", "Alpha heading", "Alpha heading SourceId" },
            output.Select(column => column.Header).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ExportOutputColumnKind.Value,
                ExportOutputColumnKind.Value,
                ExportOutputColumnKind.SourceId
            },
            output.Select(column => column.Kind).ToArray());
        Assert.AreEqual(
            output[1].OwningDatabaseFieldIdentity,
            output[2].OwningDatabaseFieldIdentity);
        Assert.AreEqual("alpha", output[1].OwningDatabaseFieldIdentity);
        Assert.IsTrue(output.All(column =>
            !string.IsNullOrWhiteSpace(column.OwningDatabaseFieldIdentity)));
        Assert.IsFalse(output.Any(column =>
            column.Kind == ExportOutputColumnKind.SourceId
            && output.All(candidate =>
                candidate.Kind != ExportOutputColumnKind.Value
                || candidate.OwningDatabaseFieldIdentity
                    != column.OwningDatabaseFieldIdentity)));
        Assert.IsFalse(alpha.IsVisible);
        Assert.AreEqual(275, alpha.Width);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Extraction);
        Assert.IsFalse(viewModel.IsExportAvailable);
    }

    [TestMethod]
    public async Task HeaderAndExportResetRestoreCanonicalRulesDeterministically()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta", "gamma"],
            ["alpha", "beta", "gamma"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        var alpha = viewModel.ExportColumns.Single(
            column => column.InformationType == "alpha");
        var beta = viewModel.ExportColumns.Single(
            column => column.InformationType == "beta");
        var gamma = viewModel.ExportColumns.Single(
            column => column.InformationType == "gamma");

        alpha.ExcelHeader = "Description";
        Assert.AreEqual("Description SourceId", alpha.SourceIdExportHeader);
        alpha.IsSourceIdExported = true;
        beta.IsExported = false;
        viewModel.MoveExportFieldUpCommand.Execute(gamma);
        viewModel.MoveExportFieldUpCommand.Execute(gamma);

        var firstJson = JsonSerializer.Serialize(viewModel.CaptureExportConfiguration());
        var secondJson = JsonSerializer.Serialize(viewModel.CaptureExportConfiguration());
        Assert.AreEqual(firstJson, secondJson);
        var roundTripped = JsonSerializer.Deserialize<ExportConfigurationSnapshot>(firstJson);
        Assert.IsNotNull(roundTripped);
        CollectionAssert.AreEqual(
            new[] { "gamma", "alpha", "beta" },
            roundTripped.Fields
                .Select(field => field.DatabaseFieldIdentity)
                .ToArray());
        Assert.AreEqual("Description SourceId", roundTripped.Fields[1].SourceIdCompanionHeader);

        viewModel.ResetExportCommand.Execute(null);

        CollectionAssert.AreEqual(
            new[] { "alpha", "beta", "gamma" },
            viewModel.ExportColumns.Select(column => column.InformationType).ToArray());
        Assert.IsTrue(viewModel.ExportColumns.All(column => column.IsExported));
        Assert.IsTrue(viewModel.ExportColumns.All(
            column => !column.IsSourceIdExported));
        Assert.AreEqual("Description", alpha.ExcelHeader);
        Assert.AreEqual("Description SourceId", alpha.SourceIdExportHeader);

        viewModel.ResetHeadersCommand.Execute(null);

        Assert.AreEqual("alpha", alpha.ExcelHeader);
        Assert.AreEqual("alpha SourceId", alpha.SourceIdExportHeader);
    }

    [TestMethod]
    public void ExportConfigurationRejectsACompanionWithoutItsOwningValueField()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ExportFieldConfiguration(
            "database-field",
            isValueIncluded: false,
            "Field",
            isSourceIdCompanionIncluded: true));
    }

    [TestMethod]
    public async Task NewGenerationRetainsCompatiblePresentationForSurvivingIdentity()
    {
        var configuration = CreateConfiguration(
            ["alpha", "beta", "delta", "gamma"],
            ["alpha", "beta", "gamma"]);
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        var alpha = viewModel.Columns.Single(column => column.InformationType == "alpha");
        var gamma = viewModel.Columns.Single(column => column.InformationType == "gamma");
        alpha.Width = 275;
        alpha.IsVisible = false;
        alpha.IsExported = false;
        alpha.ExcelHeader = "Retained heading";
        viewModel.MoveColumnUpCommand.Execute(gamma);
        viewModel.MoveColumnUpCommand.Execute(gamma);

        Assert.IsTrue(workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.AreEqual(1, configuration.SetSelection(["beta"], isSelected: false));
        Assert.AreEqual(1, configuration.SetSelection(["delta"], isSelected: true));
        Assert.IsTrue(configuration.SetDatabaseTagOverride("alpha", "Alpha updated"));
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.AreSame(alpha, viewModel.Columns.Single(
            column => column.InformationType == "alpha"));
        Assert.AreEqual("Alpha updated", alpha.DatabaseField);
        Assert.AreEqual(275, alpha.Width);
        Assert.IsFalse(alpha.IsVisible);
        Assert.IsFalse(alpha.IsExported);
        Assert.AreEqual("Retained heading", alpha.ExcelHeader);
        CollectionAssert.AreEqual(
            new[] { "gamma", "alpha", "delta" },
            viewModel.Columns.Select(column => column.InformationType).ToArray());
    }

    [TestMethod]
    public async Task WorkspaceNavigationDoesNotMutatePublishedSchema()
    {
        var configuration = CreateConfiguration(["alpha", "beta"], ["alpha", "beta"]);
        using var workflow = CreateWorkflowCoordinator();
        using var database = new DatabaseWorkspaceViewModel(configuration, workflow);
        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);
        var publishedColumns = database.Columns.ToArray();
        var workflowState = workflow.Current;
        var shell = new MainWindowViewModel(new ApplicationSession());

        foreach (var workspace in shell.Workspaces)
        {
            shell.SelectedWorkspace = workspace;
        }

        CollectionAssert.AreEqual(publishedColumns, database.Columns.ToArray());
        Assert.AreEqual(workflowState, workflow.Current);
    }

    [TestMethod]
    public async Task ExistingWorkflowDatabaseStateDrivesTruthfulHeaderState()
    {
        var configuration = new ActiveDiscoveryConfiguration();
        using var workflow = CreateWorkflowCoordinator();
        using var viewModel = new DatabaseWorkspaceViewModel(configuration, workflow);

        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, viewModel.DatabaseStatus);
        Assert.AreEqual("Database not available", viewModel.DatabaseStateText);

        await MakeDiscoveryCurrentAsync(workflow);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.DatabaseBuild);

        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        Assert.AreEqual("Database current", viewModel.DatabaseStateText);

        workflow.RecordDiscoveryConfigurationChanged();

        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        Assert.AreEqual("Database out of date", viewModel.DatabaseStateText);
        StringAssert.Contains(viewModel.DatabaseStateContext, "out of date");
    }

    private static ActiveDiscoveryConfiguration CreateConfiguration(
        IEnumerable<string> informationTypes,
        IEnumerable<string> selectedInformationTypes)
    {
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(informationTypes);
        configuration.SetSelection(selectedInformationTypes, isSelected: true);
        return configuration;
    }

    private static void AssertPublishedColumns(
        DatabaseWorkspaceViewModel viewModel,
        params string[] expectedDatabaseFields)
    {
        CollectionAssert.AreEqual(
            expectedDatabaseFields,
            viewModel.Columns.Select(column => column.DatabaseField).ToArray());
    }

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator()
    {
        return new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
    }

    private static async Task MakeDiscoveryCurrentAsync(
        IApplicationWorkflowCoordinator workflow)
    {
        Assert.IsTrue(workflow.RecordSourceSelectionChanged(true).Accepted);
        await CompleteSuccessfullyAsync(workflow, WorkflowOperationKind.Discovery);
    }

    private static Task CompleteSuccessfullyAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind)
    {
        return CompleteWithOutcomeAsync(
            workflow,
            operationKind,
            OperationOutcome.CompletedSuccessfully);
    }

    private static async Task CompleteWithOutcomeAsync(
        IApplicationWorkflowCoordinator workflow,
        WorkflowOperationKind operationKind,
        OperationOutcome outcome)
    {
        var begin = await workflow.BeginOperationAsync(operationKind);
        Assert.IsTrue(begin.Accepted);
        var completion = workflow.CompleteOperation(
            begin.Operation!.OperationId,
            outcome);
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
