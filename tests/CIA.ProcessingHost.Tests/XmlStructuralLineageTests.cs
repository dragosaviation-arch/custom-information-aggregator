using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class XmlStructuralLineageTests
{
    [TestMethod]
    public async Task SimpleScalarChildrenRetainTheirParentAndTraversalOrder()
    {
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource(
            "order.xml",
            "<order><item>A</item><qty>2</qty></order>");

        var document = await InterpretAsync(source);
        var item = document.Values[0];
        var quantity = document.Values[1];

        Assert.AreEqual(source.SourceId, document.OriginatingSourceId);
        Assert.AreEqual("item", item.InformationType);
        Assert.AreEqual("A", item.Content);
        Assert.AreEqual(source.SourceId, item.Lineage?.OriginatingSourceId);
        Assert.AreEqual("/order/item", item.Lineage?.StructuralPath);
        Assert.AreEqual(2L, item.Lineage?.NodeInstanceId);
        Assert.AreEqual(1L, item.Lineage?.ParentInstanceId);
        CollectionAssert.AreEqual(new long[] { 1 }, item.Lineage?.AncestorInstanceIds.ToArray());
        Assert.AreEqual(1L, item.Lineage?.TraversalOrder);

        Assert.AreEqual("/order/qty", quantity.Lineage?.StructuralPath);
        Assert.AreEqual(3L, quantity.Lineage?.NodeInstanceId);
        Assert.AreEqual(1L, quantity.Lineage?.ParentInstanceId);
        Assert.AreEqual(2L, quantity.Lineage?.TraversalOrder);
    }

    [TestMethod]
    public async Task RepeatedContainersAssociateChildrenByContainerInstance()
    {
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource(
            "lines.xml",
            "<order><line><item>A</item><qty>2</qty></line><line><item>B</item><qty>5</qty></line></order>");

        var values = (await InterpretAsync(source)).Values;
        var firstItem = values.Single(value => value.Content == "A");
        var firstQuantity = values.Single(value => value.Content == "2");
        var secondItem = values.Single(value => value.Content == "B");
        var secondQuantity = values.Single(value => value.Content == "5");

        Assert.AreEqual(
            firstItem.Lineage?.ParentInstanceId,
            firstQuantity.Lineage?.ParentInstanceId);
        Assert.AreEqual(
            secondItem.Lineage?.ParentInstanceId,
            secondQuantity.Lineage?.ParentInstanceId);
        Assert.AreNotEqual(
            firstItem.Lineage?.ParentInstanceId,
            secondItem.Lineage?.ParentInstanceId);
        Assert.AreEqual("/order/line/item", firstItem.Lineage?.StructuralPath);
        Assert.AreEqual("/order/line/item", secondItem.Lineage?.StructuralPath);
    }

    [TestMethod]
    public async Task NestedRepeatedContainersRetainTheFullAncestorChain()
    {
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource(
            "nested.xml",
            "<orders><order><lines><line><item>A</item></line><line><item>B</item></line></lines></order></orders>");

        var values = (await InterpretAsync(source)).Values;
        var first = values.Single(value => value.Content == "A");
        var second = values.Single(value => value.Content == "B");

        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4 },
            first.Lineage?.AncestorInstanceIds.ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 6 },
            second.Lineage?.AncestorInstanceIds.ToArray());
        Assert.AreEqual(
            first.Lineage?.AncestorInstanceIds[1],
            second.Lineage?.AncestorInstanceIds[1]);
        Assert.AreNotEqual(first.Lineage?.ParentInstanceId, second.Lineage?.ParentInstanceId);
    }

    [TestMethod]
    public async Task IdenticalLeafNamesRemainDistinctByStructuralPath()
    {
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource(
            "parties.xml",
            "<transaction><buyer><name>A</name></buyer><seller><name>B</name></seller></transaction>");

        var values = (await InterpretAsync(source)).Values;

        Assert.IsTrue(values.All(value => value.InformationType == "name"));
        Assert.AreEqual("/transaction/buyer/name", values[0].Lineage?.StructuralPath);
        Assert.AreEqual("/transaction/seller/name", values[1].Lineage?.StructuralPath);
        Assert.AreNotEqual(values[0].Lineage?.ParentInstanceId, values[1].Lineage?.ParentInstanceId);
    }

    [TestMethod]
    public async Task LineageIsDeterministicForTheSameGenericTraversal()
    {
        const string xml = "<root><group><value>A</value><value>B</value></group></root>";
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource("deterministic.xml", xml);

        var first = await InterpretAsync(source);
        var second = await InterpretAsync(source);

        Assert.AreEqual(first.OriginatingSourceId, second.OriginatingSourceId);
        Assert.HasCount(first.Values.Count, second.Values);

        for (var index = 0; index < first.Values.Count; index++)
        {
            var firstLineage = first.Values[index].Lineage!;
            var secondLineage = second.Values[index].Lineage!;
            Assert.AreEqual(firstLineage.StructuralPath, secondLineage.StructuralPath);
            Assert.AreEqual(firstLineage.NodeInstanceId, secondLineage.NodeInstanceId);
            Assert.AreEqual(firstLineage.ParentInstanceId, secondLineage.ParentInstanceId);
            Assert.AreEqual(firstLineage.TraversalOrder, secondLineage.TraversalOrder);
            CollectionAssert.AreEqual(
                firstLineage.AncestorInstanceIds.ToArray(),
                secondLineage.AncestorInstanceIds.ToArray());
        }
    }

    [TestMethod]
    public async Task ArbitraryNamespacedXmlRetainsExactValueAndNamespaceAwarePath()
    {
        using var files = new TemporaryXmlDirectory();
        var source = files.CreateSource(
            "inventory.xml",
            "<x:inventory xmlns:x=\"urn:unrelated:inventory\"><x:entry><x:code>  MiXeD &amp; Exact  </x:code></x:entry></x:inventory>");

        var document = await InterpretAsync(source);
        var value = document.Values.Single();

        Assert.AreEqual(source.SourceId, document.OriginatingSourceId);
        Assert.AreEqual("x:code", value.InformationType);
        Assert.AreEqual("  MiXeD & Exact  ", value.Content);
        Assert.AreEqual(
            "/{urn:unrelated:inventory}inventory/{urn:unrelated:inventory}entry/{urn:unrelated:inventory}code",
            value.Lineage?.StructuralPath);
        Assert.AreEqual("code", value.Lineage?.ElementPath[^1].LocalName);
        Assert.AreEqual("urn:unrelated:inventory", value.Lineage?.ElementPath[^1].NamespaceUri);
        Assert.AreEqual("x:code", value.Lineage?.ElementPath[^1].QualifiedName);
    }

    [TestMethod]
    public async Task SelectedValueStreamingStaysBoundedAndCarriesLineage()
    {
        using var files = new TemporaryXmlDirectory();
        var content = "<root>" + string.Concat(
            Enumerable.Range(1, 600).Select(index => $"<value>{index}</value>")) + "</root>";
        var source = files.CreateSource("large.xml", content);
        var reader = new SourceValueBatchReader(
            [],
            new GenericXmlElementValueSourceAdapter(),
            NullLogger<SourceValueBatchReader>.Instance);
        var batchSizes = new List<int>();
        SourceValueLineage? firstLineage = null;
        SourceValueLineage? lastLineage = null;

        var result = await reader.ReadSelectedAsync(
            source,
            new HashSet<string>(StringComparer.Ordinal) { "value" },
            values =>
            {
                batchSizes.Add(values.Count);
                firstLineage ??= values[0].Lineage;
                lastLineage = values[^1].Lineage;
                return ValueTask.CompletedTask;
            });

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(600, result.ValueCount);
        CollectionAssert.AreEqual(new[] { 256, 256, 88 }, batchSizes);
        Assert.AreEqual("/root/value", firstLineage?.StructuralPath);
        Assert.AreEqual(1L, firstLineage?.TraversalOrder);
        Assert.AreEqual(600L, lastLineage?.TraversalOrder);
        Assert.IsFalse(
            typeof(GenericXmlElementValueSourceAdapter)
                .GetFields(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .Any());
    }

    [TestMethod]
    public async Task GenericLineagePreservesControlledXmlSecurityFailures()
    {
        using var files = new TemporaryXmlDirectory();
        var secretPath = Path.Combine(files.Path, "external.txt");
        File.WriteAllText(secretPath, "must not be resolved");
        var malformed = files.CreateSource("malformed.xml", "<root><value></root>");
        var externalEntity = files.CreateSource(
            "entity.xml",
            $"<!DOCTYPE root [<!ENTITY external SYSTEM \"{new Uri(secretPath).AbsoluteUri}\">]><root><value>&external;</value></root>");
        var interpreter = CreateInterpreter();

        var malformedResult = await interpreter.InterpretAsync(malformed);
        var entityResult = await interpreter.InterpretAsync(externalEntity);

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, malformedResult.Status);
        Assert.AreEqual("malformed-xml", malformedResult.Failure?.Code);
        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, entityResult.Status);
        Assert.AreEqual("malformed-xml", entityResult.Failure?.Code);
        Assert.IsNull(entityResult.Source);
    }

    private static async Task<InterpretedSourceDocument> InterpretAsync(
        LoadedSourceContract source)
    {
        var result = await CreateInterpreter().InterpretAsync(source);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        Assert.IsNotNull(result.Source);
        return result.Source;
    }

    private static SourceInterpreter CreateInterpreter()
    {
        return new SourceInterpreter([], NullLogger<SourceInterpreter>.Instance);
    }

    private sealed class TemporaryXmlDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR135.Tests");

        public TemporaryXmlDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LoadedSourceContract CreateSource(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                path,
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

            var resolvedRoot = System.IO.Path.GetFullPath(_root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);
            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an SPR-135 test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
