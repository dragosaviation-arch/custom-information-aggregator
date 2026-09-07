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
    public void ProcessingHostCompositionContainsTheReleaseDeclaredCmlAdapter()
    {
        using var workspace = new TemporaryXmlDirectory();
        using var host = CreateHost(workspace.Path);

        var adapters = host.Services.GetServices<ISourceAdapter>().ToArray();

        Assert.HasCount(1, adapters);
        Assert.AreEqual(
            ReleaseSupportedSourceStructures.CmlStructureId,
            adapters[0].Declaration.StructureId);
        Assert.AreEqual(XName.Get("cml", string.Empty), adapters[0].Declaration.RootElementName);
        Assert.IsInstanceOfType<ISourceOccurrenceAdapter>(adapters[0]);
        Assert.IsNotNull(host.Services.GetRequiredService<ISourceOccurrenceReader>());
    }

    [TestMethod]
    public async Task ReleaseDeclaredCmlRunsDiscoveryThroughProductionComposition()
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
    public async Task FreshProductionCompositionReadsExactOccurrenceWithoutDiscoveryRun()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "occurrences.xml",
            """
            <cml>
              <identifier>First</identifier>
              <identifier><![CDATA[  Exact MiXeD-Case Value  ]]></identifier>
            </cml>
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
    public async Task ProductionInterpreterRejectsUndeclaredRootAndNamespace()
    {
        using var workspace = new TemporaryXmlDirectory();
        var undeclaredRoot = workspace.CreateSource(
            "undeclared-root.xml",
            "<unknown><value>content</value></unknown>");
        var undeclaredNamespace = workspace.CreateSource(
            "undeclared-namespace.xml",
            "<cml xmlns=\"urn:cia:not-supported\"><value>content</value></cml>");
        using var host = CreateHost(workspace.Path);
        var interpreter = host.Services.GetRequiredService<ISourceInterpreter>();

        var rootResult = await interpreter.InterpretAsync(undeclaredRoot);
        var namespaceResult = await interpreter.InterpretAsync(undeclaredNamespace);

        Assert.AreEqual(SourceInterpretationStatus.Unsupported, rootResult.Status);
        Assert.AreEqual("unsupported-xml-structure", rootResult.Failure?.Code);
        Assert.AreEqual(SourceInterpretationStatus.Unsupported, namespaceResult.Status);
        Assert.AreEqual("unsupported-xml-structure", namespaceResult.Failure?.Code);
    }

    [TestMethod]
    public async Task SameElementValueAdapterRunsDeclaredNonAircraftDiscovery()
    {
        using var workspace = new TemporaryXmlDirectory();
        var source = workspace.CreateSource(
            "inventory.xml",
            """
            <inventory xmlns="urn:cia:test:inventory">
              <stockCode>  STOCK-01  </stockCode>
            </inventory>
            """);
        var adapter = new XmlElementValueSourceAdapter(
            new SourceStructureDeclaration(
                "test.inventory.v1",
                XName.Get("inventory", "urn:cia:test:inventory")));
        var interpreter = new SourceInterpreter(
            [adapter],
            NullLogger<SourceInterpreter>.Instance);
        var occurrenceReader = new SourceOccurrenceReader(
            [adapter],
            NullLogger<SourceOccurrenceReader>.Instance);
        var service = new DiscoveryService(interpreter, occurrenceReader);

        var result = await service.RunAsync(OperationCorrelation.CreateNew(), [source]);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(1, result.Information);
        Assert.AreEqual("stockCode", result.Information[0].InformationType);
        Assert.AreEqual("  STOCK-01  ", result.Information[0].SampleValue);
        Assert.AreEqual(source.SourceId, result.Information[0].ContributingSources[0].SourceId);
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
