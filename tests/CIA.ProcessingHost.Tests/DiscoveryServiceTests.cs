using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DiscoveryServiceTests
{
    [TestMethod]
    public async Task RealInterpretedValuesAggregateByInformationAndSourceIdentity()
    {
        using var workspace = new DiscoveryWorkspace();
        var first = workspace.CreateSource(
            "first.xml",
            "<catalog><name>Alpha</name><name>Bravo</name><code>A-1</code></catalog>");
        var second = workspace.CreateSource(
            "second.xml",
            "<catalog><name>Charlie</name><code>B-2</code></catalog>");
        var service = CreateService();

        var result = await service.RunAsync(
            OperationCorrelation.CreateNew(),
            [first, second]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.IsEmpty(result.Issues);
        Assert.HasCount(2, result.Information);

        var names = result.Information.Single(item => item.InformationType == "Name");
        Assert.AreEqual(3, names.TotalOccurrenceCount);
        Assert.AreEqual("Alpha", names.SampleValue);
        Assert.HasCount(2, names.ContributingSources);
        Assert.AreEqual(
            names.TotalOccurrenceCount,
            names.ContributingSources.Sum(source => source.OccurrenceCount));
        Assert.AreEqual(
            2,
            names.ContributingSources.Single(source => source.SourceId == first.SourceId)
                .OccurrenceCount);
        Assert.AreEqual(
            1,
            names.ContributingSources.Single(source => source.SourceId == second.SourceId)
                .OccurrenceCount);
        CollectionAssert.AreEquivalent(
            new[] { first.SourceId, second.SourceId },
            names.ContributingSources.Select(source => source.SourceId).ToArray());
    }

    [TestMethod]
    public async Task ProblematicSourceProducesTruthfulPartialDiscoveryResult()
    {
        using var workspace = new DiscoveryWorkspace();
        var usable = workspace.CreateSource(
            "usable.xml",
            "<catalog><name>Alpha</name></catalog>");
        var malformed = workspace.CreateSource(
            "malformed.xml",
            "<catalog><name>Broken</catalog>");

        var result = await CreateService().RunAsync(
            OperationCorrelation.CreateNew(),
            [usable, malformed]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, result.Completion.Outcome);
        Assert.HasCount(1, result.Information);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(malformed.SourceId, result.Issues[0].SourceId);
        Assert.AreEqual("malformed-xml", result.Issues[0].Code);
        Assert.AreEqual(
            OperationItemState.Failed,
            result.Completion.Items.Single(item => item.ItemId == malformed.SourceId.ToString()).State);
    }

    [TestMethod]
    public async Task NoUsableSourceReturnsControlledFailureWithoutDiscoveredRows()
    {
        using var workspace = new DiscoveryWorkspace();
        var unsupported = workspace.CreateSource(
            "unsupported.xml",
            "<different><value>content</value></different>");

        var result = await CreateService().RunAsync(
            OperationCorrelation.CreateNew(),
            [unsupported]);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, result.Completion.Outcome);
        Assert.IsEmpty(result.Information);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual("unsupported-xml-structure", result.Issues[0].Code);
        Assert.AreEqual("discovery-no-usable-sources", result.Failure?.Code);
    }

    [TestMethod]
    public async Task DiscoveryContractsRoundTripAndRejectCountMismatch()
    {
        var sourceId = SourceId.CreateNew();
        var correlation = OperationCorrelation.CreateNew();
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [OperationItemStatus.ProcessedSuccessfully(sourceId.ToString())]);
        var response = new RunDiscoveryResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            CommandAcceptance.Accepted,
            completion,
            [
                new DiscoveredInformation(
                    "Name",
                    2,
                    [new DiscoveredSourceContribution(sourceId, "source.xml", 2)],
                    "Alpha")
            ],
            Array.Empty<DiscoverySourceIssue>(),
            Failure: null);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);
        stream.Position = 0;
        var roundTripped = (RunDiscoveryResponse)await LengthPrefixedJsonMessageFramer
            .ReadAsync(stream);

        Assert.AreEqual(response.Completion.Correlation, roundTripped.Completion.Correlation);
        Assert.AreEqual("Name", roundTripped.Information[0].InformationType);
        Assert.AreEqual(sourceId, roundTripped.Information[0].ContributingSources[0].SourceId);

        var invalid = response with
        {
            Information =
            [
                new DiscoveredInformation(
                    "Name",
                    3,
                    [new DiscoveredSourceContribution(sourceId, "source.xml", 2)],
                    "Alpha")
            ]
        };
        await using var invalidStream = new MemoryStream();
        var exception = await Assert.ThrowsExactlyAsync<IpcProtocolException>(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(invalidStream, invalid).AsTask());

        Assert.AreEqual(IpcProtocolError.InvalidContract, exception.Error);
        Assert.AreEqual(0, invalidStream.Length);
    }

    private static DiscoveryService CreateService()
    {
        var interpreter = new SourceInterpreter(
            [new CatalogDiscoveryAdapter()],
            NullLogger<SourceInterpreter>.Instance);
        return new DiscoveryService(interpreter);
    }

    private sealed class CatalogDiscoveryAdapter : ISourceAdapter
    {
        public SourceStructureDeclaration Declaration { get; } = new(
            "test.discovery-catalog.v1",
            "catalog");

        public async ValueTask<InterpretedSourceDocument> InterpretAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            CancellationToken cancellationToken = default)
        {
            var root = await XElement
                .LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken)
                .ConfigureAwait(false);
            var values = root.Elements()
                .Select(element => new InterpretedSourceValue(
                    element.Name.LocalName switch
                    {
                        "name" => "Name",
                        "code" => "Code",
                        _ => element.Name.LocalName
                    },
                    element.Value))
                .ToArray();
            return new InterpretedSourceDocument(
                originatingSourceId,
                Declaration.StructureId,
                values);
        }
    }

    private sealed class DiscoveryWorkspace : IDisposable
    {
        private readonly string _testRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR69.Tests");

        public DiscoveryWorkspace()
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
                    "Refusing to delete a Discovery test directory outside its root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }
}
