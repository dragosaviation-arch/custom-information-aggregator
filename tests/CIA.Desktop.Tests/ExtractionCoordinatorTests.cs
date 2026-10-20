using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Extraction;
using CIA.Desktop.Export;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CIA.Desktop.WorkingState;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ExtractionCoordinatorTests
{
    [TestMethod]
    public async Task ExtractionReadinessUsesTheSameCurrentPublishedDatabaseGateAsTheCommand()
    {
        var context = await ExtractionContext.CreateAsync();
        var coordinator = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        var export = new WorkbookExportCoordinator(
            coordinator,
            context.Workflow,
            new RecordingWorkbookExportClient((_, _, _, _) =>
                throw new AssertFailedException("Extraction readiness must not export.")),
            NullLogger<WorkbookExportCoordinator>.Instance);
        var workingState = new WorkingStateCoordinator(
            context.Sources,
            context.Configuration,
            context.DatabaseCoordinator,
            context.Workflow,
            new RejectUnexpectedWorkingStateClient(),
            NullLogger<WorkingStateCoordinator>.Instance);
        using var readiness = new WorkflowOperationReadinessProvider(
            context.Workflow,
            context.DatabaseCoordinator,
            coordinator,
            export,
            workingState);
        var recoveryHistory = new RecoveryHistoryStore(WorkflowOperationKind.Extraction);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            recoveryHistory,
            recoveryHistory,
            context.Workflow,
            readiness);
        recovery.ReconcileStartupHistory();

        Assert.IsFalse(coordinator.EvaluateReadiness().NormalOperationReady);
        Assert.IsFalse(coordinator.CanExtract());
        Assert.IsFalse(recovery.Current?.NormalOperationReady);

        await context.BuildCurrentDatabaseAsync();

        Assert.IsTrue(coordinator.EvaluateReadiness().NormalOperationReady);
        Assert.IsTrue(coordinator.CanExtract());
        Assert.IsTrue(recovery.Current?.NormalOperationReady);
    }

    [TestMethod]
    public async Task SuccessfulExtractionUsesCapturedDatabaseAndBecomesCurrent()
    {
        var context = await ExtractionContext.CreateAsync();
        var database = await context.BuildCurrentDatabaseAsync(valueCount: 3);
        var client = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var coordinator = context.CreateExtractionCoordinator(client);

        var result = await coordinator.ExtractAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.AreEqual(database, client.DatabaseGeneration);
        Assert.AreEqual(database, coordinator.CurrentResult?.DatabaseGeneration);
        Assert.AreEqual(client.Correlation?.OperationId, coordinator.CurrentResult?.OperationId);
        Assert.AreEqual(
            OperationOutcome.CompletedSuccessfully,
            coordinator.CurrentCompletion?.Outcome);
        Assert.IsTrue(coordinator.CurrentResult?.IsHierarchyAware);
        Assert.HasCount(database.Datasets.Count, coordinator.CurrentResult!.Datasets);
        Assert.IsFalse(typeof(ExtractionResultSummary).GetProperties().Any(property =>
            property.Name.Contains("Visible", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Width", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task AcceptedResultWithDifferentTypedDatabaseSnapshotIsRejected()
    {
        var context = await ExtractionContext.CreateAsync();
        var database = await context.BuildCurrentDatabaseAsync();
        var basis = database.Datasets.Single();
        var mismatched = new DatabaseGenerationSummary(
            database.OperationId,
            [new DatabaseDatasetSummary(
                basis.SourceSetId,
                "Different Set name",
                basis.Ordinal,
                basis.RepeatedDataLayout,
                basis.RowCount,
                basis.ValueCount,
                basis.Columns,
                basis.Mappings)]);
        var client = new RecordingExtractionClient((correlation, _) =>
            Success(correlation, mismatched));
        var coordinator = context.CreateExtractionCoordinator(client);

        var result = await coordinator.ExtractAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationMismatch, result.Rejection?.Code);
        Assert.IsNull(coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task DatabaseWorkspacePrepareActionRequiresCurrentDatabaseAndBecomesReady()
    {
        var context = await ExtractionContext.CreateAsync();
        var client = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(client);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            databaseReviewClient: null,
            extraction);

        Assert.IsFalse(viewModel.PrepareForExportCommand.CanExecute(null));
        Assert.AreEqual(ExtractionReviewState.NotPrepared, viewModel.ExtractionReviewState);

        var database = await context.BuildCurrentDatabaseAsync(valueCount: 3);
        Assert.IsTrue(viewModel.PrepareForExportCommand.CanExecute(null));

        await viewModel.PrepareForExportCommand.ExecuteAsync(null);

        Assert.AreEqual(1, client.CallCount);
        Assert.AreEqual(ExtractionReviewState.Ready, viewModel.ExtractionReviewState);
        Assert.AreEqual("Ready", viewModel.ExtractionReviewStateText);
        Assert.IsTrue(viewModel.ExtractionBasisMatchesReviewedDatabase);
        Assert.AreEqual(database.OperationId, extraction.CurrentResult?.DatabaseGeneration.OperationId);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.IsFalse(viewModel.IsExportAvailable);
    }

    [TestMethod]
    public async Task DatabaseWorkspaceShowsPreparingBeforeAtomicPublication()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var response = new TaskCompletionSource<ExtractionClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingExtractionClient(async (_, _) => await response.Task);
        var extraction = context.CreateExtractionCoordinator(client);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            databaseReviewClient: null,
            extraction);

        var preparation = viewModel.PrepareForExportCommand.ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(ExtractionReviewState.Preparing, viewModel.ExtractionReviewState);
        Assert.IsFalse(viewModel.PrepareForExportCommand.CanExecute(null));

        response.SetResult(Success(client.Correlation!, client.DatabaseGeneration!));
        await preparation;
        Assert.AreEqual(ExtractionReviewState.Ready, viewModel.ExtractionReviewState);
    }

    [TestMethod]
    public async Task CompletedWithIssuesPublishesValidResultAndShowsRealItemCounts()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync(valueCount: 2);
        var client = new RecordingExtractionClient((correlation, basis) =>
            SuccessWithIssues(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(client);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            databaseReviewClient: null,
            extraction);

        await viewModel.PrepareForExportCommand.ExecuteAsync(null);

        Assert.AreEqual(
            ExtractionReviewState.ReadyWithIssues,
            viewModel.ExtractionReviewState);
        Assert.AreEqual("Ready with issues", viewModel.ExtractionReviewStateText);
        StringAssert.Contains(viewModel.ExtractionReviewContext, "1 completed");
        StringAssert.Contains(viewModel.ExtractionReviewContext, "1 failed");
        StringAssert.Contains(viewModel.ExtractionReviewContext, "1 unprocessed");
        Assert.AreEqual(
            OperationOutcome.CompletedWithIssues,
            extraction.CurrentCompletion?.Outcome);
        Assert.IsNotNull(extraction.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
    }

    [TestMethod]
    [DataRow(OperationOutcome.Failed, ExtractionReviewState.Failed)]
    [DataRow(OperationOutcome.Cancelled, ExtractionReviewState.Cancelled)]
    [DataRow(OperationOutcome.InterruptedIncomplete, ExtractionReviewState.Interrupted)]
    public async Task UnsuccessfulReplacementIsTruthfulAndPreservesPriorValidResult(
        OperationOutcome outcome,
        ExtractionReviewState expectedState)
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var attempts = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            outcome
        ]);
        var client = new RecordingExtractionClient((correlation, basis) =>
        {
            var attempt = attempts.Dequeue();
            return attempt == OperationOutcome.CompletedSuccessfully
                ? Success(correlation, basis)
                : Failure(correlation, attempt);
        });
        var extraction = context.CreateExtractionCoordinator(client);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            databaseReviewClient: null,
            extraction);

        await viewModel.PrepareForExportCommand.ExecuteAsync(null);
        var retained = extraction.CurrentResult;
        await viewModel.PrepareForExportCommand.ExecuteAsync(null);

        Assert.AreEqual(expectedState, viewModel.ExtractionReviewState);
        Assert.AreSame(retained, extraction.CurrentResult);
        StringAssert.Contains(viewModel.ExtractionReviewContext, "remains retained");
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.IsFalse(viewModel.IsExportAvailable);
    }

    [TestMethod]
    public async Task DatabaseChangeMakesPreparationOutOfDateWithoutAutomaticExtraction()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var client = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(client);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            databaseReviewClient: null,
            extraction);
        await viewModel.PrepareForExportCommand.ExecuteAsync(null);
        var retained = extraction.CurrentResult;

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);

        Assert.AreEqual(ExtractionReviewState.OutOfDate, viewModel.ExtractionReviewState);
        Assert.AreSame(retained, extraction.CurrentResult);
        Assert.AreEqual(1, client.CallCount);

        var replacementDatabase = await context.BuildCurrentDatabaseAsync(valueCount: 4);

        Assert.AreEqual(ExtractionReviewState.OutOfDate, viewModel.ExtractionReviewState);
        Assert.IsFalse(viewModel.ExtractionBasisMatchesReviewedDatabase);
        Assert.AreNotEqual(
            replacementDatabase.OperationId,
            retained?.DatabaseGeneration.OperationId);
        Assert.AreEqual(1, client.CallCount);
        Assert.IsTrue(viewModel.PrepareForExportCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task InFlightExtractionKeepsItsCapturedGenerationAndCorrelation()
    {
        var context = await ExtractionContext.CreateAsync();
        var database = await context.BuildCurrentDatabaseAsync(valueCount: 2);
        var response = new TaskCompletionSource<ExtractionClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingExtractionClient(async (_, _) => await response.Task);
        var coordinator = context.CreateExtractionCoordinator(client);

        var extractionTask = coordinator.ExtractAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(database, client.DatabaseGeneration);
        Assert.IsFalse((await context.DatabaseCoordinator.BuildAsync()).Accepted);
        Assert.AreEqual(database, client.DatabaseGeneration);

        response.SetResult(Success(client.Correlation!, database));
        Assert.IsTrue((await extractionTask).Accepted);
        Assert.AreEqual(database.OperationId, coordinator.CurrentResult?.DatabaseGeneration.OperationId);
    }

    [TestMethod]
    public async Task StaleDatabaseIsRejectedAndFailedReplacementsPreservePriorResult()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var results = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled
        ]);
        var client = new RecordingExtractionClient((correlation, basis) =>
        {
            var outcome = results.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? Success(correlation, basis)
                : Failure(correlation, outcome);
        });
        var coordinator = context.CreateExtractionCoordinator(client);
        Assert.IsTrue((await coordinator.ExtractAsync()).Accepted);
        var retained = coordinator.CurrentResult;

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        var staleRejection = await coordinator.ExtractAsync();
        Assert.IsFalse(staleRejection.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.DatabaseNotCurrent, staleRejection.Rejection?.Code);
        Assert.AreSame(retained, coordinator.CurrentResult);

        await context.BuildCurrentDatabaseAsync();
        Assert.IsFalse((await coordinator.ExtractAsync()).Accepted);
        Assert.AreSame(retained, coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Extraction);

        Assert.IsFalse((await coordinator.ExtractAsync()).Accepted);
        Assert.AreSame(retained, coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Extraction);
    }

    [TestMethod]
    public async Task EscCancellationTargetsOnlyTheActiveExtractionOperation()
    {
        var supervisor = new TrackingProcessingHostSupervisor();
        var context = await ExtractionContext.CreateAsync(supervisor);
        await context.BuildCurrentDatabaseAsync();
        var response = new TaskCompletionSource<ExtractionClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingExtractionClient(async (_, _) => await response.Task);
        var coordinator = context.CreateExtractionCoordinator(client);

        var extractionTask = coordinator.ExtractAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activeOperationId = context.Workflow.Current.ActiveOperation!.Correlation.OperationId;
        Assert.IsTrue((await context.Workflow.RequestCancellationAsync()).Accepted);
        Assert.AreEqual(activeOperationId, supervisor.CancelledOperationId);

        response.SetResult(Failure(client.Correlation!, OperationOutcome.Cancelled));
        Assert.IsFalse((await extractionTask).Accepted);
        Assert.IsNull(coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowOperationState.Cancelled, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task WorkbookExportPublishesValidatedHierarchyBatch()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync(valueCount: 2);
        var extractionClient = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(extractionClient);
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var extractionResult = extraction.CurrentResult!;
        var dataset = extractionResult.Datasets.Single();
        var workbookId = WorkbookDefinitionId.CreateNew();
        var worksheetId = WorksheetDefinitionId.CreateNew();
        var configuration = new ExportConfigurationSnapshot(
            [new WorkbookDefinition(
                workbookId,
                "captured-export.xlsx",
                1,
                [new WorksheetDefinition(
                    worksheetId,
                    workbookId,
                    dataset.SourceSetId,
                    "Results",
                    1)])],
            [new SourceSetExportConfiguration(
                dataset.SourceSetId,
                true,
                worksheetId,
                dataset.Columns.Select(column => new ExportFieldConfiguration(
                    column.Identity,
                    true,
                    column.EffectiveName,
                    false)).ToArray(),
                [])]);
        var outputDirectory = Path.GetFullPath(".");
        var exportClient = new RecordingWorkbookExportClient(
            (correlation, result, snapshot, publicationPlan) => new WorkbookExportClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully("workbook-batch-publication")]),
                new WorkbookExportBatchSummary(
                    correlation.OperationId,
                    result.OperationId,
                    publicationPlan.OutputDirectory,
                    [new WorkbookExportFileSummary(
                        workbookId,
                        publicationPlan.Targets.Single().FinalPath,
                        1,
                        [new WorkbookExportWorksheetSummary(
                            worksheetId,
                            dataset.SourceSetId,
                            dataset.RowFamilies.Single().Id,
                            "Results",
                            1,
                            dataset.RowCount,
                            snapshot.CreateIncludedOutputColumns(
                                dataset.RowFamilies.Single().Id).Count)])]),
                FailureCode: null,
                FailureDescription: null));
        var coordinator = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);

        var result = await coordinator.ExportAsync(
            CreatePublicationPlan(configuration, extractionResult, outputDirectory),
            configuration);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.IsNotNull(coordinator.LastBatch);
        Assert.AreEqual(extractionResult.OperationId, coordinator.LastBatch.ExtractionResultId);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
    }

    [TestMethod]
    public async Task FailedReplacementExportDoesNotReusePriorBatchForPostExportAction()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var callCount = 0;
        var exportClient = new RecordingWorkbookExportClient(
            (correlation, result, snapshot, publicationPlan) =>
            {
                callCount++;
                return callCount == 1
                    ? SuccessfulWorkbookExport(correlation, result, snapshot, publicationPlan)
                    : FailedWorkbookExport(correlation, "replacement-failed");
            });
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR89.Desktop.Tests");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = PostExportBehavior.OpenExportedFile
        }).Succeeded);
        var launcher = new RecordingPostExportLauncher();
        var postExport = new PostExportBehaviorCoordinator(
            settings,
            new StaticPostExportPrompt(PostExportChoice.StatusOnly),
            launcher);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null),
            settingsService: settings,
            postExportBehaviorCoordinator: postExport);
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);
        var firstBatch = export.LastBatch;
        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.IsNotNull(firstBatch);
        Assert.AreSame(firstBatch, export.LastBatch);
        CollectionAssert.AreEqual(
            firstBatch.Workbooks.Select(workbook => workbook.FinalPath).ToArray(),
            launcher.Paths.ToArray());
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "replacement-failed");
    }

    [TestMethod]
    public async Task ExportActionAutomaticallyPreparesOnceAndReusesCurrentExtraction()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extractionClient = new RecordingExtractionClient(
            (correlation, basis) => Success(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(extractionClient);
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR191.ExportFlow.Tests");
        var picker = new StaticExportFolderPicker(directory.Path);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: picker,
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        Assert.IsTrue(viewModel.ExportToExcelCommand.CanExecute(null));
        await viewModel.ExportToExcelCommand.ExecuteAsync(null);
        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, extractionClient.CallCount);
        Assert.AreEqual(2, exportClient.CallCount);
        Assert.AreEqual(0, picker.CallCount);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.AreEqual(
            context.DatabaseCoordinator.CurrentGeneration!.OperationId,
            extraction.CurrentResult!.DatabaseGeneration.OperationId);
    }

    [TestMethod]
    [DataRow(OperationOutcome.Failed)]
    [DataRow(OperationOutcome.Cancelled)]
    public async Task UnsuccessfulAutomaticPreparationBlocksWorkbookExport(
        OperationOutcome outcome)
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extractionClient = new RecordingExtractionClient(
            (correlation, _) => Failure(correlation, outcome));
        var extraction = context.CreateExtractionCoordinator(extractionClient);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("Failed preparation must not export."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR191.ExportFlow.Tests");
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, extractionClient.CallCount);
        Assert.AreEqual(0, exportClient.CallCount);
        Assert.IsNull(export.LastBatch);
        Assert.AreEqual(
            outcome == OperationOutcome.Cancelled
                ? WorkflowOperationState.Cancelled
                : WorkflowOperationState.Failed,
            context.Workflow.Current.LatestOperation?.State);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "No Extraction Result was published");
    }

    [TestMethod]
    public async Task CancelledExportRunsNoPostExportAction()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient(
            (correlation, _, _, _) => new WorkbookExportClientResult(
                false,
                OperationCompletion.FromTerminalOutcome(
                    correlation,
                    OperationOutcome.Cancelled,
                    [OperationItemStatus.Unprocessed(
                        "workbook-batch-publication",
                        "cancelled")]),
                Batch: null,
                FailureCode: "cancelled",
                FailureDescription: "Workbook export was cancelled."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR89.Desktop.Tests");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = PostExportBehavior.AskEachTime
        }).Succeeded);
        var launcher = new RecordingPostExportLauncher();
        var prompt = new StaticPostExportPrompt(PostExportChoice.OpenExportedFiles);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null),
            settingsService: settings,
            postExportBehaviorCoordinator: new PostExportBehaviorCoordinator(
                settings,
                prompt,
                launcher));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(0, prompt.CallCount);
        Assert.IsEmpty(launcher.Paths);
        Assert.IsNull(export.LastBatch);
        Assert.AreEqual(
            WorkflowOperationState.Cancelled,
            context.Workflow.Current.LatestOperation?.State);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "cancelled");
    }

    [TestMethod]
    public async Task OperationMismatchRunsNoPostExportAction()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient(
            (correlation, result, snapshot, publicationPlan) =>
            {
                var success = SuccessfulWorkbookExport(
                    correlation,
                    result,
                    snapshot,
                    publicationPlan);
                return success with
                {
                    Batch = new WorkbookExportBatchSummary(
                        OperationId.CreateNew(),
                        success.Batch!.ExtractionResultId,
                        success.Batch.OutputDirectory,
                        success.Batch.Workbooks)
                };
            });
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR89.Desktop.Tests");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = PostExportBehavior.OpenContainingFolder
        }).Succeeded);
        var launcher = new RecordingPostExportLauncher();
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null),
            settingsService: settings,
            postExportBehaviorCoordinator: new PostExportBehaviorCoordinator(
                settings,
                new StaticPostExportPrompt(PostExportChoice.StatusOnly),
                launcher));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.IsEmpty(launcher.Paths);
        Assert.IsNull(export.LastBatch);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "does not match");
    }

    [TestMethod]
    public async Task ShellFailureAfterSuccessfulExportDoesNotChangeWorkflowOutcomeOrBatch()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            new RecordingWorkbookExportClient(SuccessfulWorkbookExport),
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR89.Desktop.Tests");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = PostExportBehavior.OpenExportedFile
        }).Succeeded);
        var launcher = new RecordingPostExportLauncher
        {
            Failure = new InvalidOperationException("shell unavailable")
        };
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null),
            settingsService: settings,
            postExportBehaviorCoordinator: new PostExportBehaviorCoordinator(
                settings,
                new StaticPostExportPrompt(PostExportChoice.StatusOnly),
                launcher));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.IsNotNull(export.LastBatch);
        Assert.AreEqual(
            WorkflowOperationState.CompletedSuccessfully,
            context.Workflow.Current.LatestOperation?.State);
        CollectionAssert.AreEqual(
            export.LastBatch.Workbooks.Select(workbook => workbook.FinalPath).ToArray(),
            launcher.Paths.ToArray());
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "Export completed");
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "could not be opened");
    }

    [TestMethod]
    public async Task UnexpectedClientExceptionRejectsFirstExtractionAndRecordsFailure()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var coordinator = context.CreateExtractionCoordinator(new ThrowingExtractionClient());

        var result = await coordinator.ExtractAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationFailed, result.Rejection?.Code);
        Assert.AreEqual("Extraction failed unexpectedly.", result.Rejection?.Reason);
        Assert.IsNull(coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task UnexpectedReplacementExceptionRetainsPriorExtractionAndReturnsRejection()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var calls = 0;
        var client = new RecordingExtractionClient((correlation, basis) =>
        {
            calls++;
            return calls == 1
                ? Success(correlation, basis)
                : throw new InvalidOperationException("replacement extraction failure");
        });
        var coordinator = context.CreateExtractionCoordinator(client);
        Assert.IsTrue((await coordinator.ExtractAsync()).Accepted);
        var retained = coordinator.CurrentResult;

        var replacement = await coordinator.ExtractAsync();

        Assert.IsFalse(replacement.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationFailed, replacement.Rejection?.Code);
        Assert.AreSame(retained, coordinator.CurrentResult);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Extraction);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task WorkbookExportRejectsUnavailableOrStaleExtraction()
    {
        var context = await ExtractionContext.CreateAsync();
        var extractionClient = new RecordingExtractionClient((correlation, basis) =>
            Success(correlation, basis));
        var extraction = context.CreateExtractionCoordinator(extractionClient);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("A rejected export must not reach the client."));
        var coordinator = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        var configuration = new ExportConfigurationSnapshot([], []);

        var unavailableExecution = await coordinator.ExportWithBatchAsync(
            new WorkbookPublicationPlan(
                Path.GetFullPath("."),
                [new WorkbookPublicationTarget(
                    WorkbookDefinitionId.CreateNew(),
                    Path.Combine(Path.GetFullPath("."), "unavailable.xlsx"),
                    WorkbookPublicationDisposition.CreateNew)]),
            configuration);
        var unavailable = unavailableExecution.Command;

        Assert.IsFalse(unavailable.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ExtractionNotCurrent, unavailable.Rejection?.Code);
        Assert.IsNull(unavailableExecution.PublishedBatch);

        await context.BuildCurrentDatabaseAsync();
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);

        var staleExecution = await coordinator.ExportWithBatchAsync(
            new WorkbookPublicationPlan(
                Path.GetFullPath("."),
                [new WorkbookPublicationTarget(
                    WorkbookDefinitionId.CreateNew(),
                    Path.Combine(Path.GetFullPath("."), "stale.xlsx"),
                    WorkbookPublicationDisposition.CreateNew)]),
            configuration);
        var stale = staleExecution.Command;

        Assert.IsFalse(stale.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ExtractionNotCurrent, stale.Rejection?.Code);
        Assert.IsNull(staleExecution.PublishedBatch);
        Assert.AreEqual(0, exportClient.CallCount);
    }

    [TestMethod]
    public async Task ExportEnablementRequiresCurrentExtractionValidRoutingValueFieldAndFolder()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("Enablement checks must not invoke export."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        var collisionResolver = new RecordingCollisionResolver(_ =>
            throw new AssertFailedException("A readiness check must not resolve collisions."));
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(null),
            workbookCollisionResolver: collisionResolver);
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        var workingState = new WorkingStateCoordinator(
            context.Sources,
            context.Configuration,
            context.DatabaseCoordinator,
            context.Workflow,
            new RejectUnexpectedWorkingStateClient(),
            NullLogger<WorkingStateCoordinator>.Instance);
        using var readiness = new WorkflowOperationReadinessProvider(
            context.Workflow,
            context.DatabaseCoordinator,
            extraction,
            export,
            workingState);
        var recoveryHistory = new RecoveryHistoryStore(WorkflowOperationKind.Export);
        using var recovery = new InterruptedOperationRecoveryCoordinator(
            recoveryHistory,
            recoveryHistory,
            context.Workflow,
            readiness);
        recovery.ReconcileStartupHistory();

        viewModel.OutputFolder = Path.Combine(directory.Path, "missing");
        Assert.IsFalse(viewModel.IsExportAvailable);
        Assert.IsFalse(export.EvaluateReadiness().NormalOperationReady);
        Assert.IsFalse(recovery.Current?.NormalOperationReady);
        viewModel.OutputFolder = directory.Path;
        Assert.IsTrue(viewModel.IsExportAvailable);
        Assert.IsTrue(export.EvaluateReadiness().NormalOperationReady);
        Assert.IsTrue(recovery.Current?.NormalOperationReady);

        var conflictingPath = Path.Combine(
            directory.Path,
            viewModel.WorkbookDefinitions.Single().FileName);
        await File.WriteAllTextAsync(conflictingPath, "existing workbook placeholder");
        var conflictReadiness = export.EvaluateReadiness();
        Assert.IsFalse(conflictReadiness.NormalOperationReady);
        Assert.IsTrue(conflictReadiness.AdditionalUserInputRequired);
        Assert.IsFalse(recovery.Current?.NormalOperationReady);
        Assert.IsTrue(recovery.Current?.AdditionalUserInputRequired);
        Assert.IsTrue(viewModel.IsExportAvailable);
        File.Delete(conflictingPath);
        Assert.IsTrue(export.EvaluateReadiness().NormalOperationReady);

        viewModel.ColumnChoices.Single(choice => choice.IsGenerated).IsIncluded = false;
        Assert.IsFalse(viewModel.IsExportAvailable);
        Assert.IsFalse(export.EvaluateReadiness().NormalOperationReady);
        Assert.IsFalse(recovery.Current?.NormalOperationReady);
        viewModel.ColumnChoices.Single(choice => choice.IsGenerated).IsIncluded = true;
        Assert.IsTrue(viewModel.IsExportAvailable);
        Assert.IsTrue(export.EvaluateReadiness().NormalOperationReady);

        viewModel.IsSelectedSetExportEnabled = false;
        Assert.IsFalse(viewModel.IsExportAvailable);
        viewModel.IsSelectedSetExportEnabled = true;
        Assert.IsTrue(viewModel.IsExportAvailable);

        viewModel.CreateExportWorkbookCommand.Execute(null);
        viewModel.WorkbookDefinitions[^1].FileName = viewModel.WorkbookDefinitions[0].FileName;
        Assert.IsFalse(viewModel.IsExportAvailable);
        Assert.IsFalse(export.EvaluateReadiness().NormalOperationReady);
        Assert.AreEqual(0, exportClient.CallCount);
    }

    [TestMethod]
    public async Task BrowseChangesOnlyTheDestinationAndWritesNothing()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        var picker = new StaticExportFolderPicker(directory.Path);
        var collisions = new RecordingCollisionResolver(_ =>
            throw new AssertFailedException("Browsing must not resolve export conflicts."));
        using var first = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            exportFolderPicker: picker,
            workbookCollisionResolver: collisions);
        var originalDefault = first.OutputFolder;
        var target = Path.Combine(
            directory.Path,
            first.WorkbookDefinitions.Single().FileName);

        first.BrowseOutputFolderCommand.Execute(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(directory.Path, first.OutputFolder);
        Assert.AreEqual(0, collisions.CallCount);
        Assert.IsFalse(File.Exists(target));
        StringAssert.Contains(first.ExportDestinationTargets.Single(), target);
        using var nextSession = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow);
        Assert.AreEqual(originalDefault, nextSession.OutputFolder);
        Assert.AreNotEqual(first.OutputFolder, nextSession.OutputFolder);
    }

    [TestMethod]
    public async Task UnifiedColumnConfigurationUpdatesDatabaseAndExportAndResetsDefaults()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator);
        var generated = viewModel.ColumnChoices.Single(choice => choice.IsGenerated);
        var originalName = generated.OriginalName;

        generated.BeginRenameCommand.Execute(null);
        generated.EditName = "   ";
        generated.CommitRename();
        Assert.IsTrue(generated.IsEditing);
        Assert.IsNotNull(generated.ValidationText);
        Assert.AreEqual(originalName, generated.DisplayName);

        generated.CancelRename();
        generated.BeginRenameCommand.Execute(null);
        generated.EditName = "Owner header";
        generated.CommitRename();
        Assert.IsFalse(generated.IsEditing);
        Assert.AreEqual("Owner header", viewModel.VisibleColumns.Single().DisplayName);
        Assert.AreEqual(
            "Owner header",
            viewModel.CaptureExportConfiguration().SourceSets.Single().Fields.Single().ExcelHeader);

        generated = viewModel.ColumnChoices.Single(choice => choice.IsGenerated);
        generated.IsSourceIdIncluded = true;
        var metadata = viewModel.ColumnChoices.First(choice => !choice.IsGenerated);
        metadata.IsIncluded = true;
        var secondMetadata = viewModel.ColumnChoices.Where(choice => !choice.IsGenerated).Skip(1).First();
        secondMetadata.IsIncluded = true;
        viewModel.MoveColumnChoiceUpCommand.Execute(secondMetadata);
        metadata.BeginRenameCommand.Execute(null);
        metadata.EditName = "Source group";
        metadata.CommitRename();
        var snapshot = viewModel.CaptureExportConfiguration();
        Assert.IsTrue(snapshot.SourceSets.Single().Fields.Single().IsSourceIdCompanionIncluded);
        Assert.AreEqual(
            "Source group",
            snapshot.SourceSets.Single().MetadataFields.Single(field =>
                field.IsIncluded && field.MetadataField == metadata.MetadataColumn!.Field).ExcelHeader);
        CollectionAssert.AreEqual(
            new[] { secondMetadata.MetadataColumn!.Field, metadata.MetadataColumn!.Field },
            snapshot.SourceSets.Single().MetadataFields
                .Where(field => field.IsIncluded)
                .Select(field => field.MetadataField)
                .ToArray());

        viewModel.SelectedWorksheetName = "Reviewed worksheet";
        Assert.AreEqual("Reviewed worksheet", viewModel.SelectedDataset!.DisplayName);
        var workbookId = viewModel.SelectedExportWorkbook!.WorkbookDefinitionId;
        viewModel.BeginWorkbookRenameCommand.Execute(null);
        viewModel.WorkbookNameEditText = "invalid/name.xlsx";
        viewModel.CommitWorkbookRename();
        Assert.IsTrue(viewModel.IsWorkbookNameEditing);
        Assert.IsNotNull(viewModel.WorkbookRenameValidationText);
        viewModel.CancelWorkbookRename();
        viewModel.BeginWorkbookRenameCommand.Execute(null);
        viewModel.WorkbookNameEditText = "Owner workbook.xlsx";
        viewModel.CommitWorkbookRename();
        Assert.IsFalse(viewModel.IsWorkbookNameEditing);
        Assert.AreEqual("Owner workbook.xlsx", viewModel.SelectedExportWorkbook.FileName);
        Assert.AreEqual(workbookId, viewModel.SelectedExportWorkbook.WorkbookDefinitionId);

        viewModel.ResetColumnLayoutCommand.Execute(null);
        snapshot = viewModel.CaptureExportConfiguration();
        Assert.AreEqual(originalName, viewModel.VisibleColumns.Single().DisplayName);
        Assert.AreEqual(originalName, snapshot.SourceSets.Single().Fields.Single().ExcelHeader);
        Assert.IsFalse(snapshot.SourceSets.Single().Fields.Single().IsSourceIdCompanionIncluded);
        Assert.IsTrue(snapshot.SourceSets.Single().MetadataFields.All(field => !field.IsIncluded));
    }

    [TestMethod]
    public async Task AskForSingleWorkbookContinuesDirectlyIntoExport()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR220.ExportPlan.Tests");
        var selectedPath = Path.Combine(directory.Path, "Owner-selected.xlsx");
        var picker = new StaticExportFolderPicker(directory.Path, selectedPath);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: picker,
            workbookCollisionResolver: new RecordingCollisionResolver(_ =>
                throw new AssertFailedException("A new destination must not require collision input.")));
        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(1, picker.SingleWorkbookCallCount);
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.AreEqual(selectedPath, exportClient.PublicationPlan!.Targets.Single().FinalPath);
        Assert.AreEqual("Owner-selected.xlsx", viewModel.WorkbookDefinitions.Single().FileName);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "Published 1 workbook");
    }

    [TestMethod]
    public async Task AskForWorkbookBatchChoosesFolderAndContinuesDirectlyIntoExport()
    {
        var context = await ExtractionContext.CreateAsync(includeSecondSourceSet: true);
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR220.ExportBatch.Tests");
        var picker = new StaticExportFolderPicker(directory.Path);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: picker,
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null));
        viewModel.CreateExportWorkbookCommand.Execute(null);
        viewModel.SelectedDataset = viewModel.Datasets[1];
        Assert.AreNotEqual(
            viewModel.WorkbookDefinitions[1].WorkbookDefinitionId,
            viewModel.SelectedExportWorkbook!.WorkbookDefinitionId);

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(1, picker.FolderCallCount);
        Assert.AreEqual(0, picker.SingleWorkbookCallCount);
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.HasCount(2, exportClient.PublicationPlan!.Targets);
        Assert.IsTrue(exportClient.PublicationPlan.Targets.All(target =>
            string.Equals(
                Path.GetDirectoryName(target.FinalPath),
                directory.Path,
                StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CancellingSaveAsRetainsDestinationAndConfiguration()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("A cancelled chooser must not export."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        var picker = new StaticExportFolderPicker(null);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: picker,
            workbookCollisionResolver: new RecordingCollisionResolver(_ => null));
        var originalFolder = viewModel.OutputFolder;
        var originalName = viewModel.WorkbookDefinitions.Single().FileName;

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(1, picker.SingleWorkbookCallCount);
        Assert.AreEqual(0, exportClient.CallCount);
        Assert.AreEqual(originalFolder, viewModel.OutputFolder);
        Assert.AreEqual(originalName, viewModel.WorkbookDefinitions.Single().FileName);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "cancelled");
    }

    [TestMethod]
    public async Task OutputFolderAndOpenFilesPreferenceAreRememberedButAlwaysAskIsSessionOnly()
    {
        var context = await ExtractionContext.CreateAsync();
        using var root = new TemporaryDirectory("CIA.SPR96.Output.Tests");
        var localAppData = Path.Combine(root.Path, "LocalAppData");
        var output = Path.Combine(root.Path, "Exports");
        Directory.CreateDirectory(output);
        var firstSettings = new ApplicationSettingsService(
            new ApplicationSettingsStore(localAppData));
        using (var first = new DatabaseWorkspaceViewModel(
                   context.Configuration,
                   context.Workflow,
                   exportFolderPicker: new StaticExportFolderPicker(output),
                   settingsService: firstSettings))
        {
            Assert.IsFalse(first.OpenFilesAfterExport);
            first.AlwaysAskWhereToExport = false;
            first.OpenFilesAfterExport = true;
            first.BrowseOutputFolderCommand.Execute(null);
            Assert.AreEqual(output, first.OutputFolder);
        }

        var nextSettings = new ApplicationSettingsService(
            new ApplicationSettingsStore(localAppData));
        using var next = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            settingsService: nextSettings);

        Assert.AreEqual(output, next.OutputFolder);
        Assert.IsTrue(next.AlwaysAskWhereToExport);
        Assert.IsTrue(next.OpenFilesAfterExport);
    }

    [TestMethod]
    public async Task UnavailableRememberedOutputFolderUsesNormalSessionFallback()
    {
        var context = await ExtractionContext.CreateAsync();
        using var root = new TemporaryDirectory("CIA.SPR96.OutputUnavailable.Tests");
        var localAppData = Path.Combine(root.Path, "LocalAppData");
        var missingOutput = Path.Combine(root.Path, "NoLongerAvailable");
        var firstSettings = new ApplicationSettingsService(
            new ApplicationSettingsStore(localAppData));
        Assert.IsTrue(firstSettings.Save(firstSettings.Current with
        {
            LastUsedOutputDirectory = missingOutput
        }).Succeeded);
        var nextSettings = new ApplicationSettingsService(
            new ApplicationSettingsStore(localAppData));

        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            settingsService: nextSettings);

        Assert.AreNotEqual(missingOutput, viewModel.OutputFolder);
        StringAssert.Contains(viewModel.OutputFolder, "Exports");
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task CancellingCollisionResolutionStartsNoExportAndChangesNoFile()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("A cancelled collision must not reach the host."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        var collisions = new RecordingCollisionResolver(_ => null);
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        var settings = new ApplicationSettingsService(
            new ApplicationSettingsStore(directory.Path));
        Assert.IsTrue(settings.Save(settings.Current with
        {
            PostExportBehavior = PostExportBehavior.OpenExportedFile
        }).Succeeded);
        var launcher = new RecordingPostExportLauncher();
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: collisions,
            settingsService: settings,
            postExportBehaviorCoordinator: new PostExportBehaviorCoordinator(
                settings,
                new StaticPostExportPrompt(PostExportChoice.StatusOnly),
                launcher));
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;
        var existingPath = Path.Combine(
            directory.Path,
            viewModel.WorkbookDefinitions.Single().FileName);
        await File.WriteAllTextAsync(existingPath, "original");

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, collisions.CallCount);
        Assert.AreEqual(0, exportClient.CallCount);
        Assert.IsEmpty(launcher.Paths);
        Assert.AreEqual("original", await File.ReadAllTextAsync(existingPath));
        Assert.IsNull(context.Workflow.Current.ActiveOperation);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "cancelled");
    }

    [TestMethod]
    public async Task ConflictDiscoveredDuringExtractionIsResolvedOnceInTheSameExportAttempt()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        string? targetPath = null;
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) =>
            {
                File.WriteAllText(targetPath!, "new conflict");
                return Success(correlation, basis);
            }));
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        var collisions = new RecordingCollisionResolver(request =>
            new WorkbookCollisionResolution(
                request.Targets.Where(target => target.Exists)
                    .Select(target => new WorkbookCollisionDecision(
                        target.WorkbookDefinitionId,
                        WorkbookCollisionAction.Overwrite))
                    .ToArray()));
        using var directory = new TemporaryDirectory("CIA.SPR220.NewCollision.Tests");
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: collisions);
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;
        targetPath = Path.Combine(
            directory.Path,
            viewModel.WorkbookDefinitions.Single().FileName);

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, collisions.CallCount);
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.AreEqual(
            WorkbookPublicationDisposition.OverwriteExisting,
            exportClient.PublicationPlan!.Targets.Single().Disposition);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "Published 1 workbook");
    }

    [TestMethod]
    public async Task NewlyDiscoveredBatchConflictDoesNotRepeatUnchangedOverwriteConfirmation()
    {
        var context = await ExtractionContext.CreateAsync(includeSecondSourceSet: true);
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        using var directory = new TemporaryDirectory("CIA.SPR220.BatchCollision.Tests");
        var requestedIds = new List<WorkbookDefinitionId>();
        string? laterConflictPath = null;
        var collisions = new RecordingCollisionResolver(request =>
        {
            var target = request.Targets.Single();
            requestedIds.Add(target.WorkbookDefinitionId);
            if (requestedIds.Count == 1)
            {
                File.WriteAllText(laterConflictPath!, "later conflict");
            }

            return new WorkbookCollisionResolution(
                [new WorkbookCollisionDecision(
                    target.WorkbookDefinitionId,
                    WorkbookCollisionAction.Overwrite)]);
        });
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: collisions);
        viewModel.CreateExportWorkbookCommand.Execute(null);
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;
        var firstPath = Path.Combine(directory.Path, viewModel.WorkbookDefinitions[0].FileName);
        laterConflictPath = Path.Combine(
            directory.Path,
            viewModel.WorkbookDefinitions[1].FileName);
        File.WriteAllText(firstPath, "first conflict");

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(2, collisions.CallCount);
        Assert.AreEqual(2, requestedIds.Distinct().Count());
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.IsTrue(exportClient.PublicationPlan!.Targets.All(target =>
            target.Disposition == WorkbookPublicationDisposition.OverwriteExisting));
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task DifferentNameUpdatesSessionDefinitionAndPublishesResolvedPath()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient(SuccessfulWorkbookExport);
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        const string renamedFile = "Renamed session workbook.xlsx";
        var collisions = new RecordingCollisionResolver(request =>
            new WorkbookCollisionResolution(
                [new WorkbookCollisionDecision(
                    request.Targets.Single(target => target.Exists).WorkbookDefinitionId,
                    WorkbookCollisionAction.DifferentName,
                    renamedFile)]));
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        var picker = new StaticExportFolderPicker(directory.Path);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: picker,
            workbookCollisionResolver: collisions);
        viewModel.OutputFolder = directory.Path;
        var originalFileName = viewModel.WorkbookDefinitions.Single().FileName;
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, originalFileName),
            "original");

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(1, exportClient.CallCount);
        Assert.AreEqual(renamedFile, viewModel.WorkbookDefinitions.Single().FileName);
        Assert.AreEqual(
            WorkbookPublicationDisposition.CreateNew,
            exportClient.PublicationPlan!.Targets.Single().Disposition);
        Assert.AreEqual(
            Path.Combine(directory.Path, renamedFile),
            export.LastBatch!.Workbooks.Single().FinalPath);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, renamedFile);
        Assert.AreEqual("original", await File.ReadAllTextAsync(
            Path.Combine(directory.Path, originalFileName)));
    }

    [TestMethod]
    public async Task DifferentNameIsRevalidatedAgainstSessionWorkbookUniqueness()
    {
        var context = await ExtractionContext.CreateAsync();
        await context.BuildCurrentDatabaseAsync();
        var extraction = context.CreateExtractionCoordinator(
            new RecordingExtractionClient((correlation, basis) => Success(correlation, basis)));
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        var exportClient = new RecordingWorkbookExportClient((_, _, _, _) =>
            throw new AssertFailedException("An invalid rename must not reach the host."));
        var export = new WorkbookExportCoordinator(
            extraction,
            context.Workflow,
            exportClient,
            NullLogger<WorkbookExportCoordinator>.Instance);
        const string reservedFileName = "Reserved.xlsx";
        var collisions = new RecordingCollisionResolver(request =>
            new WorkbookCollisionResolution(
                [new WorkbookCollisionDecision(
                    request.Targets.Single(target => target.Exists).WorkbookDefinitionId,
                    WorkbookCollisionAction.DifferentName,
                    reservedFileName)]));
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            context.DatabaseCoordinator,
            extractionCoordinator: extraction,
            workbookExportCoordinator: export,
            exportFolderPicker: new StaticExportFolderPicker(directory.Path),
            workbookCollisionResolver: collisions);
        viewModel.OutputFolder = directory.Path;
        viewModel.AlwaysAskWhereToExport = false;
        viewModel.CreateExportWorkbookCommand.Execute(null);
        viewModel.WorkbookDefinitions[0].FileName = reservedFileName;
        var collidingPath = Path.Combine(
            directory.Path,
            viewModel.SelectedExportWorkbook!.FileName);
        await File.WriteAllTextAsync(collidingPath, "original");

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(0, exportClient.CallCount);
        Assert.AreEqual("original", await File.ReadAllTextAsync(collidingPath));
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "duplicated");
    }

    private static ExtractionClientResult Success(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration)
    {
        return new ExtractionClientResult(
            true,
            OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully("extraction-publication")]),
            CreateExtractionSummary(correlation.OperationId, databaseGeneration),
            FailureCode: null,
            FailureDescription: null);
    }

    private static ExtractionClientResult Failure(
        OperationCorrelation correlation,
        OperationOutcome outcome)
    {
        return new ExtractionClientResult(
            false,
            OperationCompletion.FromTerminalOutcome(
                correlation,
                outcome,
                [OperationItemStatus.Unprocessed("extraction-publication", "not-published")]),
            PublishedResult: null,
            "not-published",
            "No Extraction Result was published.");
    }

    private static ExtractionClientResult SuccessWithIssues(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration)
    {
        return new ExtractionClientResult(
            true,
            OperationCompletion.FromCompletedItems(
                correlation,
                [
                    OperationItemStatus.ProcessedSuccessfully("published-result"),
                    OperationItemStatus.Failed("failed-item", "item-failed"),
                    OperationItemStatus.Unprocessed("unprocessed-item", "not-processed")
                ]),
            CreateExtractionSummary(correlation.OperationId, databaseGeneration),
            FailureCode: null,
            FailureDescription: null);
    }

    private static ExtractionResultSummary CreateExtractionSummary(
        OperationId operationId,
        DatabaseGenerationSummary databaseGeneration) =>
        new(
            operationId,
            databaseGeneration,
            databaseGeneration.Datasets.Select(dataset => new ExtractionDatasetSummary(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.Ordinal,
                dataset.RepeatedDataLayout,
                dataset.RowCount,
                dataset.ValueCount,
                dataset.Columns)).ToArray());

    private static WorkbookPublicationPlan CreatePublicationPlan(
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extraction,
        string outputDirectory)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extraction);
        Assert.IsTrue(validation.IsValid);
        return new WorkbookPublicationPlan(
            outputDirectory,
            validation.RunnableWorkbooks.Select(workbook => new WorkbookPublicationTarget(
                workbook.Workbook.WorkbookDefinitionId,
                Path.Combine(outputDirectory, workbook.Workbook.FileName),
                WorkbookPublicationDisposition.CreateNew)).ToArray());
    }

    private static WorkbookExportClientResult SuccessfulWorkbookExport(
        OperationCorrelation correlation,
        ExtractionResultSummary extraction,
        ExportConfigurationSnapshot configuration,
        WorkbookPublicationPlan publicationPlan)
    {
        var validation = ExportConfigurationValidator.Validate(configuration, extraction);
        Assert.IsTrue(validation.IsValid);
        var targetsById = publicationPlan.Targets.ToDictionary(target =>
            target.WorkbookDefinitionId);
        return new WorkbookExportClientResult(
            true,
            OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully("workbook-batch-publication")]),
            new WorkbookExportBatchSummary(
                correlation.OperationId,
                extraction.OperationId,
                publicationPlan.OutputDirectory,
                validation.RunnableWorkbooks.Select(workbook =>
                {
                    var target = targetsById[workbook.Workbook.WorkbookDefinitionId];
                    return new WorkbookExportFileSummary(
                        workbook.Workbook.WorkbookDefinitionId,
                        target.FinalPath,
                        workbook.Workbook.Order,
                        workbook.Worksheets.Select(worksheet =>
                        {
                            var dataset = extraction.Datasets.Single(candidate =>
                                candidate.SourceSetId == worksheet.Worksheet.SourceSetId);
                            return new WorkbookExportWorksheetSummary(
                                worksheet.Worksheet.WorksheetDefinitionId,
                                worksheet.Worksheet.SourceSetId,
                                worksheet.Worksheet.RowFamilyId,
                                worksheet.Worksheet.Name,
                                worksheet.Worksheet.Order,
                                dataset.RowCount,
                                configuration.CreateIncludedOutputColumns(
                                    worksheet.Worksheet.RowFamilyId).Count);
                        }).ToArray());
                }).ToArray()),
            FailureCode: null,
            FailureDescription: null);
    }

    private static WorkbookExportClientResult FailedWorkbookExport(
        OperationCorrelation correlation,
        string description) =>
        new(
            false,
            OperationCompletion.FromTerminalOutcome(
                correlation,
                OperationOutcome.Failed,
                [OperationItemStatus.Failed("workbook-batch-publication", "export-failed")]),
            Batch: null,
            FailureCode: "export-failed",
            FailureDescription: description);

    private sealed class RecordingExtractionClient : IExtractionClient
    {
        private readonly Func<
            OperationCorrelation,
            DatabaseGenerationSummary,
            Task<ExtractionClientResult>> _extract;

        public RecordingExtractionClient(
            Func<OperationCorrelation, DatabaseGenerationSummary, ExtractionClientResult> extract)
            : this((correlation, database) => Task.FromResult(extract(correlation, database)))
        {
        }

        public RecordingExtractionClient(
            Func<OperationCorrelation, DatabaseGenerationSummary, Task<ExtractionClientResult>> extract)
        {
            _extract = extract;
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCorrelation? Correlation { get; private set; }

        public DatabaseGenerationSummary? DatabaseGeneration { get; private set; }

        public int CallCount { get; private set; }

        public Task<ExtractionClientResult> ExtractAsync(
            OperationCorrelation correlation,
            DatabaseGenerationSummary databaseGeneration,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Correlation = correlation;
            DatabaseGeneration = databaseGeneration;
            Started.TrySetResult();
            return _extract(correlation, databaseGeneration);
        }
    }

    private sealed class ThrowingExtractionClient : IExtractionClient
    {
        public Task<ExtractionClientResult> ExtractAsync(
            OperationCorrelation correlation,
            DatabaseGenerationSummary databaseGeneration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unexpected Extraction client failure");
    }

    private sealed class RecordingWorkbookExportClient : IWorkbookExportClient
    {
        private readonly Func<
            OperationCorrelation,
            ExtractionResultSummary,
            ExportConfigurationSnapshot,
            WorkbookPublicationPlan,
            WorkbookExportClientResult> _export;

        public RecordingWorkbookExportClient(
            Func<
                OperationCorrelation,
                ExtractionResultSummary,
                ExportConfigurationSnapshot,
                WorkbookPublicationPlan,
                WorkbookExportClientResult> export)
        {
            _export = export;
        }

        public ExtractionResultSummary? ExtractionResult { get; private set; }

        public ExportConfigurationSnapshot? Configuration { get; private set; }

        public WorkbookPublicationPlan? PublicationPlan { get; private set; }

        public int CallCount { get; private set; }

        public Task<WorkbookExportClientResult> ExportAsync(
            OperationCorrelation correlation,
            ExtractionResultSummary extractionResult,
            ExportConfigurationSnapshot configuration,
            WorkbookPublicationPlan publicationPlan,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExtractionResult = extractionResult;
            Configuration = configuration;
            PublicationPlan = publicationPlan;
            return Task.FromResult(_export(
                correlation,
                extractionResult,
                configuration,
                publicationPlan));
        }
    }

    private sealed class StaticExportFolderPicker : IExportFolderPicker
    {
        private readonly string? _selectedFolder;
        private readonly string? _selectedSingleWorkbook;

        public StaticExportFolderPicker(
            string? selectedFolder,
            string? selectedSingleWorkbook = null)
        {
            _selectedFolder = selectedFolder;
            _selectedSingleWorkbook = selectedSingleWorkbook;
        }

        public int CallCount { get; private set; }

        public int FolderCallCount { get; private set; }

        public int SingleWorkbookCallCount { get; private set; }

        public string? Browse(string? currentFolder)
        {
            CallCount++;
            FolderCallCount++;
            return _selectedFolder;
        }

        public string? BrowseSingleWorkbook(string? currentFolder, string suggestedFileName)
        {
            CallCount++;
            SingleWorkbookCallCount++;
            return _selectedSingleWorkbook
                ?? (string.IsNullOrWhiteSpace(_selectedFolder)
                    ? null
                    : Path.Combine(_selectedFolder, suggestedFileName));
        }
    }

    private sealed class RecordingCollisionResolver(
        Func<WorkbookCollisionResolutionRequest, WorkbookCollisionResolution?> resolve)
        : IWorkbookCollisionResolver
    {
        public int CallCount { get; private set; }

        public WorkbookCollisionResolution? Resolve(WorkbookCollisionResolutionRequest request)
        {
            CallCount++;
            return resolve(request);
        }
    }

    private sealed class StaticPostExportPrompt(PostExportChoice choice) : IPostExportPrompt
    {
        public int CallCount { get; private set; }

        public PostExportChoice Choose(WorkbookExportBatchSummary batch)
        {
            CallCount++;
            return choice;
        }
    }

    private sealed class RecordingPostExportLauncher : IPostExportLauncher
    {
        public List<string> Paths { get; } = [];

        public Exception? Failure { get; init; }

        public void Open(string path)
        {
            Paths.Add(path);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string rootName)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                rootName,
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

    private sealed class ExtractionContext
    {
        private ExtractionContext(
            ActiveDiscoveryConfiguration configuration,
            ActiveLoadedSourceSet sources,
            ApplicationWorkflowCoordinator workflow,
            DatabaseBuildCoordinator databaseCoordinator,
            ConfigurableDatabaseClient databaseClient)
        {
            Configuration = configuration;
            Sources = sources;
            Workflow = workflow;
            DatabaseCoordinator = databaseCoordinator;
            DatabaseClient = databaseClient;
        }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ActiveLoadedSourceSet Sources { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public DatabaseBuildCoordinator DatabaseCoordinator { get; }

        private ConfigurableDatabaseClient DatabaseClient { get; }

        public static async Task<ExtractionContext> CreateAsync(
            IProcessingHostSupervisor? supervisor = null,
            bool includeSecondSourceSet = false)
        {
            var sources = new ActiveLoadedSourceSet();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var secondSource = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source-2.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var workflow = new ApplicationWorkflowCoordinator(
                supervisor ?? new TrackingProcessingHostSupervisor(),
                new RecordingHistory());
            var loading = new SourceLoadingCoordinator(
                new StaticSourceIntakeClient(
                    includeSecondSourceSet ? [source, secondSource] : [source]),
                sources,
                workflow);
            Assert.IsTrue((await loading.AddAsync(
                SourceSelectionKind.XmlFile,
                source.Path)).Accepted);
            var firstSourceSetId = sources.SourceSets.Single().SourceSetId;
            SourceSetDefinition? secondSourceSet = null;
            if (includeSecondSourceSet)
            {
                Assert.IsTrue((await loading.AddAsync(
                    SourceSelectionKind.XmlFile,
                    secondSource.Path)).Accepted);
                secondSourceSet = loading.CreateSourceSet("Set 2").SourceSet;
                Assert.IsNotNull(secondSourceSet);
                Assert.IsTrue(loading.ReassignSources(
                    [sources.Items.Single(item => item.SourceId == secondSource.SourceId)],
                    secondSourceSet.SourceSetId).Accepted);
            }

            var identities = new List<DiscoveryInformationIdentity>
            {
                new(
                    firstSourceSetId,
                    "/root/tag",
                    "tag")
            };
            if (secondSourceSet is not null)
            {
                identities.Add(new DiscoveryInformationIdentity(
                    secondSourceSet.SourceSetId,
                    "/root/tag",
                    "tag"));
            }

            var configuration = new ActiveDiscoveryConfiguration();
            configuration.Synchronize(identities);
            configuration.SetSelection(identities, isSelected: true);
            foreach (var identity in identities)
            {
                configuration.SetDatabaseTagOverride(identity, "Database Field");
            }
            var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
            Assert.IsTrue(discovery.Accepted);
            Assert.IsTrue(workflow.CompleteOperation(
                discovery.Operation!.OperationId,
                OperationOutcome.CompletedSuccessfully).Accepted);

            var databaseClient = new ConfigurableDatabaseClient();
            var databaseCoordinator = new DatabaseBuildCoordinator(
                configuration,
                sources,
                workflow,
                databaseClient,
                NullLogger<DatabaseBuildCoordinator>.Instance);
            return new ExtractionContext(
                configuration,
                sources,
                workflow,
                databaseCoordinator,
                databaseClient);
        }

        public async Task<DatabaseGenerationSummary> BuildCurrentDatabaseAsync(int valueCount = 1)
        {
            DatabaseClient.ValueCount = valueCount;
            Assert.IsTrue((await DatabaseCoordinator.BuildAsync()).Accepted);
            var generation = DatabaseCoordinator.CurrentGeneration;
            Assert.IsNotNull(generation);
            return generation;
        }

        public ExtractionCoordinator CreateExtractionCoordinator(IExtractionClient client)
        {
            return new ExtractionCoordinator(
                DatabaseCoordinator,
                Workflow,
                client,
                NullLogger<ExtractionCoordinator>.Instance);
        }
    }

    private sealed class ConfigurableDatabaseClient : IDatabaseClient
    {
        public int ValueCount { get; set; } = 1;

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var datasets = specification.Datasets.Select(dataset => new DatabaseDatasetSummary(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.Ordinal,
                dataset.RepeatedDataLayout,
                rowCount: ValueCount,
                ValueCount,
                dataset.Fields.Select((field, index) => new DatabaseColumnDefinition(
                    new DatabaseColumnIdentity(
                        dataset.SourceSetId,
                        field.FieldKey,
                        DatabaseRepeatCoordinatePath.Empty),
                    field.EffectiveName,
                    index + 1)).ToArray(),
                dataset.Fields)).ToArray();
            return Task.FromResult(new DatabaseClientResult(
                true,
                OperationCompletion.FromCompletedItems(
                    correlation,
                    [OperationItemStatus.ProcessedSuccessfully("source")]),
                new DatabaseGenerationSummary(
                    correlation.OperationId,
                    datasets),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class StaticSourceIntakeClient(params LoadedSourceContract[] sources)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            var source = sources.Single(candidate => string.Equals(
                candidate.Path,
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new SourceIntakeClientResult(
                true,
                [source],
                FailureCode: null,
                FailureDescription: null));
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract refreshedSource,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceRefreshClientResult(
                true,
                refreshedSource,
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class TrackingProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public OperationId? CancelledOperationId { get; private set; }

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
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            CancelledOperationId = operationId;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }

    private sealed class RecoveryHistoryStore :
        IProcessingHistoryReader,
        IProcessingHistoryRecorder
    {
        private readonly ProcessingOperationStartRecord _start;
        private readonly List<ProcessingAttemptRecord> _attempts = [];
        private readonly List<ProcessingDiagnosticRecord> _diagnostics = [];

        public RecoveryHistoryStore(WorkflowOperationKind operationKind)
        {
            var correlation = OperationCorrelation.CreateNew(
                DateTimeOffset.UtcNow.AddMinutes(-2));
            _start = new ProcessingOperationStartRecord(
                correlation,
                operationKind.ToString(),
                correlation.InitiatedAtUtc);
        }

        public ProcessingHistorySnapshot Read() => new(
            _attempts.ToArray(),
            _diagnostics.ToArray(),
            [_start],
            ReadProblem: null)
        {
            RecoveryEvidenceComplete = true
        };

        public void RecordStart(ProcessingOperationStartRecord record)
        {
        }

        public void RecordAttempt(ProcessingAttemptRecord record) => _attempts.Add(record);

        public void RecordDiagnostic(ProcessingDiagnosticRecord record) =>
            _diagnostics.Add(record);
    }

    private sealed class RejectUnexpectedWorkingStateClient : IWorkingStateClient
    {
        public Task<WorkingStateClientResult> SaveAsync(
            OperationCorrelation correlation,
            string targetPath,
            WorkingStateSnapshot snapshot,
            WorkingStatePublicationMode publicationMode,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Readiness evaluation must not save working state.");

        public Task<WorkingStateClientResult> RestoreAsync(
            OperationCorrelation correlation,
            string packagePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Readiness evaluation must not restore working state.");
    }
}
