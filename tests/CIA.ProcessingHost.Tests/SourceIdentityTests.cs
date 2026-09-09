using System.Text.Json;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceIntake;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class SourceIdentityTests
{
    [TestMethod]
    public void NewSourceIdentitiesAreNonEmptyUniqueTypedValues()
    {
        var sourceIds = Enumerable.Range(0, 100)
            .Select(_ => SourceId.CreateNew())
            .ToArray();

        Assert.IsTrue(sourceIds.All(sourceId => sourceId.Value != Guid.Empty));
        Assert.AreEqual(sourceIds.Length, sourceIds.Distinct().Count());
        Assert.AreEqual(typeof(SourceId), sourceIds[0].GetType());
    }

    [TestMethod]
    public void SourceIdentityUsesStrictJsonAndRejectsEmptyValues()
    {
        var sourceId = SourceId.CreateNew();
        var json = JsonSerializer.Serialize(sourceId);

        Assert.AreEqual($"\"{sourceId}\"", json);
        Assert.AreEqual(sourceId, JsonSerializer.Deserialize<SourceId>(json));
        Assert.ThrowsExactly<ArgumentException>(() => SourceId.From(Guid.Empty));
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<SourceId>($"\"{Guid.Empty}\""));
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<SourceId>("{}"));
    }

    [TestMethod]
    public void SourceSetIdentityUsesStrictTypedJsonAndRemainsOpaqueToPresentation()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var json = JsonSerializer.Serialize(sourceSetId);

        Assert.AreNotEqual(Guid.Empty, sourceSetId.Value);
        Assert.AreEqual($"\"{sourceSetId}\"", json);
        Assert.AreEqual(sourceSetId, JsonSerializer.Deserialize<SourceSetId>(json));
        Assert.ThrowsExactly<ArgumentException>(() => SourceSetId.From(Guid.Empty));
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<SourceSetId>($"\"{Guid.Empty}\""));
    }

    [TestMethod]
    public void SourceIdentityRemainsStableWhenThePathAttributeChanges()
    {
        var sourceId = SourceId.CreateNew();
        var sourceSetId = SourceSetId.CreateNew();
        var source = new LoadedSourceContract(
            sourceId,
            sourceSetId,
            Path.GetFullPath("original/source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);

        var updatedPath = source with { Path = Path.GetFullPath("relinked/source.xml") };

        Assert.AreEqual(sourceId, updatedPath.SourceId);
        Assert.AreEqual(sourceSetId, updatedPath.SourceSetId);
        Assert.AreNotEqual(source.Path, updatedPath.Path);
    }

    [TestMethod]
    public void OriginatingSourceIdentityCanBeCarriedIntoLaterDerivedResults()
    {
        var sourceId = SourceId.CreateNew();
        var interpretedSource = new InterpretedSourceDocument(
            sourceId,
            "test.catalog.v1",
            [new InterpretedSourceValue("ProductSku", "SKU-1")]);
        var occurrence = new DerivedOccurrence(
            interpretedSource.OriginatingSourceId,
            interpretedSource.Values[0].Content);

        Assert.AreEqual(sourceId, occurrence.OriginatingSourceId);
        Assert.AreEqual("SKU-1", occurrence.Content);
    }

    [TestMethod]
    public void InterpretedSourceRejectsMissingProvenance()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new InterpretedSourceDocument(
                default,
                "test.catalog.v1",
                Array.Empty<InterpretedSourceValue>()));
    }

    [TestMethod]
    public void SourceProcessingBoundaryHasNoExternalServiceClientDependency()
    {
        var sourceBoundaryAssemblies = new[]
        {
            typeof(SourceId).Assembly,
            typeof(InterpretedSourceDocument).Assembly,
            typeof(SourceIntakeService).Assembly
        };
        string[] externalClientAssemblyPrefixes =
        [
            "System.Net.Http",
            "Azure.",
            "AWSSDK.",
            "Google.Cloud.",
            "Grpc.Net.Client"
        ];

        var references = sourceBoundaryAssemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.IsFalse(references.Any(reference => externalClientAssemblyPrefixes.Any(
            prefix => reference.StartsWith(prefix, StringComparison.Ordinal))));
    }

    private sealed record DerivedOccurrence(SourceId OriginatingSourceId, string Content);
}
