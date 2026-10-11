using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Hierarchy;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Database;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DiscoveryOutputPreviewServiceTests
{
    [TestMethod]
    public async Task PreviewUsesDatabaseProjectionForRootRepeatedAndNestedFamiliesWithoutPublication()
    {
        using var workspace = new Workspace();
        var source = workspace.Source("catalog.xml", """
            <catalog>
              <label>Current catalog</label>
              <book><id>B1</id><review><score>5</score></review><review><score>4</score></review></book>
              <book><id>B2</id><review><score>3</score></review></book>
            </catalog>
            """);
        var discovery = await workspace.DiscoverAsync(source);
        var dataset = CreateDataset(discovery, source, RepeatedDataLayout.StructuralRows);
        var selected = discovery.Information.Single(item => item.InformationType == "id");

        var interpreted = (await workspace.Interpreter.InterpretAsync(source)).Source!;
        var authoritative = await workspace.Projector.ProjectAsync(
            dataset,
            source,
            interpreted);
        _ = await workspace.Projector.ProjectAsync(
            dataset,
            source,
            interpreted,
            50);
        var result = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            dataset,
            selected,
            maximumRows: 50));

        Assert.IsTrue(result.Accepted,
            $"{result.Failure?.Code}: {result.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsNotNull(result.Preview);
        Assert.IsGreaterThanOrEqualTo(3, result.Preview.Groups.Count);
        CollectionAssert.AreEqual(
            Describe(authoritative.Rows).ToArray(),
            Describe(result.Preview.Groups.SelectMany(group => group.Rows)).ToArray());
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    public async Task SiblingRepeatedFamiliesStaySeparateAndPreviewRowsAreBounded()
    {
        using var workspace = new Workspace();
        var records = string.Join(string.Empty, Enumerable.Range(1, 40).Select(index => $"""
            <record>
              <id>{index}</id>
              <left><entry><value>L{index}-1</value></entry><entry><value>L{index}-2</value></entry></left>
              <right><entry><value>R{index}-1</value></entry><entry><value>R{index}-2</value></entry></right>
            </record>
            """));
        var source = workspace.Source("siblings.xml", $"<root>{records}</root>");
        var discovery = await workspace.DiscoverAsync(source);
        var dataset = CreateDataset(discovery, source, RepeatedDataLayout.StructuralRows);
        var selected = discovery.Information.Single(item => item.InformationType == "id");

        var result = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            dataset,
            selected,
            maximumRows: 3));

        Assert.IsTrue(result.Accepted,
            $"{result.Failure?.Code}: {result.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsNotNull(result.Preview);
        Assert.IsTrue(result.Preview.Groups.All(group => group.Rows.Count <= 3));
        Assert.IsTrue(result.Preview.Groups.Any(group => group.IsTruncated));
        foreach (var group in result.Preview.Groups)
        {
            var values = group.Rows.SelectMany(row => row.Cells)
                .SelectMany(cell => cell.Values).ToArray();
            Assert.IsFalse(
                values.Any(value => value.StartsWith('L'))
                && values.Any(value => value.StartsWith('R')),
                "Unrelated sibling output families were cross-paired.");
        }

        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    [TestMethod]
    public async Task OccurrenceAndLayoutRequestsRemainSourceSpecificAndEphemeral()
    {
        using var workspace = new Workspace();
        var first = workspace.Source(
            "first.xml",
            "<root><record><id>A</id><value>A1</value><value>A2</value></record></root>");
        var second = workspace.Source(
            "second.xml",
            "<root><record><id>B</id><value>B1</value><value>B2</value></record></root>");
        var discovery = await workspace.DiscoverAsync(first, second);
        var selected = discovery.Information.Single(item => item.InformationType == "id");
        var structuralDataset = CreateDataset(
            discovery,
            [first, second],
            RepeatedDataLayout.StructuralRows);
        var numberedDataset = CreateDataset(
            discovery,
            [first, second],
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns);

        var structural = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            structuralDataset,
            selected,
            maximumRows: 20,
            globalOrdinal: 2));
        var numbered = await workspace.Preview.GetPreviewAsync(CreateRequest(
            discovery,
            numberedDataset,
            selected,
            maximumRows: 20,
            globalOrdinal: 2));

        Assert.IsTrue(structural.Accepted,
            $"{structural.Failure?.Code}: {structural.Failure?.Description} {workspace.PreviewLogger.Exception}");
        Assert.IsTrue(numbered.Accepted, $"{numbered.Failure?.Code}: {numbered.Failure?.Description}");
        Assert.AreEqual(second.SourceId, structural.Preview?.SourceId);
        Assert.AreEqual(second.SourceId, numbered.Preview?.SourceId);
        Assert.AreEqual(RepeatedDataLayout.StructuralRows, structural.Preview?.RepeatedDataLayout);
        Assert.AreEqual(
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            numbered.Preview?.RepeatedDataLayout);
        Assert.IsTrue(structural.Preview!.Groups.SelectMany(group => group.Rows)
            .SelectMany(row => row.Cells).SelectMany(cell => cell.Values)
            .All(value => !value.StartsWith('A')));
        Assert.IsNull(await workspace.Repository.ReadPublishedDatabaseGenerationAsync());
    }

    private static DiscoveryOutputPreviewRequest CreateRequest(
        DiscoveryHostResult discovery,
        DatabaseDatasetBuildSpecification dataset,
        DiscoveredInformation selected,
        int maximumRows,
        int globalOrdinal = 1)
    {
        return new DiscoveryOutputPreviewRequest(
            new DiscoveryOccurrenceLookup(
                discovery.Completion.Correlation.OperationId,
                [selected.Identity],
                globalOrdinal,
                selected.TotalOccurrenceCount),
            new DiscoveryOutputProjectionSpecification(
                dataset.SourceSetId,
                dataset.DisplayName,
                dataset.RepeatedDataLayout,
                dataset.RowFamilies,
                dataset.Fields),
            maximumRows);
    }

    private static DatabaseDatasetBuildSpecification CreateDataset(
        DiscoveryHostResult discovery,
        LoadedSourceContract source,
        RepeatedDataLayout layout) => CreateDataset(discovery, [source], layout);

    private static DatabaseDatasetBuildSpecification CreateDataset(
        DiscoveryHostResult discovery,
        IReadOnlyList<LoadedSourceContract> sources,
        RepeatedDataLayout layout)
    {
        var sourceSetId = sources[0].SourceSetId;
        var assignments = discovery.Information.ToDictionary(
            item => item.Identity,
            item => item.RowFamilyId!.Value);
        var configuration = discovery.Information.Select(item =>
            new DiscoveryConfigurationItem(
                item.Identity,
                DiscoveryInformationDisposition.Selected)).ToArray();
        var fields = DatabaseTagMapper.CreateFieldMappings(
            sourceSetId,
            configuration,
            new Dictionary<DiscoveryInformationIdentity, string>(),
            assignments);
        var selectedFamilies = fields.Select(field => field.RowFamilyId).ToHashSet();
        var families = discovery.FamilyCatalog!.Families
            .Where(family => selectedFamilies.Contains(family.Id))
            .ToArray();
        return new DatabaseDatasetBuildSpecification(
            sourceSetId,
            "Set 1",
            1,
            layout,
            sources,
            families,
            fields);
    }

    private static IEnumerable<string> Describe(IEnumerable<DatabaseReviewRow> rows) =>
        rows.OrderBy(row => row.SemanticIdentity!.RowFamilyId)
            .ThenBy(row => row.Ordinal)
            .Select(row => string.Join("|", row.Cells
                .OrderBy(cell => cell.ColumnIdentity.FieldKey, StringComparer.Ordinal)
                .ThenBy(cell => cell.ColumnIdentity.RepeatCoordinates)
                .SelectMany(cell => cell.Values.Select(value =>
                    $"{cell.ColumnIdentity.FieldKey}:{cell.ColumnIdentity.RepeatCoordinates}:{value.Value}"))));

    private static IEnumerable<string> Describe(IEnumerable<DiscoveryOutputPreviewRow> rows) =>
        rows.OrderBy(row => row.RecordIdentity.RowFamilyId)
            .ThenBy(row => row.Ordinal)
            .Select(row => string.Join("|", row.Cells
                .OrderBy(cell => cell.ColumnIdentity.FieldKey, StringComparer.Ordinal)
                .ThenBy(cell => cell.ColumnIdentity.RepeatCoordinates)
                .SelectMany(cell => cell.Values.Select(value =>
                    $"{cell.ColumnIdentity.FieldKey}:{cell.ColumnIdentity.RepeatCoordinates}:{value}"))));

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR207.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SourceSetId _sourceSetId = SourceSetId.CreateNew();

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(Path.Combine(_root, "LocalAppData")));
            Interpreter = new SourceInterpreter(
                [],
                new GenericXmlElementValueSourceAdapter(),
                NullLogger<SourceInterpreter>.Instance);
            Projector = new DatabaseSourceProjectionService(new HierarchyFlatteningEngine());
            PreviewLogger = new CapturingLogger<DiscoveryOutputPreviewService>();
            Preview = new DiscoveryOutputPreviewService(
                Repository,
                Interpreter,
                Projector,
                PreviewLogger);
        }

        public StructuredInformationRepository Repository { get; }

        public SourceInterpreter Interpreter { get; }

        public DatabaseSourceProjectionService Projector { get; }

        public DiscoveryOutputPreviewService Preview { get; }

        public CapturingLogger<DiscoveryOutputPreviewService> PreviewLogger { get; }

        public LoadedSourceContract Source(string fileName, string content)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllText(path, content);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                _sourceSetId,
                path,
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public async Task<DiscoveryHostResult> DiscoverAsync(
            params LoadedSourceContract[] sources)
        {
            var discovery = new DiscoveryService(
                Interpreter,
                new SourceOccurrenceReader(
                    [],
                    new GenericXmlElementValueSourceAdapter(),
                    NullLogger<SourceOccurrenceReader>.Instance),
                Repository);
            var result = await discovery.RunAsync(OperationCorrelation.CreateNew(), sources);
            Assert.IsTrue(result.Accepted);
            return result;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exception = exception;
        }
    }
}
