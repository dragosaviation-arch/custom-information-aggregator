using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class StructuralDiscoveryCandidateTests
{
    [TestMethod]
    public async Task GenericInterpretationPreservesElementFieldsAndExposesDistinctAttributes()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CopyFixture("element-and-attributes.xml");

        var document = await InterpretAsync(source);
        var elementCode = document.Values.Single(value =>
            value.CandidateKind == SourceValueCandidateKind.Element
            && value.InformationType == "code");
        var attributeCode = document.Values.Single(value =>
            value.CandidateKind == SourceValueCandidateKind.Attribute
            && value.InformationType == "code");
        var name = document.Values.Single(value => value.InformationType == "name");

        Assert.AreEqual("C100", elementCode.Content);
        Assert.AreEqual("C100", attributeCode.Content);
        Assert.AreNotEqual(elementCode.StructuralIdentity, attributeCode.StructuralIdentity);
        Assert.AreEqual("/inventory/item/code", elementCode.StructuralIdentity);
        Assert.AreEqual("/inventory/item/@code", attributeCode.StructuralIdentity);
        Assert.AreEqual("  Widget  ", name.Content);
        Assert.AreEqual(source.SourceId, attributeCode.Lineage?.OriginatingSourceId);
        Assert.AreEqual(elementCode.Lineage?.ParentInstanceId, name.Lineage?.ParentInstanceId);
    }

    [TestMethod]
    public async Task RepeatedRecordsUseStableAttributesForPlainStructuralSlots()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CopyFixture("stable-slot-records.xml");

        var values = (await InterpretAsync(source)).Values;
        var quantities = values
            .Where(value => value.InformationType == "cell [slot=B]")
            .ToArray();
        var descriptions = values
            .Where(value => value.InformationType == "cell [slot=C]")
            .ToArray();

        CollectionAssert.AreEqual(new[] { "2", "5" }, quantities.Select(value => value.Content).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Widget", "Gadget" },
            descriptions.Select(value => value.Content).ToArray());
        Assert.IsTrue(quantities.All(value => value.CandidateKind == SourceValueCandidateKind.Structural));
        Assert.IsTrue(descriptions.All(value => value.CandidateKind == SourceValueCandidateKind.Structural));
        Assert.AreEqual(
            "/records/record/cell[@slot='B']",
            quantities[0].StructuralIdentity);
        Assert.AreEqual(
            "/records/record/cell[@slot='C']",
            descriptions[0].StructuralIdentity);
        Assert.AreEqual(
            quantities[0].Lineage?.ParentInstanceId,
            descriptions[0].Lineage?.ParentInstanceId);
        Assert.AreNotEqual(
            quantities[0].Lineage?.ParentInstanceId,
            quantities[1].Lineage?.ParentInstanceId);
    }

    [TestMethod]
    public async Task RepeatedRecordsFallBackToDeterministicSiblingPosition()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CopyFixture("position-slot-records.xml");

        var first = await InterpretAsync(source);
        var second = await InterpretAsync(source);
        var firstPositions = first.Values
            .Where(value => value.CandidateKind == SourceValueCandidateKind.Structural)
            .ToArray();
        var secondPositions = second.Values
            .Where(value => value.CandidateKind == SourceValueCandidateKind.Structural)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "10", "Alpha", "20", "Beta" },
            firstPositions.Select(value => value.Content).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "member [position 2]",
                "member [position 3]",
                "member [position 2]",
                "member [position 3]"
            },
            firstPositions.Select(value => value.InformationType).ToArray());
        CollectionAssert.AreEqual(
            firstPositions.Select(value => value.StructuralIdentity).ToArray(),
            secondPositions.Select(value => value.StructuralIdentity).ToArray());
        Assert.IsTrue(firstPositions.All(value =>
            value.Lineage?.StructuralPath ==
            "/{urn:generic:unrelated}unrelated/{urn:generic:unrelated}group/{urn:generic:unrelated}member"));
    }

    [TestMethod]
    public async Task MixedExplicitAndStructuralValuesAreNotDuplicated()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CopyFixture("stable-slot-records.xml");

        var values = (await InterpretAsync(source)).Values;

        Assert.HasCount(1, values.Where(value => value.Content == "C100"));
        Assert.HasCount(1, values.Where(value => value.Content == "C200"));
        Assert.HasCount(1, values.Where(value => value.Content == "2"));
        Assert.HasCount(1, values.Where(value => value.Content == "Widget"));
        Assert.AreEqual(
            SourceValueCandidateKind.Element,
            values.Single(value => value.Content == "C100").CandidateKind);
        Assert.AreEqual(
            SourceValueCandidateKind.Structural,
            values.Single(value => value.Content == "2").CandidateKind);
    }

    [TestMethod]
    public async Task DiscoverySeparatesCandidateKindSetAndStructuralIdentityAndPreviewsExactSlot()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = workspace.CopyFixture("stable-slot-records.xml", firstSet, "first.xml");
        var second = workspace.CopyFixture("stable-slot-records.xml", secondSet, "second.xml");
        var service = CreateDiscoveryService();
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(correlation, [first, second]);
        var firstQuantity = result.Information.Single(information =>
            information.SourceSetId == firstSet
            && information.Identity.CandidateKind == SourceValueCandidateKind.Structural
            && information.Identity.StructuralIdentity.EndsWith("[@slot='B']", StringComparison.Ordinal));
        var secondQuantity = result.Information.Single(information =>
            information.SourceSetId == secondSet
            && information.Identity.CandidateKind == SourceValueCandidateKind.Structural
            && information.Identity.StructuralIdentity.EndsWith("[@slot='B']", StringComparison.Ordinal));
        var attributeCode = result.Information.Single(information =>
            information.SourceSetId == firstSet
            && information.InformationType == "slot"
            && information.Identity.CandidateKind == SourceValueCandidateKind.Attribute);

        Assert.AreNotEqual(firstQuantity.Identity, secondQuantity.Identity);
        Assert.AreNotEqual(firstQuantity.Identity, attributeCode.Identity);
        Assert.AreEqual(2, firstQuantity.TotalOccurrenceCount);

        var preview = await CreateDiscoveryService().GetOccurrenceAsync(
            new DiscoveryOccurrenceLookup(
                correlation.OperationId,
                firstQuantity.Identity,
                GlobalOrdinal: 2,
                TotalOccurrenceCount: 2,
                first,
                LocalOrdinal: 2,
                ExpectedSourceOccurrenceCount: 2));

        Assert.IsTrue(preview.Accepted);
        Assert.AreEqual("5", preview.Occurrence?.Value);
        Assert.AreEqual(first.SourceId, preview.Occurrence?.SourceId);
        Assert.AreEqual(firstQuantity.Identity, preview.Occurrence?.Identity);
    }

    [TestMethod]
    public async Task StructurallyEquivalentSlotsAtDifferentPathsRemainSeparate()
    {
        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CopyFixture("separate-record-contexts.xml");

        var result = await CreateDiscoveryService().RunAsync(
            OperationCorrelation.CreateNew(),
            [source]);
        var quantities = result.Information
            .Where(information =>
                information.InformationType == "cell [slot=B]"
                && information.Identity.CandidateKind == SourceValueCandidateKind.Structural)
            .ToArray();

        Assert.HasCount(2, quantities);
        Assert.IsTrue(quantities.Any(information =>
            information.Identity.StructuralIdentity ==
            "/root/left/record/cell[@slot='B']"));
        Assert.IsTrue(quantities.Any(information =>
            information.Identity.StructuralIdentity ==
            "/root/right/record/cell[@slot='B']"));
        Assert.IsTrue(quantities.All(information => information.TotalOccurrenceCount == 2));
    }

    [TestMethod]
    public async Task CandidateReaderRetainsOnlyBoundedShapeStateAndNoXmlTree()
    {
        var readerType = typeof(GenericXmlElementValueSourceAdapter).Assembly.GetType(
            "CIA.ProcessingHost.SourceInterpretation.XmlElementValueReader",
            throwOnError: true)!;
        var fields = readerType.GetFields(
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        Assert.IsFalse(fields.Any(field =>
            typeof(System.Xml.Linq.XNode).IsAssignableFrom(field.FieldType)
            || typeof(System.Xml.XmlDocument).IsAssignableFrom(field.FieldType)));

        using var workspace = new StructuralDiscoveryWorkspace();
        var source = workspace.CreateSource(
            "arbitrary.xml",
            "<root><record><slot>1</slot><slot>A</slot></record><record><slot>2</slot><slot>B</slot></record></root>");
        var document = await InterpretAsync(source);

        Assert.IsTrue(document.Values.Any(value =>
            value.CandidateKind == SourceValueCandidateKind.Structural));
    }

    private static async Task<InterpretedSourceDocument> InterpretAsync(
        LoadedSourceContract source)
    {
        var result = await new SourceInterpreter(
                [],
                NullLogger<SourceInterpreter>.Instance)
            .InterpretAsync(source);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        Assert.IsNotNull(result.Source);
        return result.Source;
    }

    private static DiscoveryService CreateDiscoveryService()
    {
        var generic = new GenericXmlElementValueSourceAdapter();
        return new DiscoveryService(
            new SourceInterpreter(
                [],
                generic,
                NullLogger<SourceInterpreter>.Instance),
            new SourceOccurrenceReader(
                [],
                generic,
                NullLogger<SourceOccurrenceReader>.Instance));
    }

    private sealed class StructuralDiscoveryWorkspace : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR148.Tests");

        public StructuralDiscoveryWorkspace()
        {
            Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LoadedSourceContract CopyFixture(
            string fixtureName,
            SourceSetId? sourceSetId = null,
            string? targetName = null)
        {
            var fixturePath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "StructuralDiscovery",
                fixtureName);
            var targetPath = System.IO.Path.Combine(Path, targetName ?? fixtureName);
            File.Copy(fixturePath, targetPath);
            return CreateContract(targetPath, sourceSetId);
        }

        public LoadedSourceContract CreateSource(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return CreateContract(path, sourceSetId: null);
        }

        private static LoadedSourceContract CreateContract(
            string path,
            SourceSetId? sourceSetId)
        {
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId ?? SourceSetId.CreateNew(),
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

            var resolvedRoot = System.IO.Path.GetFullPath(root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);
            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an SPR-148 test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
