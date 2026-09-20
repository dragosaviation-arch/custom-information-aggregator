using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Extraction;
using CIA.Desktop.Export;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class ExtractionCoordinatorTests
{
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
                            "Results",
                            1,
                            dataset.RowCount,
                            snapshot.CreateIncludedOutputColumns(dataset.SourceSetId).Count)])]),
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

        var unavailable = await coordinator.ExportAsync(
            new WorkbookPublicationPlan(
                Path.GetFullPath("."),
                [new WorkbookPublicationTarget(
                    WorkbookDefinitionId.CreateNew(),
                    Path.Combine(Path.GetFullPath("."), "unavailable.xlsx"),
                    WorkbookPublicationDisposition.CreateNew)]),
            configuration);

        Assert.IsFalse(unavailable.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ExtractionNotCurrent, unavailable.Rejection?.Code);

        await context.BuildCurrentDatabaseAsync();
        Assert.IsTrue((await extraction.ExtractAsync()).Accepted);
        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);

        var stale = await coordinator.ExportAsync(
            new WorkbookPublicationPlan(
                Path.GetFullPath("."),
                [new WorkbookPublicationTarget(
                    WorkbookDefinitionId.CreateNew(),
                    Path.Combine(Path.GetFullPath("."), "stale.xlsx"),
                    WorkbookPublicationDisposition.CreateNew)]),
            configuration);

        Assert.IsFalse(stale.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.ExtractionNotCurrent, stale.Rejection?.Code);
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

        viewModel.OutputFolder = Path.Combine(directory.Path, "missing");
        Assert.IsFalse(viewModel.IsExportAvailable);
        viewModel.OutputFolder = directory.Path;
        Assert.IsTrue(viewModel.IsExportAvailable);

        viewModel.ExportColumns.Single().IsExported = false;
        Assert.IsFalse(viewModel.IsExportAvailable);
        viewModel.ExportColumns.Single().IsExported = true;
        Assert.IsTrue(viewModel.IsExportAvailable);

        viewModel.IsSelectedSetExportEnabled = false;
        Assert.IsFalse(viewModel.IsExportAvailable);
        viewModel.IsSelectedSetExportEnabled = true;
        Assert.IsTrue(viewModel.IsExportAvailable);

        viewModel.CreateExportWorkbookCommand.Execute(null);
        viewModel.WorkbookDefinitions[^1].FileName = viewModel.WorkbookDefinitions[0].FileName;
        Assert.IsFalse(viewModel.IsExportAvailable);
        Assert.AreEqual(0, exportClient.CallCount);
    }

    [TestMethod]
    public async Task BrowseChangesOnlyTheCurrentViewModelSessionFolder()
    {
        var context = await ExtractionContext.CreateAsync();
        using var directory = new TemporaryDirectory("CIA.SPR88.Desktop.Tests");
        var picker = new StaticExportFolderPicker(directory.Path);
        using var first = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            exportFolderPicker: picker);
        var originalDefault = first.OutputFolder;

        first.BrowseOutputFolderCommand.Execute(null);

        Assert.AreEqual(1, picker.CallCount);
        Assert.AreEqual(directory.Path, first.OutputFolder);
        using var nextSession = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow);
        Assert.AreEqual(originalDefault, nextSession.OutputFolder);
        Assert.AreNotEqual(first.OutputFolder, nextSession.OutputFolder);
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
        var existingPath = Path.Combine(
            directory.Path,
            viewModel.WorkbookDefinitions.Single().FileName);
        await File.WriteAllTextAsync(existingPath, "original");

        await viewModel.ExportToExcelCommand.ExecuteAsync(null);

        Assert.AreEqual(1, collisions.CallCount);
        Assert.AreEqual(0, exportClient.CallCount);
        Assert.AreEqual("original", await File.ReadAllTextAsync(existingPath));
        Assert.IsNull(context.Workflow.Current.ActiveOperation);
        StringAssert.Contains(viewModel.WorkbookExportStatusText, "cancelled");
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
                                worksheet.Worksheet.Name,
                                worksheet.Worksheet.Order,
                                dataset.RowCount,
                                configuration.CreateIncludedOutputColumns(dataset.SourceSetId).Count);
                        }).ToArray());
                }).ToArray()),
            FailureCode: null,
            FailureDescription: null);
    }

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

        public StaticExportFolderPicker(string? selectedFolder)
        {
            _selectedFolder = selectedFolder;
        }

        public int CallCount { get; private set; }

        public string? Browse(string? currentFolder)
        {
            CallCount++;
            return _selectedFolder;
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
            IProcessingHostSupervisor? supervisor = null)
        {
            var sources = new ActiveLoadedSourceSet();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var workflow = new ApplicationWorkflowCoordinator(
                supervisor ?? new TrackingProcessingHostSupervisor(),
                new RecordingHistory());
            var loading = new SourceLoadingCoordinator(
                new StaticSourceIntakeClient(source),
                sources,
                workflow);
            Assert.IsTrue((await loading.AddAsync(
                SourceSelectionKind.XmlFile,
                source.Path)).Accepted);
            var identity = new DiscoveryInformationIdentity(
                sources.SourceSets.Single().SourceSetId,
                "/root/tag",
                "tag");
            var configuration = new ActiveDiscoveryConfiguration();
            configuration.Synchronize([identity]);
            configuration.SetSelection([identity], isSelected: true);
            configuration.SetDatabaseTagOverride(identity, "Database Field");
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

    private sealed class StaticSourceIntakeClient(LoadedSourceContract source)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
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
}
