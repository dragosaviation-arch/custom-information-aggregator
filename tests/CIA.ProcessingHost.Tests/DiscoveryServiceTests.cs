using System.Reflection;
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
            "zeta.xml",
            "<catalog><name>Alpha</name><name>Bravo</name><code>A-1</code></catalog>");
        var second = workspace.CreateSource(
            "alpha.xml",
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
        CollectionAssert.AreEqual(
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
        var firstOccurrence = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 1,
            totalOccurrenceCount: 3,
            first,
            localOrdinal: 1,
            expectedSourceOccurrenceCount: 2));
        var secondOccurrence = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 2,
            totalOccurrenceCount: 3,
            first,
            localOrdinal: 2,
            expectedSourceOccurrenceCount: 2));
        var thirdOccurrence = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 3,
            totalOccurrenceCount: 3,
            second,
            localOrdinal: 1,
            expectedSourceOccurrenceCount: 1));

        Assert.IsTrue(discovery.Accepted);
        Assert.AreEqual("Alpha", firstOccurrence.Occurrence?.Value);
        Assert.AreEqual(first.SourceId, firstOccurrence.Occurrence?.SourceId);
        Assert.AreEqual("Bravo", secondOccurrence.Occurrence?.Value);
        Assert.AreEqual(first.SourceId, secondOccurrence.Occurrence?.SourceId);
        Assert.AreEqual("Charlie", thirdOccurrence.Occurrence?.Value);
        Assert.AreEqual(second.SourceId, thirdOccurrence.Occurrence?.SourceId);
        Assert.AreEqual(3, thirdOccurrence.Occurrence?.TotalOccurrenceCount);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            service.GetOccurrenceAsync(CreateLookup(
                correlation.OperationId,
                "Name",
                globalOrdinal: 0,
                totalOccurrenceCount: 3,
                first,
                localOrdinal: 1,
                expectedSourceOccurrenceCount: 2)));
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
        var occurrence = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 2,
            totalOccurrenceCount: 2,
            source,
            localOrdinal: 2,
            expectedSourceOccurrenceCount: 2));

        Assert.IsFalse(normalResponseJson.Contains(onDemandOnlyValue, StringComparison.Ordinal));
        Assert.AreEqual(onDemandOnlyValue, occurrence.Occurrence?.Value);
    }

    [TestMethod]
    public async Task FreshServiceRetrievesOccurrenceWithoutPriorDiscoveryRun()
    {
        using var workspace = new DiscoveryWorkspace();
        var usable = workspace.CreateSource(
            "usable.xml",
            "<catalog><name>Retained value</name></catalog>");
        var lookup = CreateLookup(
            OperationCorrelation.CreateNew().OperationId,
            "Name",
            globalOrdinal: 1,
            totalOccurrenceCount: 1,
            usable,
            localOrdinal: 1,
            expectedSourceOccurrenceCount: 1);

        var occurrence = await CreateService().GetOccurrenceAsync(lookup);

        Assert.IsTrue(occurrence.Accepted);
        Assert.AreEqual("Retained value", occurrence.Occurrence?.Value);
        Assert.AreEqual(usable.SourceId, occurrence.Occurrence?.SourceId);
    }

    [TestMethod]
    public void DiscoveryServiceRetainsOnlyInjectedReaderDependencies()
    {
        var fieldTypes = typeof(DiscoveryService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { typeof(ISourceInterpreter), typeof(ISourceOccurrenceReader) },
            fieldTypes);
    }

    [TestMethod]
    public async Task EachPreviewReadsOnlyTargetSourceWithoutFullInterpretationOrCache()
    {
        using var workspace = new DiscoveryWorkspace();
        var names = workspace.CreateSource(
            "names.xml",
            "<catalog><name>Alpha</name><name>Bravo</name></catalog>");
        var codes = workspace.CreateSource(
            "codes.xml",
            "<catalog><code>A-1</code></catalog>");
        var adapter = new CatalogDiscoveryAdapter();
        var service = CreateService(adapter);
        var correlation = OperationCorrelation.CreateNew();

        var discovery = await service.RunAsync(correlation, [names, codes]);

        Assert.IsTrue(discovery.Accepted);
        Assert.AreEqual(1, adapter.GetInterpretCallCount(names.SourceId));
        Assert.AreEqual(1, adapter.GetInterpretCallCount(codes.SourceId));

        var firstName = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 1,
            totalOccurrenceCount: 2,
            names,
            localOrdinal: 1,
            expectedSourceOccurrenceCount: 2));

        Assert.AreEqual("Alpha", firstName.Occurrence?.Value);
        Assert.AreEqual(1, adapter.GetInterpretCallCount(names.SourceId));
        Assert.AreEqual(1, adapter.GetInterpretCallCount(codes.SourceId));
        Assert.AreEqual(1, adapter.GetOccurrenceCallCount(names.SourceId));
        Assert.AreEqual(0, adapter.GetOccurrenceCallCount(codes.SourceId));

        var secondName = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 2,
            totalOccurrenceCount: 2,
            names,
            localOrdinal: 2,
            expectedSourceOccurrenceCount: 2));

        Assert.AreEqual("Bravo", secondName.Occurrence?.Value);
        Assert.AreEqual(2, adapter.GetOccurrenceCallCount(names.SourceId));
        Assert.AreEqual(0, adapter.GetOccurrenceCallCount(codes.SourceId));
        Assert.AreEqual(1, adapter.GetInterpretCallCount(names.SourceId));
    }

    [TestMethod]
    public async Task ChangedSourceCountReturnsControlledOutOfDateFailure()
    {
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "source.xml",
            "<catalog><name>Alpha</name><name>Bravo</name></catalog>");
        var service = CreateService();
        var correlation = OperationCorrelation.CreateNew();
        var discovery = await service.RunAsync(correlation, [source]);
        File.WriteAllText(source.Path, "<catalog><name>Alpha</name></catalog>");

        var occurrence = await service.GetOccurrenceAsync(CreateLookup(
            correlation.OperationId,
            "Name",
            globalOrdinal: 1,
            totalOccurrenceCount: 2,
            source,
            localOrdinal: 1,
            expectedSourceOccurrenceCount: 2));

        Assert.IsTrue(discovery.Accepted);
        Assert.IsFalse(occurrence.Accepted);
        Assert.AreEqual("discovery-preview-out-of-date", occurrence.Failure?.Code);
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
    public async Task NoUsableMalformedSourceReturnsControlledFailureWithoutDiscoveredRows()
    {
        using var workspace = new DiscoveryWorkspace();
        var malformed = workspace.CreateSource(
            "malformed.xml",
            "<different><value></different>");

        var result = await CreateService().RunAsync(
            OperationCorrelation.CreateNew(),
            [malformed]);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(OperationOutcome.Failed, result.Completion.Outcome);
        Assert.IsEmpty(result.Information);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual("malformed-xml", result.Issues[0].Code);
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

    private static DiscoveryOccurrenceLookup CreateLookup(
        OperationId operationId,
        string informationType,
        int globalOrdinal,
        int totalOccurrenceCount,
        LoadedSourceContract source,
        int localOrdinal,
        int expectedSourceOccurrenceCount)
    {
        return new DiscoveryOccurrenceLookup(
            operationId,
            informationType,
            globalOrdinal,
            totalOccurrenceCount,
            source,
            localOrdinal,
            expectedSourceOccurrenceCount);
    }

    private static DiscoveryService CreateService(CatalogDiscoveryAdapter? adapter = null)
    {
        adapter ??= new CatalogDiscoveryAdapter();
        ISourceAdapter[] adapters = [adapter];
        var interpreter = new SourceInterpreter(
            adapters,
            NullLogger<SourceInterpreter>.Instance);
        var occurrenceReader = new SourceOccurrenceReader(
            adapters,
            NullLogger<SourceOccurrenceReader>.Instance);

        return new DiscoveryService(interpreter, occurrenceReader);
    }

    private sealed class CatalogDiscoveryAdapter : ISourceAdapter, ISourceOccurrenceAdapter
    {
        private readonly Dictionary<SourceId, int> _interpretCallCounts = [];
        private readonly Dictionary<SourceId, int> _occurrenceCallCounts = [];

        public SourceStructureDeclaration Declaration { get; } = new(
            "test.discovery-catalog.v1",
            "catalog");

        public async ValueTask<InterpretedSourceDocument> InterpretAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            CancellationToken cancellationToken = default)
        {
            _interpretCallCounts[originatingSourceId] =
                GetInterpretCallCount(originatingSourceId) + 1;
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

        public async ValueTask<SourceOccurrenceRead> ReadOccurrenceAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            string informationType,
            int localOrdinal,
            CancellationToken cancellationToken = default)
        {
            _occurrenceCallCounts[originatingSourceId] =
                GetOccurrenceCallCount(originatingSourceId) + 1;
            var containingElements = new Stack<string>();
            var occurrenceCount = 0;
            string? requestedValue = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when !reader.IsEmptyElement:
                        containingElements.Push(MapInformationType(reader.LocalName));
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (containingElements.TryPeek(out var currentInformationType)
                            && string.Equals(
                                currentInformationType,
                                informationType,
                                StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(reader.Value))
                        {
                            occurrenceCount++;
                            if (occurrenceCount == localOrdinal)
                            {
                                requestedValue = reader.Value;
                            }
                        }

                        break;

                    case XmlNodeType.EndElement:
                        containingElements.Pop();
                        break;
                }
            }
            while (await reader.ReadAsync().ConfigureAwait(false));

            return new SourceOccurrenceRead(requestedValue, occurrenceCount);
        }

        public int GetInterpretCallCount(SourceId sourceId)
        {
            return _interpretCallCounts.GetValueOrDefault(sourceId);
        }

        public int GetOccurrenceCallCount(SourceId sourceId)
        {
            return _occurrenceCallCounts.GetValueOrDefault(sourceId);
        }

        private static string MapInformationType(string localName)
        {
            return localName switch
            {
                "name" => "Name",
                "code" => "Code",
                _ => localName
            };
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
