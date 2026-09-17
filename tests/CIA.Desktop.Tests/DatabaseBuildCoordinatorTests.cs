using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DatabaseBuildCoordinatorTests
{
    [TestMethod]
    public async Task FirstSuccessfulBuildPublishesCapturedGenerationAndWorkspaceState()
    {
        var context = await BuildContext.CreateAsync();
        var client = new RecordingDatabaseClient((correlation, specification) =>
            HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 3));
        var coordinator = context.CreateCoordinator(client);
        using var database = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator);

        var result = await coordinator.BuildAsync();

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);
        Assert.AreEqual(client.Correlation?.OperationId, coordinator.CurrentGeneration?.OperationId);
        Assert.HasCount(1, database.Columns);
        Assert.AreEqual("DatabaseName", database.Columns[0].DatabaseField);
        Assert.AreEqual("1 rows · 3 values", database.RecordCountText);
    }

    [TestMethod]
    public async Task InFlightBuildUsesSnapshotAndIsNotAuthoritativeBeforeResponse()
    {
        var context = await BuildContext.CreateAsync();
        var response = new TaskCompletionSource<DatabaseClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseBuildSpecification? capturedSpecification = null;
        var client = new RecordingDatabaseClient(async (correlation, specification) =>
        {
            capturedSpecification = specification;
            return await response.Task;
        });
        var coordinator = context.CreateCoordinator(client);

        var buildTask = coordinator.BuildAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);

        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "ChangedLater"));
        response.SetResult(HierarchySuccess(
            client.Correlation!, capturedSpecification!, rowCount: 1, valueCount: 1));
        Assert.IsTrue((await buildTask).Accepted);

        Assert.AreEqual("DatabaseName", capturedSpecification!.Datasets.Single().Fields.Single().EffectiveName);
        Assert.AreEqual(
            "DatabaseName",
            coordinator.CurrentGeneration!.Datasets.Single().Mappings.Single().EffectiveName);
    }

    [TestMethod]
    public async Task FailedAndCancelledAttemptsNeverReplacePublishedGeneration()
    {
        var context = await BuildContext.CreateAsync();
        var outcomes = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled
        ]);
        var client = new RecordingDatabaseClient((correlation, specification) =>
        {
            var outcome = outcomes.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 1)
                : Failure(correlation, specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray(), outcome);
        });
        var coordinator = context.CreateCoordinator(client);
        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        var published = coordinator.CurrentGeneration;

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "Replacement"));
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreSame(published, coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);

        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreSame(published, coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task EscCancellationTargetsOnlyActiveDatabaseOperationCorrelation()
    {
        var supervisor = new TrackingProcessingHostSupervisor();
        var context = await BuildContext.CreateAsync(supervisor);
        var response = new TaskCompletionSource<DatabaseClientResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingDatabaseClient(async (_, _) => await response.Task);
        var coordinator = context.CreateCoordinator(client);

        var buildTask = coordinator.BuildAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = context.Workflow.Current.ActiveOperation;
        Assert.IsNotNull(active);

        var cancellation = await context.Workflow.RequestCancellationAsync();
        Assert.IsTrue(cancellation.Accepted);
        Assert.AreEqual(active.Correlation.OperationId, supervisor.CancelledOperationId);
        response.SetResult(Failure(
            active.Correlation,
            context.Sources.CreateIncludedReadySnapshot(),
            OperationOutcome.Cancelled));
        await buildTask;

        Assert.AreEqual(WorkflowOperationState.Cancelled, context.Workflow.Current.LatestOperation?.State);
        Assert.IsNull(coordinator.CurrentGeneration);
    }

    [TestMethod]
    public async Task FailedOrCancelledFirstBuildLeavesDatabaseUnavailable()
    {
        foreach (var outcome in new[] { OperationOutcome.Failed, OperationOutcome.Cancelled })
        {
            var context = await BuildContext.CreateAsync();
            var client = new RecordingDatabaseClient((correlation, specification) =>
                Failure(correlation, specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray(), outcome));
            var coordinator = context.CreateCoordinator(client);

            Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
            Assert.IsNull(coordinator.CurrentGeneration);
            Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);
        }
    }

    [TestMethod]
    public async Task PublishedReviewExposesHierarchyRowsAndPerValueProvenance()
    {
        var context = await BuildContext.CreateAsync(
            informationTypes: ["alpha", "beta", "gamma"]);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("alpha", "Combined"));
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("beta", "Combined"));
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("gamma", "Gamma override"));
        var databaseClient = new RecordingHierarchyDatabaseClient((correlation, specification) =>
            HierarchySuccess(correlation, specification, rowCount: 4, valueCount: 7));
        var firstSourceId = SourceId.CreateNew();
        var secondSourceId = SourceId.CreateNew();
        var reviewClient = new RecordingHierarchyDatabaseReviewClient(query =>
        {
            var generation = databaseClient.PublishedGeneration!;
            var dataset = generation.Datasets.Single();
            return AcceptedHierarchyReview(
                generation.OperationId,
                dataset,
                query,
                4,
                [
                    (firstSourceId, new[] { ("Combined", "A1"), ("Gamma override", "G1") }),
                    (firstSourceId, new[] { ("Combined", "A2") }),
                    (secondSourceId, new[] { ("Combined", "B1"), ("Gamma override", "G2") }),
                    (secondSourceId, new[] { ("Combined", "B2"), ("Gamma override", "G3") })
                ]);
        });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 4);

        CollectionAssert.AreEqual(
            new[] { "Combined", "Gamma override" },
            viewModel.VisibleColumns.Select(column => column.DatabaseField).ToArray());
        Assert.AreEqual("A1", viewModel.Records[0].Cells[0].DisplayValue);
        Assert.AreEqual("G1", viewModel.Records[0].Cells[1].DisplayValue);
        Assert.AreEqual("B2", viewModel.Records[3].Cells[0].DisplayValue);
        Assert.AreEqual("G3", viewModel.Records[3].Cells[1].DisplayValue);
        StringAssert.Contains(viewModel.Records[0].Cells[0].SourceContext!, "alpha");
        StringAssert.Contains(
            viewModel.Records[0].Cells[0].SourceContext!,
            firstSourceId.ToString());
        Assert.AreEqual("4 rows · 7 values", viewModel.RecordCountText);

        CollectionAssert.AreEquivalent(
            new[]
            {
                DatabaseMetadataField.SourceFile,
                DatabaseMetadataField.SourceKind,
                DatabaseMetadataField.RecordHierarchy
            },
            viewModel.VisibleMetadataFields.ToArray());
        var generationId = coordinator.CurrentGeneration!.OperationId;
        viewModel.MetadataVisibilityMode = DatabaseMetadataVisibilityMode.None;
        Assert.IsTrue(viewModel.Records.All(row => row.MetadataCells.Count == 0));
        viewModel.MetadataFields.Single(metadata =>
            metadata.Field == DatabaseMetadataField.SourceId).IsVisible = true;
        Assert.AreEqual(DatabaseMetadataVisibilityMode.Custom, viewModel.MetadataVisibilityMode);
        Assert.IsTrue(viewModel.Records.All(row =>
            row.MetadataCells.Single().Value == row.Source.SourceId.ToString()));
        viewModel.MetadataVisibilityMode = DatabaseMetadataVisibilityMode.All;
        Assert.HasCount(
            Enum.GetValues<DatabaseMetadataField>().Length,
            viewModel.Records[0].MetadataCells);
        Assert.AreEqual(generationId, coordinator.CurrentGeneration.OperationId);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);

        var combined = viewModel.Columns.Single(column => column.DatabaseField == "Combined");
        combined.Width = 280;
        viewModel.MoveColumnDownCommand.Execute(combined);
        viewModel.Columns.Single(column => column.DatabaseField == "Gamma override").IsVisible =
            false;
        Assert.HasCount(1, reviewClient.Requests);
        Assert.IsTrue(viewModel.Records.All(row => row.Cells.Count == 1));
        CollectionAssert.AreEqual(
            new[] { "A1", "A2", "B1", "B2" },
            viewModel.Records.Select(row => row.Cells[0].DisplayValue).ToArray());
    }

    [TestMethod]
    public async Task ReviewUsesBoundedPagesInsteadOfLoadingThePublishedDatabaseAtOnce()
    {
        var context = await BuildContext.CreateAsync();
        var databaseClient = new RecordingHierarchyDatabaseClient((correlation, specification) =>
            HierarchySuccess(correlation, specification, rowCount: 150, valueCount: 150));
        var sourceId = SourceId.CreateNew();
        var reviewClient = new RecordingHierarchyDatabaseReviewClient(query =>
        {
            var generation = databaseClient.PublishedGeneration!;
            var last = Math.Min(150, query.StartRowOrdinal + query.RowCount - 1);
            var rows = Enumerable.Range(query.StartRowOrdinal, last - query.StartRowOrdinal + 1)
                .Select(ordinal => (sourceId, new[] { ("DatabaseName", $"Value {ordinal}") }))
                .ToArray();
            return AcceptedHierarchyReview(
                generation.OperationId,
                generation.Datasets.Single(),
                query,
                150,
                rows);
        });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 100);
        Assert.AreEqual(DatabaseReviewLimits.MaximumRowsPerPage, reviewClient.Requests[0].RowCount);
        Assert.AreEqual(1, viewModel.Records[0].Ordinal);
        Assert.AreEqual("Page 1 of 2", viewModel.ReviewPageText);

        await viewModel.NextReviewPageCommand.ExecuteAsync(null);
        await WaitForAsync(() => viewModel.Records.FirstOrDefault()?.Ordinal == 101);
        Assert.HasCount(50, viewModel.Records);
        Assert.AreEqual("Value 150", viewModel.Records[^1].Cells[0].DisplayValue);
        Assert.AreEqual("Page 2 of 2", viewModel.ReviewPageText);
        Assert.HasCount(2, reviewClient.Requests);
    }

    [TestMethod]
    public async Task RowInclusionRequiresWorkflowAuthorizationBeforeHostMutation()
    {
        var context = await BuildContext.CreateAsync();
        var databaseClient = new RecordingHierarchyDatabaseClient((correlation, specification) =>
            HierarchySuccess(correlation, specification, rowCount: 2, valueCount: 2));
        var excluded = new HashSet<int>();
        var sourceId = SourceId.CreateNew();
        var reviewClient = new RecordingHierarchyDatabaseReviewClient(
            query =>
            {
                var generation = databaseClient.PublishedGeneration!;
                var result = AcceptedHierarchyReview(
                    generation.OperationId,
                    generation.Datasets.Single(),
                    query,
                    2,
                    [
                        (sourceId, new[] { ("DatabaseName", "first") }),
                        (sourceId, new[] { ("DatabaseName", "second") })
                    ]);
                var page = result.Page!;
                var rows = page.Rows.Select(row => new DatabaseReviewRow(
                    row.Ordinal,
                    !excluded.Contains(row.Ordinal),
                    row.RecordIdentity,
                    row.Source,
                    row.Cells)).ToArray();
                return result with
                {
                    Page = new DatabaseReviewPage(
                        page.GenerationId,
                        page.Dataset,
                        page.StartRowOrdinal,
                        page.RequestedRowCount,
                        page.TotalRowCount,
                        rows)
                };
            },
            change =>
            {
                foreach (var ordinal in change.RowOrdinals)
                {
                    if (change.IsIncluded)
                    {
                        excluded.Remove(ordinal);
                    }
                    else
                    {
                        excluded.Add(ordinal);
                    }
                }
                return new DatabaseRowInclusionClientResult(
                    true, change.RowOrdinals.Count, null, null);
            });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration, context.Workflow, coordinator, reviewClient);
        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 2);

        var extraction = await context.Workflow.BeginOperationAsync(WorkflowOperationKind.Extraction);
        Assert.IsTrue(extraction.Accepted);
        Assert.IsTrue(context.Workflow.CompleteOperation(
            extraction.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);
        Assert.IsTrue(viewModel.SetRowIncludedCommand.CanExecute(viewModel.Records[0]));

        await viewModel.SetRowIncludedCommand.ExecuteAsync(viewModel.Records[0]);
        await WaitForAsync(() => !viewModel.Records[0].IsIncluded);

        Assert.HasCount(1, reviewClient.InclusionChanges);
        Assert.IsTrue(viewModel.IncludeVisibleRowsCommand.CanExecute(null));
        await viewModel.IncludeVisibleRowsCommand.ExecuteAsync(null);
        await WaitForAsync(() => viewModel.Records.All(row => row.IsIncluded));
        Assert.HasCount(2, reviewClient.InclusionChanges);
        Assert.IsTrue(viewModel.ExcludeVisibleRowsCommand.CanExecute(null));
        await viewModel.ExcludeVisibleRowsCommand.ExecuteAsync(null);
        await WaitForAsync(() => viewModel.Records.All(row => !row.IsIncluded));
        Assert.HasCount(3, reviewClient.InclusionChanges);
        Assert.HasCount(2, reviewClient.InclusionChanges[^1].RowOrdinals);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Extraction);

        var active = await context.Workflow.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);
        Assert.IsTrue(active.Accepted);
        Assert.IsFalse(viewModel.SetRowIncludedCommand.CanExecute(viewModel.Records[0]));
        await viewModel.SetRowIncludedCommand.ExecuteAsync(viewModel.Records[0]);
        Assert.HasCount(3, reviewClient.InclusionChanges);
        Assert.IsTrue(context.Workflow.CompleteOperation(
            active.Operation!.OperationId,
            OperationOutcome.Failed).Accepted);

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);
        Assert.IsFalse(viewModel.SetRowIncludedCommand.CanExecute(viewModel.Records[0]));
        await viewModel.SetRowIncludedCommand.ExecuteAsync(viewModel.Records[0]);
        Assert.HasCount(3, reviewClient.InclusionChanges);
        Assert.HasCount(2, excluded);
    }

    [TestMethod]
    public async Task StaleFailedAndCancelledBuildsRetainReviewUntilSuccessfulReplacement()
    {
        var context = await BuildContext.CreateAsync();
        var outcomes = new Queue<OperationOutcome>(
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled,
            OperationOutcome.CompletedSuccessfully
        ]);
        var databaseClient = new RecordingHierarchyDatabaseClient((correlation, specification) =>
        {
            var outcome = outcomes.Dequeue();
            return outcome == OperationOutcome.CompletedSuccessfully
                ? HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 1)
                : Failure(
                    correlation,
                    specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray(),
                    outcome);
        });
        var sourceId = SourceId.CreateNew();
        OperationId? firstGenerationId = null;
        var reviewClient = new RecordingHierarchyDatabaseReviewClient(query =>
        {
            var generation = databaseClient.PublishedGeneration!;
            var replacement = firstGenerationId is not null
                && generation.OperationId != firstGenerationId;
            var databaseTagName = replacement ? "Replacement" : "DatabaseName";
            var value = replacement ? "replacement value" : "original value";
            return AcceptedHierarchyReview(
                generation.OperationId,
                generation.Datasets.Single(),
                query,
                1,
                [(sourceId, new[] { (databaseTagName, value) })]);
        });
        var coordinator = context.CreateCoordinator(databaseClient);
        using var viewModel = new DatabaseWorkspaceViewModel(
            context.Configuration,
            context.Workflow,
            coordinator,
            reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.Count == 1);
        firstGenerationId = coordinator.CurrentGeneration!.OperationId;
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "Replacement"));
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, viewModel.DatabaseStatus);
        Assert.IsFalse((await coordinator.BuildAsync()).Accepted);
        Assert.AreEqual("original value", viewModel.Records[0].Cells[0].DisplayValue);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() =>
            viewModel.Records.FirstOrDefault()?.Cells[0].DisplayValue == "replacement value");
        Assert.AreEqual(WorkflowArtifactStatus.Current, viewModel.DatabaseStatus);
        Assert.AreEqual("Replacement", viewModel.Columns.Single().DatabaseField);
    }

    [TestMethod]
    public async Task RehydratedTypedPublicationMatchesCapturedSpecificationByValue()
    {
        var context = await BuildContext.CreateAsync();
        var client = new RecordingDatabaseClient((correlation, specification) =>
        {
            var result = HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 1);
            var json = JsonSerializer.Serialize(result.PublishedGeneration);
            var rehydrated = JsonSerializer.Deserialize<DatabaseGenerationSummary>(json);
            return result with { PublishedGeneration = rehydrated };
        });
        var coordinator = context.CreateCoordinator(client);

        var result = await coordinator.BuildAsync();

        Assert.IsTrue(result.Accepted);
        Assert.IsTrue(coordinator.CurrentGeneration!.IsHierarchyAware);
        Assert.AreEqual(WorkflowArtifactStatus.Current, context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task AcceptedLegacyPublicationCannotSatisfyTypedBuild()
    {
        var context = await BuildContext.CreateAsync();
        var client = new RecordingDatabaseClient((correlation, _) =>
        {
            var completion = OperationCompletion.FromCompletedItems(
                correlation,
                [OperationItemStatus.ProcessedSuccessfully("source")]);
            var legacy = new DatabaseGenerationSummary(
                correlation.OperationId,
                new DatabaseMappingSnapshot([new DatabaseColumnMapping("DatabaseName", ["tag"])]),
                valueCount: 1);
            return new DatabaseClientResult(true, completion, legacy, null, null);
        });
        var coordinator = context.CreateCoordinator(client);

        var result = await coordinator.BuildAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationMismatch, result.Rejection?.Code);
        Assert.IsNull(coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task UnexpectedExceptionRejectsFirstBuildAfterRecordingFailedWorkflowState()
    {
        var context = await BuildContext.CreateAsync();
        var coordinator = context.CreateCoordinator(new ThrowingDatabaseClient());

        var result = await coordinator.BuildAsync();

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationFailed, result.Rejection?.Code);
        Assert.AreEqual("Database generation failed unexpectedly.", result.Rejection?.Reason);
        Assert.IsNull(coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task UnexpectedReplacementExceptionRetainsPriorGenerationAndReturnsRejection()
    {
        var context = await BuildContext.CreateAsync();
        var callCount = 0;
        var client = new RecordingDatabaseClient((correlation, specification) =>
        {
            callCount++;
            return callCount == 1
                ? HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 1)
                : throw new InvalidOperationException("replacement failure");
        });
        var coordinator = context.CreateCoordinator(client);
        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        var published = coordinator.CurrentGeneration;
        Assert.IsNotNull(published);

        Assert.IsTrue(context.Workflow.RecordDiscoveryConfigurationChanged().Accepted);
        Assert.IsTrue(context.Configuration.SetDatabaseTagOverride("tag", "Replacement"));
        var replacement = await coordinator.BuildAsync();

        Assert.IsFalse(replacement.Accepted);
        Assert.AreEqual(WorkflowRejectionCode.OperationFailed, replacement.Rejection?.Code);
        Assert.AreSame(published, coordinator.CurrentGeneration);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, context.Workflow.Current.Database);
        Assert.AreEqual(WorkflowOperationState.Failed, context.Workflow.Current.LatestOperation?.State);
    }

    [TestMethod]
    public async Task DiscoveryDatabaseActionSurfacesTypedPublicationFailure()
    {
        var context = await BuildContext.CreateAsync();
        var coordinator = context.CreateCoordinator(new ThrowingDatabaseClient());
        using var viewModel = new DiscoveryWorkspaceViewModel(
            new UnexpectedDiscoveryClient(),
            context.Configuration,
            context.Sources,
            context.Workflow,
            coordinator);

        Assert.IsTrue(viewModel.BuildDatabaseCommand.CanExecute(null));
        await viewModel.BuildDatabaseCommand.ExecuteAsync(null);

        Assert.AreEqual("Database creation failed", viewModel.StatusTitle);
        Assert.AreEqual("Database generation failed unexpectedly.", viewModel.StatusDetail);
        Assert.AreNotEqual("Database created", viewModel.StatusTitle);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, context.Workflow.Current.Database);
    }

    [TestMethod]
    public async Task DatasetSelectionKeepsSourceSetsAndPresentationStateIndependent()
    {
        var workflow = new ApplicationWorkflowCoordinator(
            new TrackingProcessingHostSupervisor(),
            new RecordingProcessingHistoryRecorder());
        var sources = new ActiveLoadedSourceSet();
        var firstSource = new LoadedSourceContract(
            SourceId.CreateNew(), Path.GetFullPath("first.xml"), true,
            LoadedSourceStatus.Ready, LoadedSourceKind.XmlFile);
        var secondSource = new LoadedSourceContract(
            SourceId.CreateNew(), Path.GetFullPath("second.xml"), true,
            LoadedSourceStatus.Ready, LoadedSourceKind.XmlFile);
        var loading = new SourceLoadingCoordinator(
            new QueuedSourceIntakeClient(firstSource, secondSource), sources, workflow);
        Assert.IsTrue((await loading.AddAsync(
            SourceSelectionKind.XmlFile, firstSource.Path)).Accepted);
        Assert.IsTrue((await loading.AddToNewSourceSetAsync(
            SourceSelectionKind.XmlFile, secondSource.Path, SourceLoadSettings.Default)).Accepted);
        var sets = sources.SourceSets.ToArray();
        var identities = sets.Select(set => new DiscoveryInformationIdentity(
            set.SourceSetId, "/records/record/code", "code")).ToArray();
        var configuration = new ActiveDiscoveryConfiguration();
        configuration.Synchronize(identities);
        configuration.SetSelection(identities, true);
        var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
        Assert.IsTrue(discovery.Accepted);
        Assert.IsTrue(workflow.CompleteOperation(
            discovery.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully).Accepted);

        var databaseClient = new RecordingHierarchyDatabaseClient((correlation, specification) =>
            HierarchySuccess(correlation, specification, rowCount: 1, valueCount: 1));
        var reviewClient = new RecordingHierarchyDatabaseReviewClient(query =>
        {
            var generation = databaseClient.PublishedGeneration!;
            var dataset = generation.Datasets.Single(candidate =>
                candidate.SourceSetId == query.SourceSetId);
            var source = query.SourceSetId == sets[0].SourceSetId ? firstSource : secondSource;
            var value = query.SourceSetId == sets[0].SourceSetId ? "first" : "second";
            return AcceptedHierarchyReview(
                generation.OperationId,
                dataset,
                query,
                1,
                [(source.SourceId, new[] { ("code", value) })]);
        });
        var coordinator = new DatabaseBuildCoordinator(
            configuration,
            sources,
            workflow,
            databaseClient,
            NullLogger<DatabaseBuildCoordinator>.Instance);
        using var viewModel = new DatabaseWorkspaceViewModel(
            configuration, workflow, coordinator, reviewClient);

        Assert.IsTrue((await coordinator.BuildAsync()).Accepted);
        await WaitForAsync(() => viewModel.Records.FirstOrDefault()?.Cells[0].DisplayValue == "first");
        Assert.HasCount(2, viewModel.Datasets);
        CollectionAssert.AreEqual(
            new[] { "Set 1", "Set 2" },
            viewModel.Datasets.Select(dataset => dataset.DisplayName).ToArray());
        var firstColumn = viewModel.Columns.Single();
        firstColumn.Width = 260;
        firstColumn.IsVisible = false;

        viewModel.SelectedDataset = viewModel.Datasets[1];
        await WaitForAsync(() => viewModel.Records.FirstOrDefault()?.Cells[0].DisplayValue == "second");
        var secondColumn = viewModel.Columns.Single();
        Assert.IsTrue(secondColumn.IsVisible);
        Assert.AreEqual(DatabaseColumnPresentation.DefaultWidth, secondColumn.Width);
        secondColumn.Width = 210;

        viewModel.SelectedDataset = viewModel.Datasets[0];
        await WaitForAsync(() => reviewClient.Requests[^1].GenerationId
            == coordinator.CurrentGeneration!.OperationId
            && reviewClient.Requests.Count >= 3);
        Assert.IsFalse(viewModel.Columns.Single().IsVisible);
        Assert.AreEqual(260, viewModel.Columns.Single().Width);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Database);
        CollectionAssert.AreEqual(
            new[] { sets[0].SourceSetId, sets[1].SourceSetId, sets[0].SourceSetId },
            reviewClient.SourceSetRequests.TakeLast(3).ToArray());
    }

    private static DatabaseReviewClientResult AcceptedHierarchyReview(
        OperationId generationId,
        DatabaseDatasetSummary dataset,
        DatabaseReviewQuery query,
        int totalRowCount,
        IReadOnlyList<(SourceId SourceId, (string ColumnName, string Value)[] Values)> rowValues,
        bool isIncluded = true)
    {
        var rows = rowValues.Select((row, index) =>
        {
            var cells = row.Values.Select(value =>
            {
                var column = dataset.Columns.Single(candidate => string.Equals(
                    candidate.EffectiveName,
                    value.ColumnName,
                    StringComparison.Ordinal));
                var detailedIdentity = dataset.Mappings
                    .First(mapping => string.Equals(
                        mapping.FieldKey,
                        column.Identity.FieldKey,
                        StringComparison.Ordinal))
                    .DetailedIdentities[0];
                var lineage = new DatabaseLineageEvidence(
                    detailedIdentity.StructuralPath,
                    index + 1,
                    null,
                    [],
                    index + 1,
                    [new DatabaseSourceElementEvidence(
                        detailedIdentity.InformationType,
                        string.Empty,
                        detailedIdentity.InformationType,
                        index + 1,
                        1)]);
                return new DatabaseReviewCell(
                    column.Identity,
                    false,
                    [new DatabaseReviewValue(
                        value.Value,
                        detailedIdentity,
                        row.SourceId,
                        lineage,
                        column.Identity.RepeatCoordinates)]);
            }).ToArray();
            return new DatabaseReviewRow(
                query.StartRowOrdinal + index,
                isIncluded,
                $"record-{query.StartRowOrdinal + index}",
                new DatabaseSourceMetadata(
                    dataset.SourceSetId,
                    dataset.DisplayName,
                    row.SourceId,
                    "source.xml",
                    Path.GetFullPath("source.xml"),
                    LoadedSourceKind.XmlFile,
                    null,
                    null,
                    null),
                cells);
        }).ToArray();
        return new DatabaseReviewClientResult(
            true,
            new DatabaseReviewPage(
                generationId,
                dataset,
                query.StartRowOrdinal,
                query.RowCount,
                totalRowCount,
                rows),
            FailureCode: null,
            FailureDescription: null);
    }

    private static DatabaseClientResult HierarchySuccess(
        OperationCorrelation correlation,
        DatabaseBuildSpecification specification,
        int rowCount,
        int valueCount)
    {
        var datasets = specification.Datasets.Select(dataset =>
        {
            var columns = dataset.Fields
                .GroupBy(field => field.FieldKey, StringComparer.Ordinal)
                .Select((group, index) => new DatabaseColumnDefinition(
                    new DatabaseColumnIdentity(
                        dataset.SourceSetId,
                        group.Key,
                        DatabaseRepeatCoordinatePath.Empty),
                    group.First().EffectiveName,
                    index + 1))
                .ToArray();
            return new DatabaseDatasetSummary(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.Ordinal,
                dataset.RepeatedDataLayout,
                rowCount,
                valueCount,
                columns,
                dataset.Fields);
        }).ToArray();
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            specification.Datasets.SelectMany(dataset => dataset.Sources)
                .Select(source => OperationItemStatus.ProcessedSuccessfully(
                    source.SourceId.ToString())).ToArray());
        return new DatabaseClientResult(
            true,
            completion,
            new DatabaseGenerationSummary(correlation.OperationId, datasets),
            FailureCode: null,
            FailureDescription: null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static DatabaseClientResult Failure(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        OperationOutcome outcome)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            outcome,
            sources.Select(source => OperationItemStatus.Unprocessed(
                source.SourceId.ToString(),
                "not-published")).ToArray());
        return new DatabaseClientResult(
            false,
            completion,
            PublishedGeneration: null,
            "not-published",
            "The candidate was not published.");
    }

    private sealed class RecordingDatabaseClient : IDatabaseClient
    {
        private readonly Func<
            OperationCorrelation,
            DatabaseBuildSpecification,
            Task<DatabaseClientResult>> _build;

        public RecordingDatabaseClient(
            Func<
                OperationCorrelation,
                DatabaseBuildSpecification,
                DatabaseClientResult> build)
            : this((correlation, specification) =>
                Task.FromResult(build(correlation, specification)))
        {
        }

        public RecordingDatabaseClient(
            Func<
                OperationCorrelation,
                DatabaseBuildSpecification,
                Task<DatabaseClientResult>> build)
        {
            _build = build;
        }

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCorrelation? Correlation { get; private set; }

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            Correlation = correlation;
            Started.TrySetResult();
            return _build(correlation, specification);
        }
    }

    private sealed class RecordingHierarchyDatabaseClient(
        Func<OperationCorrelation, DatabaseBuildSpecification, DatabaseClientResult> build)
        : IDatabaseClient
    {
        public DatabaseGenerationSummary? PublishedGeneration { get; private set; }

        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var result = build(correlation, specification);
            if (result.Accepted)
            {
                PublishedGeneration = result.PublishedGeneration;
            }
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingDatabaseClient : IDatabaseClient
    {
        public Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unexpected Database client failure");
    }

    private sealed class RecordingHierarchyDatabaseReviewClient(
        Func<DatabaseReviewQuery, DatabaseReviewClientResult> readPage,
        Func<DatabaseRowInclusionChange, DatabaseRowInclusionClientResult>? setRowsIncluded = null)
        : IDatabaseReviewClient
    {
        public List<(OperationId GenerationId, int StartRowOrdinal, int RowCount)> Requests
        {
            get;
        } = [];

        public List<SourceSetId> SourceSetRequests { get; } = [];

        public List<DatabaseRowInclusionChange> InclusionChanges { get; } = [];

        public Task<DatabaseReviewClientResult> ReadPageAsync(
            DatabaseReviewQuery query,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((query.GenerationId, query.StartRowOrdinal, query.RowCount));
            SourceSetRequests.Add(query.SourceSetId);
            return Task.FromResult(readPage(query));
        }

        public Task<DatabaseRowInclusionClientResult> SetRowsIncludedAsync(
            DatabaseRowInclusionChange change,
            CancellationToken cancellationToken = default)
        {
            InclusionChanges.Add(change);
            return Task.FromResult(setRowsIncluded?.Invoke(change)
                ?? new DatabaseRowInclusionClientResult(true, change.RowOrdinals.Count, null, null));
        }
    }

    private sealed class QueuedSourceIntakeClient(params LoadedSourceContract[] sources)
        : ISourceIntakeClient
    {
        private readonly Queue<LoadedSourceContract> _sources = new(sources);

        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SourceIntakeClientResult(
                true,
                [_sources.Dequeue()],
                null,
                null));
        }

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceRefreshClientResult(true, source, null, null));
    }

    private sealed class BuildContext
    {
        private BuildContext(
            ActiveDiscoveryConfiguration configuration,
            ActiveLoadedSourceSet sources,
            ApplicationWorkflowCoordinator workflow)
        {
            Configuration = configuration;
            Sources = sources;
            Workflow = workflow;
        }

        public ActiveDiscoveryConfiguration Configuration { get; }

        public ActiveLoadedSourceSet Sources { get; }

        public ApplicationWorkflowCoordinator Workflow { get; }

        public static async Task<BuildContext> CreateAsync(
            IProcessingHostSupervisor? supervisor = null,
            IReadOnlyList<string>? informationTypes = null)
        {
            informationTypes ??= ["tag"];
            var sources = new ActiveLoadedSourceSet();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                Path.GetFullPath("source.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var workflow = new ApplicationWorkflowCoordinator(
                supervisor ?? new TrackingProcessingHostSupervisor(),
                new RecordingProcessingHistoryRecorder());
            var loading = new SourceLoadingCoordinator(
                new StaticSourceIntakeClient(source),
                sources,
                workflow);
            Assert.IsTrue((await loading.AddAsync(
                SourceSelectionKind.XmlFile,
                source.Path)).Accepted);
            var sourceSetId = sources.SourceSets.Single().SourceSetId;
            var identities = informationTypes.Select(informationType =>
                new DiscoveryInformationIdentity(
                    sourceSetId,
                    $"/root/{informationType}",
                    informationType)).ToArray();
            var configuration = new ActiveDiscoveryConfiguration();
            configuration.Synchronize(identities);
            configuration.SetSelection(identities, isSelected: true);
            if (informationTypes.SequenceEqual(["tag"], StringComparer.Ordinal))
            {
                configuration.SetDatabaseTagOverride(identities.Single(), "DatabaseName");
            }
            var discovery = await workflow.BeginOperationAsync(WorkflowOperationKind.Discovery);
            Assert.IsTrue(discovery.Accepted);
            Assert.IsTrue(workflow.CompleteOperation(
                discovery.Operation!.OperationId,
                OperationOutcome.CompletedSuccessfully).Accepted);
            return new BuildContext(configuration, sources, workflow);
        }

        public DatabaseBuildCoordinator CreateCoordinator(IDatabaseClient client)
        {
            return new DatabaseBuildCoordinator(
                Configuration,
                Sources,
                Workflow,
                client,
                NullLogger<DatabaseBuildCoordinator>.Instance);
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

    private sealed class UnexpectedDiscoveryClient : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Database creation must not run Discovery.");

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Database creation must not read an occurrence.");
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
}
