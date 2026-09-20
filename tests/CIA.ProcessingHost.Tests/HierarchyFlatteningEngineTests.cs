using CIA.Contracts.Discovery;
using CIA.Contracts.Sources;
using CIA.Core.Hierarchy;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class HierarchyFlatteningEngineTests
{
    [TestMethod]
    public async Task ScalarDocumentProducesOneTypedRowWithExactValues()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-scalar.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.AreEqual(sourceSetId, result.SourceSetId);
        Assert.HasCount(1, result.Rows);
        CollectionAssert.AreEqual(
            new[] { "Exact scalar title", "1" },
            result.Rows[0].Cells.Select(cell => cell.Value).ToArray());
        Assert.IsTrue(result.Rows[0].Cells.All(cell =>
            cell.SourceId == source.SourceId
            && cell.Lineage.OriginatingSourceId == source.SourceId));
    }

    [TestMethod]
    public async Task RepeatedAndNestedRecordsKeepHierarchyProvenFieldsTogether()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-nested-records.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(3, result.Rows);
        AssertPaired(result, "A", "10");
        AssertPaired(result, "B", "20");
        AssertPaired(result, "C", "30");
        Assert.IsTrue(result.Rows.Single(row => Contains(row, "A")).Cells.Any(cell => cell.Value == "P1"));
        Assert.IsTrue(result.Rows.Single(row => Contains(row, "C")).Cells.Any(cell => cell.Value == "P2"));
    }

    [TestMethod]
    [DataRow(RepeatedDataLayout.AlignRepeatedGroupsByPosition, 3)]
    [DataRow(RepeatedDataLayout.StructuralRows, 7)]
    [DataRow(RepeatedDataLayout.AllCombinations, 12)]
    [DataRow(RepeatedDataLayout.NumberRepeatedValuesIntoColumns, 1)]
    public async Task AllModesPreserveTheSameAtomicPair(
        RepeatedDataLayout layout,
        int expectedRows)
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(sourceSetId, layout, source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(expectedRows, result.Rows);
        AssertAtomicPairNeverSplits(result, "A", "10");
        AssertAtomicPairNeverSplits(result, "B", "20");
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task AlignByPositionUsesBlanksAndDiscardsNoUnequalGroupValues()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(3, result.Rows);
        CollectionAssert.AreEqual(
            new[] { "red", "blue", "green" },
            result.Rows
                .Select(row => row.Cells.Single(cell => cell.DetailedIdentity.InformationType == "color").Value)
                .ToArray());
        Assert.HasCount(0, result.Rows[2].Cells.Where(cell =>
            cell.DetailedIdentity.InformationType is "size" or "code" or "price"));
        Assert.AreEqual(9, result.Rows.Sum(row => row.Cells.Count));
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task StructuralRowsDoNotPositionallyAssociateIndependentBranches()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.StructuralRows,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(7, result.Rows);
        Assert.IsFalse(result.Rows.Any(row =>
            row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "color")
            && row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "size")));
        AssertPaired(result, "A", "10");
        AssertPaired(result, "B", "20");
    }

    [TestMethod]
    public async Task AllCombinationsIsExplicitAndUsesDeterministicCartesianExpansion()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AllCombinations,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(12, result.Rows);
        Assert.IsTrue(result.Rows.All(row =>
            row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "code")
            && row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "price")
            && row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "color")
            && row.Cells.Any(cell => cell.DetailedIdentity.InformationType == "size")));
        AssertAtomicPairNeverSplits(result, "A", "10");
        AssertAtomicPairNeverSplits(result, "B", "20");
    }

    [TestMethod]
    public async Task NumberedColumnsUseOneOrdinalForEveryMemberOfAnAtomicGroup()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);
        var cells = result.Rows.Single().Cells;

        Assert.AreEqual(1, cells.Single(cell => cell.Value == "A").ColumnIdentity.RepeatOrdinal);
        Assert.AreEqual(1, cells.Single(cell => cell.Value == "10").ColumnIdentity.RepeatOrdinal);
        Assert.AreEqual(2, cells.Single(cell => cell.Value == "B").ColumnIdentity.RepeatOrdinal);
        Assert.AreEqual(2, cells.Single(cell => cell.Value == "20").ColumnIdentity.RepeatOrdinal);
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            cells.Where(cell => cell.DetailedIdentity.InformationType == "color")
                .Select(cell => cell.ColumnIdentity.RepeatOrdinal!.Value)
                .ToArray());
    }

    [TestMethod]
    [DataRow(RepeatedDataLayout.AlignRepeatedGroupsByPosition)]
    [DataRow(RepeatedDataLayout.StructuralRows)]
    [DataRow(RepeatedDataLayout.AllCombinations)]
    [DataRow(RepeatedDataLayout.NumberRepeatedValuesIntoColumns)]
    public async Task SparseRepeatedRecordsRemainDistinctWithoutSelectedIdentityOverlap(
        RepeatedDataLayout layout)
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-sparse-records.xml", sourceSetId);
        var request = await CreateRequestAsync(sourceSetId, layout, source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);
        var cells = result.Rows.SelectMany(row => row.Cells).ToArray();

        Assert.HasCount(4, cells);
        Assert.HasCount(3, cells
            .Select(GetRecordInstanceId)
            .Distinct());
        Assert.AreNotEqual(
            GetLogicalPosition(result, "A1"),
            GetLogicalPosition(result, "B2"));
        Assert.AreEqual(
            GetLogicalPosition(result, "A3"),
            GetLogicalPosition(result, "B3"));
        CollectionAssert.AreEquivalent(
            new[] { "A1", "B2", "A3", "B3" },
            cells.Select(cell => cell.Value).ToArray());
    }

    [TestMethod]
    public async Task SelectedSubsetDoesNotEraseSparseRecordInstanceBoundaries()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-sparse-records.xml", sourceSetId);
        var document = await InterpretAsync(source);

        var selectedA = CreateSourceInput(
            sourceSetId,
            document,
            identity => identity.InformationType == "a");
        var aResult = await new HierarchyFlatteningEngine().FlattenAsync(
            new HierarchyFlatteningRequest(
                sourceSetId,
                RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                [selectedA]));
        var selectedB = CreateSourceInput(
            sourceSetId,
            document,
            identity => identity.InformationType == "b");
        var bResult = await new HierarchyFlatteningEngine().FlattenAsync(
            new HierarchyFlatteningRequest(
                sourceSetId,
                RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
                [selectedB]));

        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            aResult.Rows.Single().Cells
                .Select(cell => cell.ColumnIdentity.RepeatOrdinal!.Value)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            bResult.Rows.Single().Cells
                .Select(cell => cell.ColumnIdentity.RepeatOrdinal!.Value)
                .ToArray());
        Assert.AreNotEqual(
            GetRecordInstanceId(aResult.Rows.Single().Cells[0]),
            GetRecordInstanceId(aResult.Rows.Single().Cells[1]));
        Assert.AreNotEqual(
            GetRecordInstanceId(bResult.Rows.Single().Cells[0]),
            GetRecordInstanceId(bResult.Rows.Single().Cells[1]));
        Assert.AreEqual(
            GetRecordInstanceId(aResult.Rows.Single().Cells.Single(cell => cell.Value == "A3")),
            GetRecordInstanceId(bResult.Rows.Single().Cells.Single(cell => cell.Value == "B3")));
        Assert.AreNotEqual(
            GetRecordInstanceId(aResult.Rows.Single().Cells.Single(cell => cell.Value == "A1")),
            GetRecordInstanceId(bResult.Rows.Single().Cells.Single(cell => cell.Value == "B2")));
    }

    [TestMethod]
    public async Task NestedNumberedColumnsUseUnambiguousOuterToInnerCoordinates()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-nested-records.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            source);
        var engine = new HierarchyFlatteningEngine();

        var first = await engine.FlattenAsync(request);
        var second = await engine.FlattenAsync(request);
        var cells = first.Rows.Single().Cells;

        AssertCoordinates(cells, "P1", 1);
        AssertCoordinates(cells, "A", 1, 1);
        AssertCoordinates(cells, "10", 1, 1);
        AssertCoordinates(cells, "B", 1, 2);
        AssertCoordinates(cells, "20", 1, 2);
        AssertCoordinates(cells, "P2", 2);
        AssertCoordinates(cells, "C", 2, 1);
        AssertCoordinates(cells, "30", 2, 1);
        Assert.AreEqual(cells.Count, cells.Select(cell => cell.ColumnIdentity).Distinct().Count());
        Assert.AreNotEqual(
            cells.Single(cell => cell.Value == "A").ColumnIdentity,
            cells.Single(cell => cell.Value == "C").ColumnIdentity);
        Assert.IsTrue(cells.All(cell =>
            cell.Lineage.ElementPath.Count > 0
            && cell.Lineage.NodeInstanceId > 0
            && cell.Lineage.TraversalOrder > 0));
        CollectionAssert.AreEqual(CreateSignature(first), CreateSignature(second));
    }

    [TestMethod]
    public async Task RootLevelRecordFamiliesRemainIndependentInEveryMode()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-record-families.xml", sourceSetId);

        foreach (var layout in Enum.GetValues<RepeatedDataLayout>())
        {
            var request = await CreateRequestAsync(sourceSetId, layout, source);
            var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

            Assert.IsFalse(result.Rows.Any(row =>
                row.Cells.Any(cell => cell.StructuralPath.Contains("/buyer/", StringComparison.Ordinal))
                && row.Cells.Any(cell => cell.StructuralPath.Contains("/seller/", StringComparison.Ordinal))));
            Assert.HasCount(2, result.Rows.SelectMany(row => row.Cells).Where(cell =>
                cell.Value == "Shared"));
        }
    }

    [TestMethod]
    public async Task FileBoundaryPreventsAssociationAcrossTwoFilesInTheSameSet()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var first = workspace.CopyFixture(
            "flattening-independent-groups.xml",
            sourceSetId,
            "first.xml");
        var second = workspace.CopyFixture(
            "flattening-independent-groups.xml",
            sourceSetId,
            "second.xml");
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AllCombinations,
            first,
            second);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(24, result.Rows);
        Assert.IsTrue(result.Rows.All(row =>
            row.Cells.Select(cell => cell.SourceId).Distinct().Count() == 1));
        CollectionAssert.AreEquivalent(
            new[] { first.SourceId, second.SourceId },
            result.Rows.Select(row => row.SourceId).Distinct().ToArray());
    }

    [TestMethod]
    public async Task DifferentSourceSetsRemainIndependentAndCannotShareOneRequest()
    {
        using var workspace = new FlatteningWorkspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = workspace.CopyFixture("flattening-scalar.xml", firstSet, "first.xml");
        var second = workspace.CopyFixture("flattening-scalar.xml", secondSet, "second.xml");
        var firstDocument = await InterpretAsync(first);
        var secondDocument = await InterpretAsync(second);
        var firstInput = CreateSourceInput(firstSet, firstDocument);
        var secondInput = CreateSourceInput(secondSet, secondDocument);

        Assert.ThrowsExactly<ArgumentException>(() => new HierarchyFlatteningRequest(
            firstSet,
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            [firstInput, secondInput]));

        var firstResult = await new HierarchyFlatteningEngine().FlattenAsync(
            new HierarchyFlatteningRequest(
                firstSet,
                RepeatedDataLayout.AlignRepeatedGroupsByPosition,
                [firstInput]));
        var secondResult = await new HierarchyFlatteningEngine().FlattenAsync(
            new HierarchyFlatteningRequest(
                secondSet,
                RepeatedDataLayout.AlignRepeatedGroupsByPosition,
                [secondInput]));

        Assert.AreEqual(firstSet, firstResult.SourceSetId);
        Assert.AreEqual(secondSet, secondResult.SourceSetId);
        Assert.AreNotEqual(firstResult.Rows[0].SourceId, secondResult.Rows[0].SourceId);
    }

    [TestMethod]
    public async Task ElementAttributeAndStructuralCandidatesShareTheirProvenRecord()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("stable-slot-records.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.StructuralRows,
            source);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.HasCount(2, result.Rows);
        var first = result.Rows.Single(row => Contains(row, "C100"));
        Assert.IsTrue(first.Cells.Any(cell =>
            cell.CandidateKind == SourceValueCandidateKind.Element && cell.Value == "C100"));
        Assert.IsTrue(first.Cells.Any(cell =>
            cell.CandidateKind == SourceValueCandidateKind.Attribute && cell.Value == "A"));
        Assert.IsTrue(first.Cells.Any(cell =>
            cell.CandidateKind == SourceValueCandidateKind.Structural && cell.Value == "2"));
        Assert.IsTrue(first.Cells.Any(cell =>
            cell.CandidateKind == SourceValueCandidateKind.Structural && cell.Value == "Widget"));
    }

    [TestMethod]
    public async Task OnlySelectedDetailedPhysicalIdentitiesEnterTheResult()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-record-families.xml", sourceSetId);
        var document = await InterpretAsync(source);
        var selected = document.Values
            .Select(value => HierarchySourceOccurrence
                .FromInterpretedValue(sourceSetId, value)
                .Identity)
            .Where(identity => identity.InformationType == "name")
            .Distinct()
            .ToArray();
        var input = HierarchySourceInput.FromInterpretedSource(
            sourceSetId,
            document,
            selected);
        var request = new HierarchyFlatteningRequest(
            sourceSetId,
            RepeatedDataLayout.StructuralRows,
            [input]);

        var result = await new HierarchyFlatteningEngine().FlattenAsync(request);

        Assert.IsTrue(result.Rows.SelectMany(row => row.Cells).All(cell =>
            cell.DetailedIdentity.InformationType == "name"));
        CollectionAssert.AreEquivalent(
            selected.Select(identity => identity.StructuralPath).Distinct().ToArray(),
            result.Rows.SelectMany(row => row.Cells)
                .Select(cell => cell.DetailedIdentity.StructuralPath)
                .Distinct()
                .ToArray());
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task OversizeAllCombinationsFailsBeforeReturningAnyPartialResult()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AllCombinations,
            source);
        var engine = new HierarchyFlatteningEngine(new HierarchyFlatteningOptions(11));

        var exception = await Assert.ThrowsExactlyAsync<AllCombinationsLimitExceededException>(
            async () => await engine.FlattenAsync(request));

        Assert.AreEqual(12L, exception.ProjectedRows);
        Assert.AreEqual(11, exception.MaximumRows);
    }

    [TestMethod]
    [TestCategory("AlphaRegressionGate")]
    public async Task AllCombinationsHonorsCancellationDuringGeneration()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-large-combinations.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AllCombinations,
            source);
        var engine = new HierarchyFlatteningEngine(new HierarchyFlatteningOptions(2_000));
        using var cancellation = new CancellationTokenSource();

        var operation = engine.FlattenAsync(request, cancellation.Token).AsTask();
        await Task.Yield();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await operation);
    }

    [TestMethod]
    public async Task AllCombinationsChecksCancellationBeforeProjection()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-large-combinations.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.AllCombinations,
            source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new HierarchyFlatteningEngine().FlattenAsync(request, cancellation.Token));
    }

    [TestMethod]
    public async Task UnchangedInputProducesLogicallyIdenticalOutput()
    {
        using var workspace = new FlatteningWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var source = workspace.CopyFixture("flattening-independent-groups.xml", sourceSetId);
        var request = await CreateRequestAsync(
            sourceSetId,
            RepeatedDataLayout.NumberRepeatedValuesIntoColumns,
            source);
        var engine = new HierarchyFlatteningEngine();

        var first = await engine.FlattenAsync(request);
        var second = await engine.FlattenAsync(request);

        CollectionAssert.AreEqual(CreateSignature(first), CreateSignature(second));
    }

    [TestMethod]
    public void EngineAndContractsRemainIndependentOfXmlTreesDatabaseUiAndExport()
    {
        var assembly = typeof(HierarchyFlatteningEngine).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.IsFalse(references.Contains("Microsoft.Data.Sqlite", StringComparer.Ordinal));
        Assert.IsFalse(references.Contains("DocumentFormat.OpenXml", StringComparer.Ordinal));
        Assert.IsFalse(references.Contains("PresentationFramework", StringComparer.Ordinal));
        Assert.IsFalse(typeof(HierarchyFlatteningEngine)
            .GetFields(System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public)
            .Any(field => typeof(System.Xml.XmlDocument).IsAssignableFrom(field.FieldType)));
    }

    private static async Task<HierarchyFlatteningRequest> CreateRequestAsync(
        SourceSetId sourceSetId,
        RepeatedDataLayout layout,
        params LoadedSourceContract[] sources)
    {
        var inputs = new List<HierarchySourceInput>();
        foreach (var source in sources)
        {
            var document = await InterpretAsync(source);
            inputs.Add(CreateSourceInput(sourceSetId, document));
        }

        return new HierarchyFlatteningRequest(sourceSetId, layout, inputs);
    }

    private static HierarchySourceInput CreateSourceInput(
        SourceSetId sourceSetId,
        InterpretedSourceDocument document,
        Func<DiscoveryInformationIdentity, bool>? include = null)
    {
        var identities = document.Values
            .Where(value => value.Lineage is not null)
            .Select(value => HierarchySourceOccurrence
                .FromInterpretedValue(sourceSetId, value)
                .Identity)
            .Where(identity => include?.Invoke(identity) ?? true)
            .Distinct()
            .ToArray();
        return HierarchySourceInput.FromInterpretedSource(sourceSetId, document, identities);
    }

    private static async Task<InterpretedSourceDocument> InterpretAsync(
        LoadedSourceContract source)
    {
        var result = await new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance)
            .InterpretAsync(source);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        Assert.IsNotNull(result.Source);
        return result.Source;
    }

    private static string[] CreateSignature(HierarchyFlatteningResult result)
    {
        return result.Rows.Select(row => string.Join(
            "|",
            row.Cells.Select(cell => string.Join(
                ":",
                cell.DetailedIdentity.InformationType,
                cell.DetailedIdentity.CandidateKind,
                cell.DetailedIdentity.StructuralIdentity,
                cell.ColumnIdentity.RepeatCoordinates,
                cell.Value,
                cell.SourceId,
                cell.Lineage.NodeInstanceId,
                cell.Lineage.TraversalOrder)))).ToArray();
    }

    private static void AssertPaired(
        HierarchyFlatteningResult result,
        string first,
        string second)
    {
        var row = result.Rows.Single(candidate => Contains(candidate, first));
        Assert.IsTrue(Contains(row, second));
    }

    private static void AssertAtomicPairNeverSplits(
        HierarchyFlatteningResult result,
        string first,
        string second)
    {
        var rowsContainingFirst = result.Rows.Where(row => Contains(row, first)).ToArray();
        Assert.IsNotEmpty(rowsContainingFirst);
        Assert.IsTrue(rowsContainingFirst.All(row => Contains(row, second)));
        Assert.IsFalse(result.Rows.Any(row => Contains(row, second) && !Contains(row, first)));
    }

    private static bool Contains(FlattenedHierarchyRow row, string value)
    {
        return row.Cells.Any(cell => string.Equals(cell.Value, value, StringComparison.Ordinal));
    }

    private static long GetRecordInstanceId(FlattenedHierarchyCell cell)
    {
        return cell.Lineage.ElementPath
            .Single(element => element.LocalName == "record")
            .InstanceId;
    }

    private static string GetLogicalPosition(
        HierarchyFlatteningResult result,
        string value)
    {
        var row = result.Rows.Single(candidate => Contains(candidate, value));
        var cell = row.Cells.Single(candidate => candidate.Value == value);
        return cell.ColumnIdentity.RepeatCoordinates.Count == 0
            ? $"row:{row.Ordinal}"
            : $"repeat:{cell.ColumnIdentity.RepeatCoordinates}";
    }

    private static void AssertCoordinates(
        IReadOnlyList<FlattenedHierarchyCell> cells,
        string value,
        params int[] expected)
    {
        CollectionAssert.AreEqual(
            expected,
            cells.Single(cell => cell.Value == value)
                .ColumnIdentity.RepeatCoordinates.Coordinates.ToArray());
    }

    private sealed class FlatteningWorkspace : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR137.Tests");

        public FlatteningWorkspace()
        {
            Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LoadedSourceContract CopyFixture(
            string fixtureName,
            SourceSetId sourceSetId,
            string? targetName = null)
        {
            var fixturePath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "StructuralDiscovery",
                fixtureName);
            var targetPath = System.IO.Path.Combine(Path, targetName ?? fixtureName);
            File.Copy(fixturePath, targetPath);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId,
                targetPath,
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);
            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an SPR-137 test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
