using System.Xml.Linq;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Hosting;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ProductionXmlSourceAdapterTests
{
    [TestMethod]
    public void ProcessingHostCompositionContainsTheGenericXmlFallback()
    {
        using var workspace = new TemporaryXmlDirectory();
        using var host = CreateHost(workspace.Path);

        var adapter = host.Services.GetRequiredService<IGenericXmlSourceAdapter>();

        Assert.IsInstanceOfType<GenericXmlElementValueSourceAdapter>(adapter);
        Assert.AreEqual(GenericXmlElementValueSourceAdapter.GenericStructureId, adapter.StructureId);
        Assert.IsEmpty(host.Services.GetServices<ISourceAdapter>());
        Assert.IsNotNull(host.Services.GetRequiredService<ISourceOccurrenceReader>());
    }

    [TestMethod]
    public async Task CmlRunsGenericDiscoveryThroughProductionComposition()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "declared-cml.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE cml SYSTEM "must-not-be-resolved.dtd">
            <cml>
              <identifier>  MiXeD-Case-01  </identifier>
              <description>Exact &amp; represented content</description>
            </cml>
            """);
        using var host = CreateHost(workspace.Path);
        var service = host.Services.GetRequiredService<DiscoveryService>();

        var result = await service.RunAsync(OperationCorrelation.CreateNew(), [source]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.IsEmpty(result.Issues);
        Assert.HasCount(2, result.Information);

        var identifier = result.Information.Single(item => item.InformationType == "identifier");
        Assert.AreEqual("  MiXeD-Case-01  ", identifier.SampleValue);
        Assert.HasCount(1, identifier.ContributingSources);
        Assert.AreEqual(source.SourceId, identifier.ContributingSources[0].SourceId);
    }

    [TestMethod]
    public async Task AmmOccurrenceIsReadByFreshProductionCompositionWithoutDiscoveryRun()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "occurrences.xml",
            """
            <amm>
              <identifier>First</identifier>
              <identifier><![CDATA[  Exact MiXeD-Case Value  ]]></identifier>
            </amm>
            """);
        using var host = CreateHost(workspace.Path);
        var service = host.Services.GetRequiredService<DiscoveryService>();
        var correlation = OperationCorrelation.CreateNew();

        var occurrence = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            "identifier",
            GlobalOrdinal: 2,
            TotalOccurrenceCount: 2,
            source,
            LocalOrdinal: 2,
            ExpectedSourceOccurrenceCount: 2));

        Assert.IsTrue(occurrence.Accepted);
        Assert.AreEqual("  Exact MiXeD-Case Value  ", occurrence.Occurrence?.Value);
        Assert.AreEqual(source.SourceId, occurrence.Occurrence?.SourceId);
        Assert.AreEqual(2, occurrence.Occurrence?.Ordinal);
        Assert.AreEqual(2, occurrence.Occurrence?.TotalOccurrenceCount);
    }

    [TestMethod]
    [DataRow("cml")]
    [DataRow("amm")]
    [DataRow("unrelatedInventory")]
    public async Task ArbitraryRootNamesNeedNoReleaseDeclaration(string rootName)
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            $"{rootName}.xml",
            $"<{rootName}><value>content</value></{rootName}>");
        using var host = CreateHost(workspace.Path);
        var interpreter = host.Services.GetRequiredService<ISourceInterpreter>();
        var discoveryService = host.Services.GetRequiredService<DiscoveryService>();

        var interpretation = await interpreter.InterpretAsync(source);
        var discovery = await discoveryService.RunAsync(OperationCorrelation.CreateNew(), [source]);

        Assert.AreEqual(SourceInterpretationStatus.Usable, interpretation.Status);
        Assert.AreEqual(
            GenericXmlElementValueSourceAdapter.GenericStructureId,
            interpretation.Source?.StructureId);
        Assert.AreEqual(source.SourceId, interpretation.Source?.OriginatingSourceId);
        Assert.IsTrue(discovery.Accepted);
        Assert.HasCount(1, discovery.Information);
        Assert.AreEqual("content", discovery.Information[0].SampleValue);
        Assert.AreEqual(source.SourceId, discovery.Information[0].ContributingSources[0].SourceId);
    }

    [TestMethod]
    public async Task ArbitraryNamespacedNonAircraftXmlRunsGenericDiscovery()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "inventory.xml",
            """
            <inventory xmlns="urn:cia:test:inventory">
              <stockCode>  STOCK-01  </stockCode>
            </inventory>
            """);
        using var host = CreateHost(workspace.Path);
        var service = host.Services.GetRequiredService<DiscoveryService>();

        var result = await service.RunAsync(OperationCorrelation.CreateNew(), [source]);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Information);
        Assert.AreEqual("stockCode", result.Information[0].InformationType);
        Assert.AreEqual("  STOCK-01  ", result.Information[0].SampleValue);
        Assert.AreEqual(source.SourceId, result.Information[0].ContributingSources[0].SourceId);
    }

    [TestMethod]
    public async Task MalformedAndProhibitedEntityXmlFailWithoutResolvingExternalContent()
    {
        using var workspace = new TemporaryXmlDirectory();
        var secretPath = Path.Combine(workspace.Path, "must-not-be-read.txt");
        File.WriteAllText(secretPath, "external content");
        var malformed = workspace.CreateSource("malformed.xml", "<root><value></root>");
        var entity = workspace.CreateSource(
            "entity.xml",
            $"<!DOCTYPE root [<!ENTITY external SYSTEM \"{new Uri(secretPath).AbsoluteUri}\">]><root><value>&external;</value></root>");
        using var host = CreateHost(workspace.Path);
        var interpreter = host.Services.GetRequiredService<ISourceInterpreter>();

        var malformedResult = await interpreter.InterpretAsync(malformed);
        var entityResult = await interpreter.InterpretAsync(entity);

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, malformedResult.Status);
        Assert.AreEqual("malformed-xml", malformedResult.Failure?.Code);
        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, entityResult.Status);
        Assert.AreEqual("malformed-xml", entityResult.Failure?.Code);
        Assert.IsNull(entityResult.Source);
    }

    [TestMethod]
    public async Task ExactSpecializedAdapterTakesPrecedenceOverGenericFallback()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "specialized.xml",
            "<specialized><value>content</value></specialized>");
        var specialized = new XmlElementValueSourceAdapter(
            new SourceStructureDeclaration("test.specialized.v1", XName.Get("specialized")));
        var generic = new GenericXmlElementValueSourceAdapter();
        var interpreter = new SourceInterpreter(
            [specialized],
            generic,
            NullLogger<SourceInterpreter>.Instance);
        var occurrenceReader = new SourceOccurrenceReader(
            [specialized],
            generic,
            NullLogger<SourceOccurrenceReader>.Instance);

        var interpretation = await interpreter.InterpretAsync(source);
        var occurrence = await occurrenceReader.ReadAsync(source, "value", 1);

        Assert.AreEqual(SourceInterpretationStatus.Usable, interpretation.Status);
        Assert.AreEqual("test.specialized.v1", interpretation.Source?.StructureId);
        Assert.IsTrue(occurrence.Accepted);
        Assert.AreEqual("content", occurrence.Value);
    }

    private static Microsoft.Extensions.Hosting.IHost CreateHost(string logDirectory)
    {
        return ProcessingHostApplicationHost.Create(
            [$"--{ApplicationLogPaths.DirectoryConfigurationKey}={logDirectory}"]);
    }

    private sealed class TemporaryXmlDirectory : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR122.Tests");

        public TemporaryXmlDirectory()
        {
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
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

            var root = System.IO.Path.GetFullPath(_testRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an SPR-122 test directory outside its root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }
}
