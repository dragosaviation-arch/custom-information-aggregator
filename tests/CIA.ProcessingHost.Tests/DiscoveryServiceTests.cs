using System.Text;
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
    public async Task OccurrencesAreRetrievedInStableOrderWithSourceProvenance()
    {
        using var workspace = new DiscoveryWorkspace();
        var first = workspace.CreateSource(
            "first.xml",
            "<catalog><name>Alpha</name><name>Bravo</name></catalog>");
        var second = workspace.CreateSource(
            "second.xml",
            "<catalog><name>Charlie</name></catalog>");
        var service = CreateService();
        var correlation = OperationCorrelation.CreateNew();

        var discovery = await service.RunAsync(correlation, [first, second]);
        var firstOccurrence = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 1);
        var secondOccurrence = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 2);
        var thirdOccurrence = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 3);

        Assert.IsTrue(discovery.Accepted);
        Assert.AreEqual("Alpha", firstOccurrence.Occurrence?.Value);
        Assert.AreEqual(first.SourceId, firstOccurrence.Occurrence?.SourceId);
        Assert.AreEqual("Bravo", secondOccurrence.Occurrence?.Value);
        Assert.AreEqual(first.SourceId, secondOccurrence.Occurrence?.SourceId);
        Assert.AreEqual("Charlie", thirdOccurrence.Occurrence?.Value);
        Assert.AreEqual(second.SourceId, thirdOccurrence.Occurrence?.SourceId);
        Assert.AreEqual(3, thirdOccurrence.Occurrence?.TotalOccurrenceCount);

        var beforeFirst = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 0);
        var afterLast = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 4);

        Assert.IsFalse(beforeFirst.Accepted);
        Assert.AreEqual("occurrence-ordinal-out-of-range", beforeFirst.Failure?.Code);
        Assert.IsFalse(afterLast.Accepted);
        Assert.AreEqual("occurrence-ordinal-out-of-range", afterLast.Failure?.Code);
    }

    [TestMethod]
    public async Task NormalDiscoveryResponseContainsOnlySampleWhileOtherValuesRemainOnDemand()
    {
        const string onDemandOnlyValue = "VALUE_AVAILABLE_ONLY_THROUGH_OCCURRENCE_REQUEST";
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "source.xml",
            $"<catalog><name>Sample</name><name>{onDemandOnlyValue}</name></catalog>");
        var service = CreateService();
        var correlation = OperationCorrelation.CreateNew();
        var result = await service.RunAsync(correlation, [source]);
        var response = new RunDiscoveryResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            CommandAcceptance.Accepted,
            result.Completion,
            result.Information,
            result.Issues,
            Failure: null);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);
        var normalResponseJson = Encoding.UTF8.GetString(
            stream.ToArray().AsSpan(IpcProtocol.FrameHeaderLength));
        var occurrence = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 2);

        Assert.IsFalse(normalResponseJson.Contains(onDemandOnlyValue, StringComparison.Ordinal));
        Assert.AreEqual(onDemandOnlyValue, occurrence.Occurrence?.Value);
    }

    [TestMethod]
    public async Task FailedReRunDoesNotReplaceLastPublishedPreviewBasis()
    {
        using var workspace = new DiscoveryWorkspace();
        var usable = workspace.CreateSource(
            "usable.xml",
            "<catalog><name>Retained value</name></catalog>");
        var unsupported = workspace.CreateSource(
            "unsupported.xml",
            "<different><name>Not published</name></different>");
        var service = CreateService();
        var successfulCorrelation = OperationCorrelation.CreateNew();
        var failedCorrelation = OperationCorrelation.CreateNew();

        var successful = await service.RunAsync(successfulCorrelation, [usable]);
        var failed = await service.RunAsync(failedCorrelation, [unsupported]);
        var retained = await service.GetOccurrenceAsync(
            successfulCorrelation.OperationId,
            "Name",
            1);
        var failedAttempt = await service.GetOccurrenceAsync(
            failedCorrelation.OperationId,
            "Name",
            1);

        Assert.IsTrue(successful.Accepted);
        Assert.IsFalse(failed.Accepted);
        Assert.AreEqual("Retained value", retained.Occurrence?.Value);
        Assert.IsFalse(failedAttempt.Accepted);
        Assert.AreEqual("discovery-result-unavailable", failedAttempt.Failure?.Code);
    }

    [TestMethod]
    public async Task PreviewInterpretsOnlyContributorsAndKeepsOneReplaceableTagIndex()
    {
        using var workspace = new DiscoveryWorkspace();
        var names = workspace.CreateSource(
            "names.xml",
            "<catalog><name>Alpha</name><name>Bravo</name></catalog>");
        var codes = workspace.CreateSource(
            "codes.xml",
            "<catalog><code>A-1</code></catalog>");
        var countingInterpreter = new CountingSourceInterpreter(CreateInterpreter());
        var service = new DiscoveryService(countingInterpreter);
        var correlation = OperationCorrelation.CreateNew();

        var discovery = await service.RunAsync(correlation, [names, codes]);

        Assert.IsTrue(discovery.Accepted);
        Assert.AreEqual(1, countingInterpreter.GetCallCount(names.SourceId));
        Assert.AreEqual(1, countingInterpreter.GetCallCount(codes.SourceId));

        var firstName = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 1);

        Assert.AreEqual("Alpha", firstName.Occurrence?.Value);
        Assert.AreEqual(2, countingInterpreter.GetCallCount(names.SourceId));
        Assert.AreEqual(1, countingInterpreter.GetCallCount(codes.SourceId));

        var secondName = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 2);

        Assert.AreEqual("Bravo", secondName.Occurrence?.Value);
        Assert.AreEqual(2, countingInterpreter.GetCallCount(names.SourceId));
        Assert.AreEqual(1, countingInterpreter.GetCallCount(codes.SourceId));

        var code = await service.GetOccurrenceAsync(correlation.OperationId, "Code", 1);

        Assert.AreEqual("A-1", code.Occurrence?.Value);
        Assert.AreEqual(2, countingInterpreter.GetCallCount(names.SourceId));
        Assert.AreEqual(2, countingInterpreter.GetCallCount(codes.SourceId));

        var rebuiltName = await service.GetOccurrenceAsync(correlation.OperationId, "Name", 1);

        Assert.AreEqual("Alpha", rebuiltName.Occurrence?.Value);
        Assert.AreEqual(3, countingInterpreter.GetCallCount(names.SourceId));
        Assert.AreEqual(2, countingInterpreter.GetCallCount(codes.SourceId));
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
        return new DiscoveryService(CreateInterpreter());
    }

    private static SourceInterpreter CreateInterpreter()
    {
        return new SourceInterpreter(
            [new CatalogDiscoveryAdapter()],
            NullLogger<SourceInterpreter>.Instance);
    }

    private sealed class CountingSourceInterpreter(ISourceInterpreter inner) : ISourceInterpreter
    {
        private readonly Dictionary<SourceId, int> _callCounts = [];

        public async Task<SourceInterpretationResult> InterpretAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            _callCounts[source.SourceId] = GetCallCount(source.SourceId) + 1;
            return await inner.InterpretAsync(source, cancellationToken);
        }

        public int GetCallCount(SourceId sourceId)
        {
            return _callCounts.GetValueOrDefault(sourceId);
        }
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
