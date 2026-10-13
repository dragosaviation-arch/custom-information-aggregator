using CIA.Contracts.Database;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Diagnostics;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Operations;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DiscoveryOutputPlanGoldenPipelineTests
{
    private static readonly string[] SelectedLogicalNames =
        ["shared_name", "family_title", "root_code"];

    private static readonly string[] ExpectedDetailedPaths =
    [
        "/document/family_a_records/family_a/shared_name",
        "/document/family_b_records/family_b/shared_name",
        "/document/family_title",
        "/document/nested_c/family_c_records/family_c/family_title",
        "/document/nested_c/family_c_records/family_c/shared_name",
        "/document/nested_d/level/family_d_records/family_d/family_title",
        "/document/root_code",
        "/document/shared_name"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedProjection =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["/document"] = ["family_title", "root_code", "shared_name"],
            ["/document/family_a_records/family_a"] = ["shared_name"],
            ["/document/family_b_records/family_b"] = ["shared_name"],
            ["/document/nested_c/family_c_records/family_c"] =
                ["family_title", "shared_name"],
            ["/document/nested_d/level/family_d_records/family_d"] = ["family_title"]
        };

    [TestMethod]
    [TestCategory("Spr213RecoveryR0")]
    [TestCategory("Spr213RecoverySliceB")]
    [TestCategory("AlphaRegressionGate")]
    public async Task GoldenStructuralSelectionBuildsFiveCoherentOutputSetsAndRealPreview()
    {
        using var workspace = new Workspace();
        using var workflow = new ApplicationWorkflowCoordinator(
            new ReadyProcessingHostSupervisor(),
            new NullHistory());
        var sourceSet = new ActiveLoadedSourceSet();
        var fixturePath = ResolveFixturePath("golden-output-plan.xml");
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            fixturePath,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var loading = new SourceLoadingCoordinator(
            new AcceptedSourceIntakeClient(source),
            sourceSet,
            workflow);
        var load = await loading.AddAsync(SourceSelectionKind.XmlFile, fixturePath);
        Assert.IsTrue(load.Accepted, load.FailureDescription);
        source = sourceSet.CreateIncludedReadySnapshot().Single();

        var configuration = new ActiveDiscoveryConfiguration();
        var outputPlan = new ActiveDiscoveryOutputPlan();
        var client = new InProcessDiscoveryClient(workspace.Discovery, workspace.Preview);
        var services = new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(outputPlan)
            .AddSingleton(sourceSet)
            .AddSingleton<IApplicationWorkflowCoordinator>(workflow)
            .AddSingleton<IDiscoveryClient>(client)
            .AddSingleton<IDatabaseClient>(new InProcessDatabaseClient(workspace.Database))
            .AddSingleton<ILogger<DatabaseBuildCoordinator>>(
                NullLogger<DatabaseBuildCoordinator>.Instance)
            .AddSingleton<DatabaseBuildCoordinator>()
            .BuildServiceProvider();
        await using (services)
        using (var viewModel = ActivatorUtilities.CreateInstance<DiscoveryWorkspaceViewModel>(
                         services))
        {
            var databaseCoordinator = services.GetRequiredService<DatabaseBuildCoordinator>();

            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !viewModel.IsBusy && viewModel.Information.Count > 0);

            var selectedLogical = SelectedLogicalNames.Select(name =>
                viewModel.Information.Single(item =>
                    item.InformationType == name
                    && item.CandidateKind == SourceValueCandidateKind.Element)).ToArray();
            foreach (var logical in selectedLogical)
            {
                viewModel.ToggleSelectionCommand.Execute(logical);
            }
            await WaitUntilAsync(() =>
                viewModel.SelectionImpact.SelectedLogicalFieldCount == 3
                && viewModel.OutputSets.Count == 5);

            var selectedDetailed = configuration.Current.Items
                .Where(item => item.Disposition == DiscoveryInformationDisposition.Selected
                    && item.Identity.CandidateKind != SourceValueCandidateKind.ElementScope)
                .Select(item => item.Identity)
                .OrderBy(identity => identity.StructuralPath, StringComparer.Ordinal)
                .ToArray();
            Assert.IsGreaterThan(0, selectedDetailed.Length);
            Assert.HasCount(8, selectedDetailed);
            CollectionAssert.AreEqual(
                ExpectedDetailedPaths,
                selectedDetailed.Select(identity => identity.StructuralPath).ToArray());
            Assert.HasCount(
                8,
                selectedDetailed.Select(identity => identity.StructuralIdentity).Distinct().ToArray());
            Assert.AreEqual(3, viewModel.SelectionImpact.SelectedLogicalFieldCount);
            Assert.AreEqual(8, viewModel.SelectionImpact.SelectedDetailedContextCount);
            Assert.HasCount(5, viewModel.SelectionImpact.Groups);
            Assert.HasCount(5, viewModel.OutputSets);
            Assert.HasCount(5, outputPlan.Current.OutputSets);
            Assert.IsTrue(outputPlan.Current.OutputSets.All(set => set.IsEnabled));
            Assert.AreEqual(
                "3 logical tags \u2192 8 detailed fields \u2192 5 output tables",
                viewModel.SelectionFanoutSummary);
            Assert.AreEqual("8 / 8 selected", viewModel.SelectionSummary);
            Assert.AreEqual(
                "Baseline: 5 of 5 proposed Output Sets enabled.",
                viewModel.OutputPlanStatusText);
            Assert.IsTrue(databaseCoordinator.CanBuild());
            Assert.IsTrue(viewModel.BuildDatabaseCommand.CanExecute(null));

            Assert.IsTrue(viewModel.Information.Any(item => item.IsStructuralScope));
            Assert.IsTrue(configuration.Current.Items
                .Where(item => item.Identity.CandidateKind == SourceValueCandidateKind.ElementScope)
                .All(item => item.Disposition != DiscoveryInformationDisposition.Selected));
            Assert.HasCount(4, selectedLogical.Single(item => item.InformationType == "shared_name")
                .DetailedIdentities);
            Assert.HasCount(3, selectedLogical.Single(item => item.InformationType == "family_title")
                .DetailedIdentities);
            Assert.HasCount(1, selectedLogical.Single(item => item.InformationType == "root_code")
                .DetailedIdentities);

            foreach (var expected in ExpectedProjection)
            {
                var impact = viewModel.SelectionImpact.Groups.Single(group =>
                    group.RowFamilyId.AnchorPath == expected.Key);
                CollectionAssert.AreEquivalent(expected.Value, impact.ProjectedColumns.ToArray());
                var set = viewModel.OutputSets.Single(item =>
                    item.RowFamilyIds.Single().AnchorPath == expected.Key);
                Assert.IsTrue(set.IsEnabled);
                Assert.HasCount(expected.Value.Length, impact.ProjectedColumns);
            }

            Assert.AreEqual(
                8,
                viewModel.SelectionImpact.Groups.Sum(group => group.ProjectedColumns.Count));
            Assert.IsTrue(viewModel.OutputSets.All(set => set.RowFamilyIds.Count == 1));

            var familyAPath = "/document/family_a_records/family_a";
            var familyASet = viewModel.OutputSets.Single(item =>
                item.RowFamilyIds.Single().AnchorPath == familyAPath);
            var sharedName = selectedLogical.Single(item => item.InformationType == "shared_name");
            var familyAField = sharedName.Children.Single(item =>
                item.StructuralPath == $"{familyAPath}/shared_name");
            viewModel.ShowOutputModeCommand.Execute(null);
            viewModel.SelectedOutputSet = familyASet;
            viewModel.SelectDetailedInformationCommand.Execute(familyAField);
            await WaitUntilAsync(() => PreviewValues(viewModel).Contains("A name 1"));

            var firstValues = PreviewValues(viewModel);
            CollectionAssert.Contains(firstValues, "A name 1");
            Assert.IsFalse(firstValues.Any(value => value.StartsWith("Root", StringComparison.Ordinal)));
            Assert.IsFalse(firstValues.Any(value => value.StartsWith("B ", StringComparison.Ordinal)));
            Assert.IsFalse(firstValues.Any(value => value.StartsWith("C ", StringComparison.Ordinal)));
            Assert.IsFalse(firstValues.Any(value => value.StartsWith("D ", StringComparison.Ordinal)));

            await viewModel.NextOccurrenceCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => PreviewValues(viewModel).Contains("A name 2"));
            CollectionAssert.Contains(PreviewValues(viewModel), "A name 2");
            CollectionAssert.DoesNotContain(PreviewValues(viewModel), "A name 1");

            var repeated = await workspace.Discovery.RunAsync(
                OperationCorrelation.CreateNew(),
                [source]);
            Assert.IsTrue(repeated.Accepted);
            var repeatedIdentities = repeated.Information
                .Where(item => SelectedLogicalNames.Contains(
                    item.InformationType,
                    StringComparer.Ordinal)
                    && item.Identity.CandidateKind == SourceValueCandidateKind.Element)
                .Select(item => item.Identity)
                .OrderBy(identity => identity.StructuralPath, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(selectedDetailed, repeatedIdentities);

            var approvedPlan = outputPlan.Current;
            var firstBuild = await databaseCoordinator.BuildAsync();
            Assert.IsTrue(firstBuild.Accepted, firstBuild.Rejection?.Reason);
            var firstGeneration = databaseCoordinator.CurrentGeneration!;
            Assert.HasCount(5, firstGeneration.Datasets);
            CollectionAssert.AreEqual(
                approvedPlan.OutputSets.Select(set => set.OutputSetId).ToArray(),
                firstGeneration.Datasets.Select(dataset => dataset.OutputSetId!.Value).ToArray());
            Assert.IsTrue(firstGeneration.Datasets.All(dataset =>
                dataset.ParentSourceSetId == source.SourceSetId
                && dataset.RowFamilies.Count == 1));
            foreach (var dataset in firstGeneration.Datasets)
            {
                var expectedSet = approvedPlan.OutputSets.Single(set =>
                    set.OutputSetId == dataset.OutputSetId);
                var expectedFields = ExpectedProjection[expectedSet.RowFamilyIds.Single().AnchorPath];
                CollectionAssert.AreEquivalent(
                    expectedFields,
                    dataset.Mappings.Select(mapping => mapping.EffectiveName).ToArray());
                Assert.IsTrue(dataset.Mappings.SelectMany(mapping => mapping.DetailedIdentities)
                    .All(identity => identity.StructuralPath == expectedSet.RowFamilyIds.Single().AnchorPath
                        || identity.StructuralPath.StartsWith(
                            expectedSet.RowFamilyIds.Single().AnchorPath + "/",
                            StringComparison.Ordinal)));
            }

            Assert.AreEqual(
                0,
                await CountRowsAsync(
                    workspace.Repository.DatabasePath,
                    "hierarchy_database_datasets",
                    firstGeneration.OperationId));
            Assert.AreEqual(
                5,
                await CountRowsAsync(
                    workspace.Repository.DatabasePath,
                    "hierarchy_output_set_datasets",
                    firstGeneration.OperationId));

            var renamed = approvedPlan.OutputSets[0];
            var disabled = approvedPlan.OutputSets[1];
            Assert.IsTrue(outputPlan.Rename(renamed.OutputSetId, "Renamed approved output"));
            Assert.IsTrue(outputPlan.SetEnabled(disabled.OutputSetId, false));
            var replacement = await databaseCoordinator.BuildAsync();
            Assert.IsTrue(replacement.Accepted, replacement.Rejection?.Reason);
            var replacementGeneration = databaseCoordinator.CurrentGeneration!;
            Assert.HasCount(4, replacementGeneration.Datasets);
            Assert.IsFalse(replacementGeneration.Datasets.Any(dataset =>
                dataset.OutputSetId == disabled.OutputSetId));
            Assert.AreEqual(
                "Renamed approved output",
                replacementGeneration.Datasets.Single(dataset =>
                    dataset.OutputSetId == renamed.OutputSetId).DisplayName);
        }
    }

    [TestMethod]
    [TestCategory("Spr213RecoverySliceB")]
    public async Task ApprovedOutputSetsKeepDifferentParentSourceSetsIsolated()
    {
        using var workspace = new Workspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var firstSource = CreateSource(workspace.Root, firstSet, "first.xml", "<root><item><value>A</value></item></root>");
        var secondSource = CreateSource(workspace.Root, secondSet, "second.xml", "<root><item><value>B</value></item></root>");
        var firstDataset = await CreateOutputSetDatasetAsync(
            workspace, firstSet, firstSource, "First output", 1);
        var secondDataset = await CreateOutputSetDatasetAsync(
            workspace, secondSet, secondSource, "Second output", 2);

        var result = await workspace.Database.BuildAsync(
            OperationCorrelation.CreateNew(),
            new DatabaseBuildSpecification([firstDataset, secondDataset]));

        Assert.IsTrue(result.Accepted, result.Failure?.Description);
        Assert.HasCount(2, result.PublishedGeneration!.Datasets);
        Assert.AreEqual(firstSet, result.PublishedGeneration.Datasets[0].ParentSourceSetId);
        Assert.AreEqual(secondSet, result.PublishedGeneration.Datasets[1].ParentSourceSetId);
        Assert.AreNotEqual(
            result.PublishedGeneration.Datasets[0].OutputSetId,
            result.PublishedGeneration.Datasets[1].OutputSetId);
        Assert.IsTrue(result.PublishedGeneration.Datasets[0].Mappings
            .SelectMany(mapping => mapping.DetailedIdentities)
            .All(identity => identity.SourceSetId == firstSet));
        Assert.IsTrue(result.PublishedGeneration.Datasets[1].Mappings
            .SelectMany(mapping => mapping.DetailedIdentities)
            .All(identity => identity.SourceSetId == secondSet));
    }

    private static LoadedSourceContract CreateSource(
        string root,
        SourceSetId sourceSetId,
        string name,
        string content)
    {
        var path = Path.Combine(root, sourceSetId.ToString(), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new LoadedSourceContract(
            SourceId.CreateNew(), sourceSetId, path, true,
            LoadedSourceStatus.Ready, LoadedSourceKind.XmlFile);
    }

    private static async Task<DatabaseDatasetBuildSpecification> CreateOutputSetDatasetAsync(
        Workspace workspace,
        SourceSetId sourceSetId,
        LoadedSourceContract source,
        string name,
        int ordinal)
    {
        var interpretation = await workspace.Interpreter.InterpretAsync(source);
        Assert.AreEqual(SourceInterpretationStatus.Usable, interpretation.Status);
        var identities = interpretation.Source!.Values
            .Where(value => value.Lineage is not null
                && value.InformationType == "value")
            .Select(value => HierarchySourceOccurrence
                .FromInterpretedValue(sourceSetId, value).Identity)
            .Distinct()
            .ToArray();
        var family = new RowFamilyDefinition(
            new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, "/root/item"),
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/root"));
        var mappings = DatabaseTagMapper.CreateFieldMappings(
            sourceSetId,
            identities.Select(identity => new DiscoveryConfigurationItem(
                identity,
                DiscoveryInformationDisposition.Selected)),
            new Dictionary<DiscoveryInformationIdentity, string>(),
            identities.ToDictionary(identity => identity, _ => family.Id));
        return new DatabaseDatasetBuildSpecification(
            OutputSetId.CreateNew(),
            sourceSetId,
            name,
            ordinal,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            [source],
            [family],
            mappings);
    }

    private static async Task<int> CountRowsAsync(
        string databasePath,
        string table,
        OperationId generationId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE generation_id = $generationId;";
        command.Parameters.AddWithValue("$generationId", generationId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string[] PreviewValues(DiscoveryWorkspaceViewModel viewModel) =>
        viewModel.SelectedOutputPreviewGroup?.Rows
            .SelectMany(row => row.Cells)
            .Select(cell => cell.Value)
            .Where(value => value.Length > 0)
            .ToArray() ?? [];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string ResolveFixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "StructuralDiscovery",
        fileName);

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR213.Recovery.R0.Tests",
            Guid.NewGuid().ToString("N"));

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData")));
            var adapter = new GenericXmlElementValueSourceAdapter();
            Interpreter = new SourceInterpreter(
                [],
                adapter,
                NullLogger<SourceInterpreter>.Instance);
            Discovery = new DiscoveryService(
                Interpreter,
                new SourceOccurrenceReader(
                    [],
                    adapter,
                    NullLogger<SourceOccurrenceReader>.Instance),
                Repository);
            Preview = new DiscoveryOutputPreviewService(
                Repository,
                Interpreter,
                new DatabaseSourceProjectionService(new HierarchyFlatteningEngine()),
                NullLogger<DiscoveryOutputPreviewService>.Instance);
            Database = new DatabaseGenerationService(
                Repository,
                Interpreter,
                new HierarchyFlatteningEngine(),
                new CooperativeOperationCancellation(new NullHistory()),
                NullLogger<DatabaseGenerationService>.Instance);
        }

        public StructuredInformationRepository Repository { get; }

        public SourceInterpreter Interpreter { get; }

        public DiscoveryService Discovery { get; }

        public DiscoveryOutputPreviewService Preview { get; }

        public DatabaseGenerationService Database { get; }

        public string Root => _root;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class InProcessDiscoveryClient(
        DiscoveryService discovery,
        DiscoveryOutputPreviewService preview) : IDiscoveryClient
    {
        public async Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            var result = await discovery.RunAsync(correlation, sources, cancellationToken);
            return new DiscoveryClientResult(
                result.Accepted,
                result.Information,
                result.Issues,
                result.Completion,
                result.Failure?.Code,
                result.Failure?.Description)
            {
                FamilyCatalog = result.FamilyCatalog
            };
        }

        public async Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            var result = await discovery.GetOccurrenceAsync(lookup, cancellationToken);
            return new DiscoveryOccurrenceClientResult(
                result.Accepted,
                result.Occurrence,
                result.Failure?.Code,
                result.Failure?.Description);
        }

        public async Task<DiscoveryOutputPreviewClientResult> GetOutputPreviewAsync(
            DiscoveryOutputPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await preview.GetPreviewAsync(request, cancellationToken);
            return new DiscoveryOutputPreviewClientResult(
                result.Accepted,
                result.Preview,
                result.Failure?.Code,
                result.Failure?.Description);
        }

        public async Task<DiscoveryContributorClientResult> GetContributorsAsync(
            DiscoveryContributorPageQuery query,
            CancellationToken cancellationToken = default)
        {
            var result = await discovery.GetContributorsAsync(query, cancellationToken);
            return new DiscoveryContributorClientResult(
                result.Accepted,
                result.Page,
                result.Failure?.Code,
                result.Failure?.Description);
        }
    }

    private sealed class AcceptedSourceIntakeClient(LoadedSourceContract source)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SourceIntakeClientResult(
                    true,
                    [source],
                    FailureCode: null,
                    FailureDescription: null));

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract loadedSource,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SourceRefreshClientResult(
                    true,
                    loadedSource,
                    FailureCode: null,
                    FailureDescription: null));
    }

    private sealed class InProcessDatabaseClient(DatabaseGenerationService database) : IDatabaseClient
    {
        public async Task<DatabaseClientResult> BuildAsync(
            OperationCorrelation correlation,
            DatabaseBuildSpecification specification,
            CancellationToken cancellationToken = default)
        {
            var result = await database.BuildAsync(correlation, specification, cancellationToken);
            return new DatabaseClientResult(
                result.Accepted,
                result.Completion,
                result.PublishedGeneration,
                result.Failure?.Code,
                result.Failure?.Description);
        }
    }

    private sealed class ReadyProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 213,
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

    private sealed class NullHistory : IProcessingHistoryRecorder
    {
        public void RecordAttempt(ProcessingAttemptRecord record)
        {
        }

        public void RecordDiagnostic(ProcessingDiagnosticRecord record)
        {
        }
    }
}
