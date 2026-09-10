using System.Reflection;
using System.Runtime.CompilerServices;
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
    public async Task DiscoveryKeepsSourceSetsAndStructuralPathsIndependentlyIdentifiable()
    {
        using var workspace = new DiscoveryWorkspace();
        var firstSet = SourceSetId.CreateNew();
        var secondSet = SourceSetId.CreateNew();
        var first = workspace.CreateSource(
            "first.xml",
            "<root><buyer><name>Buyer A</name></buyer><seller><name>Seller A</name></seller></root>",
            firstSet);
        var second = workspace.CreateSource(
            "second.xml",
            "<root><buyer><name>Buyer B</name></buyer></root>",
            secondSet);
        var service = CreateGenericService();
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(correlation, [first, second]);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(3, result.Information);
        var firstBuyer = result.Information.Single(item =>
            item.SourceSetId == firstSet && item.StructuralPath == "/root/buyer/name");
        var firstSeller = result.Information.Single(item =>
            item.SourceSetId == firstSet && item.StructuralPath == "/root/seller/name");
        var secondBuyer = result.Information.Single(item =>
            item.SourceSetId == secondSet && item.StructuralPath == "/root/buyer/name");
        Assert.AreEqual("name", firstBuyer.InformationType);
        Assert.AreEqual("name", firstSeller.InformationType);
        Assert.AreEqual("name", secondBuyer.InformationType);

        var preview = await CreateGenericService().GetOccurrenceAsync(
            new DiscoveryOccurrenceLookup(
                correlation.OperationId,
                firstSeller.Identity,
                GlobalOrdinal: 1,
                TotalOccurrenceCount: 1,
                first,
                LocalOrdinal: 1,
                ExpectedSourceOccurrenceCount: 1));

        Assert.IsTrue(preview.Accepted);
        Assert.AreEqual("Seller A", preview.Occurrence?.Value);
        Assert.AreEqual(firstSeller.Identity, preview.Occurrence?.Identity);
        Assert.AreEqual(first.SourceId, preview.Occurrence?.SourceId);
    }

    [TestMethod]
    public async Task SameNameStructuralRowsRemainIndependentAcrossRepeatedPreviewSwitches()
    {
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "same-name-paths.xml",
            "<root><first><toolnbr>A-100</toolnbr></first>" +
            "<second><toolnbr>B-200</toolnbr></second></root>");
        var service = CreateGenericService();
        var correlation = OperationCorrelation.CreateNew();
        var discovery = await service.RunAsync(correlation, [source]);
        var first = discovery.Information.Single(item =>
            item.StructuralPath == "/root/first/toolnbr");
        var second = discovery.Information.Single(item =>
            item.StructuralPath == "/root/second/toolnbr");

        var firstPreview = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            first.Identity,
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1,
            source,
            LocalOrdinal: 1,
            ExpectedSourceOccurrenceCount: 1));
        var secondPreview = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            second.Identity,
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1,
            source,
            LocalOrdinal: 1,
            ExpectedSourceOccurrenceCount: 1));
        var firstAgain = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            first.Identity,
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1,
            source,
            LocalOrdinal: 1,
            ExpectedSourceOccurrenceCount: 1));

        Assert.AreEqual("toolnbr", first.InformationType);
        Assert.AreEqual("toolnbr", second.InformationType);
        Assert.AreNotEqual(first.Identity, second.Identity);
        Assert.AreEqual("A-100", firstPreview.Occurrence?.Value);
        Assert.AreEqual(first.Identity, firstPreview.Occurrence?.Identity);
        Assert.AreEqual("B-200", secondPreview.Occurrence?.Value);
        Assert.AreEqual(second.Identity, secondPreview.Occurrence?.Identity);
        Assert.AreEqual("A-100", firstAgain.Occurrence?.Value);
        Assert.AreEqual(first.Identity, firstAgain.Occurrence?.Identity);
    }

    [TestMethod]
    public async Task ThreeSourcesAggregateCountsAndDistinctProvenanceInSourceOrder()
    {
        using var workspace = new DiscoveryWorkspace();
        var first = workspace.CreateSource(
            "first/shared.xml",
            "<catalog><name>Alpha</name><name>Bravo</name><code>A-1</code></catalog>");
        var second = workspace.CreateSource(
            "second/shared.xml",
            "<catalog><name>Charlie</name><code>B-2</code></catalog>");
        var third = workspace.CreateSource(
            "third.xml",
            "<catalog><name>Delta</name><category>Reference</category></catalog>");
        var service = CreateService();

        var result = await service.RunAsync(
            OperationCorrelation.CreateNew(),
            [first, second, third]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.IsEmpty(result.Issues);
        Assert.HasCount(3, result.Information);

        var names = result.Information.Single(item => item.InformationType == "Name");
        Assert.AreEqual(4, names.TotalOccurrenceCount);
        Assert.AreEqual("Alpha", names.SampleValue);
        Assert.HasCount(3, names.ContributingSources);
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
        Assert.AreEqual(
            1,
            names.ContributingSources.Single(source => source.SourceId == third.SourceId)
                .OccurrenceCount);
        CollectionAssert.AreEqual(
            new[] { first.SourceId, second.SourceId, third.SourceId },
            names.ContributingSources.Select(source => source.SourceId).ToArray());
        Assert.AreEqual("shared.xml", names.ContributingSources[0].SourceName);
        Assert.AreEqual("shared.xml", names.ContributingSources[1].SourceName);
        CollectionAssert.AreEqual(
            new[] { "Code", "Name", "category" },
            result.Information.Select(item => item.InformationType).ToArray());
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
    public void RunAsyncDoesNotRetainAWorkloadCollectionOfInterpretedDocuments()
    {
        var runAsync = typeof(DiscoveryService).GetMethod(nameof(DiscoveryService.RunAsync))!;
        var stateMachine = runAsync
            .GetCustomAttribute<AsyncStateMachineAttribute>()!
            .StateMachineType;

        var retainsDocumentCollection = stateMachine
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .Any(type => type.IsGenericType
                && type.GetGenericArguments().Contains(typeof(InterpretedSourceDocument)));

        Assert.IsFalse(
            retainsDocumentCollection,
            "Discovery must aggregate each source without retaining a workload-wide interpreted-document collection.");
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
    public async Task ProblematicSourceDoesNotPreventIndependentSourcesFromAggregating()
    {
        using var workspace = new DiscoveryWorkspace();
        var first = workspace.CreateSource(
            "first.xml",
            "<catalog><name>Alpha</name></catalog>");
        var malformed = workspace.CreateSource(
            "malformed.xml",
            "<catalog><name>Broken</catalog>");
        var second = workspace.CreateSource(
            "second.xml",
            "<catalog><name>Bravo</name></catalog>");

        var result = await CreateService().RunAsync(
            OperationCorrelation.CreateNew(),
            [first, malformed, second]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, result.Completion.Outcome);
        Assert.HasCount(1, result.Information);
        Assert.AreEqual(2, result.Information[0].TotalOccurrenceCount);
        CollectionAssert.AreEqual(
            new[] { first.SourceId, second.SourceId },
            result.Information[0].ContributingSources
                .Select(source => source.SourceId)
                .ToArray());
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
        var sourceSetId = SourceSetId.CreateNew();
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
                    new DiscoveryInformationIdentity(
                        sourceSetId,
                        "/catalog/item",
                        "code",
                        SourceValueCandidateKind.Attribute,
                        "/catalog/item/@code"),
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
        Assert.AreEqual("code", roundTripped.Information[0].InformationType);
        Assert.AreEqual(
            SourceValueCandidateKind.Attribute,
            roundTripped.Information[0].Identity.CandidateKind);
        Assert.AreEqual(
            "/catalog/item/@code",
            roundTripped.Information[0].Identity.StructuralIdentity);
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

    private static DiscoveryService CreateGenericService()
    {
        ISourceAdapter[] adapters = [];
        var generic = new GenericXmlElementValueSourceAdapter();
        return new DiscoveryService(
            new SourceInterpreter(
                adapters,
                generic,
                NullLogger<SourceInterpreter>.Instance),
            new SourceOccurrenceReader(
                adapters,
                generic,
                NullLogger<SourceOccurrenceReader>.Instance));
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
            string structuralPath,
            SourceValueCandidateKind candidateKind,
            string structuralIdentity,
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
                            && string.Equals(
                                structuralPath,
                                $"/{informationType}",
                                StringComparison.Ordinal)
                            && candidateKind == SourceValueCandidateKind.Element
                            && string.Equals(
                                structuralIdentity,
                                $"/{informationType}",
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
        private readonly SourceSetId _defaultSourceSetId = SourceSetId.CreateNew();

        public DiscoveryWorkspace()
        {
            Path = System.IO.Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LoadedSourceContract CreateSource(
            string fileName,
            string content,
            SourceSetId? sourceSetId = null)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId ?? _defaultSourceSetId,
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
