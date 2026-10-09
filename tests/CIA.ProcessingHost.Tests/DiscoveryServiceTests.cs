using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.ProcessingHost.Discovery;
using CIA.ProcessingHost.Repository;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class DiscoveryServiceTests
{
    [TestMethod]
    public async Task LargeMultilineCdataUsesBoundedSampleWhileOccurrenceRemainsExact()
    {
        var fullValue = "\r\n<style>\n  .review { color: red; }\t\n</style>\n"
            + string.Join("\r\n", Enumerable.Repeat("<div>Long review widget content</div>", 40));
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "goodbooks-like.xml",
            $"<book><reviews_widget><![CDATA[{fullValue}]]></reviews_widget></book>");
        var service = CreateGenericService(workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(correlation, [source]);
        var information = result.Information.Single(item =>
            item.InformationType == "reviews_widget");
        var occurrence = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            information.Identity,
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1,
            source,
            LocalOrdinal: 1,
            ExpectedSourceOccurrenceCount: 1));

        Assert.IsTrue(result.Accepted);
        Assert.IsLessThanOrEqualTo(
            DiscoverySampleValueFormatter.MaximumLength,
            information.SampleValue.Length);
        Assert.IsFalse(information.SampleValue.Any(
            character => character is '\r' or '\n' or '\t'));
        Assert.EndsWith("…", information.SampleValue);
        Assert.IsTrue(occurrence.Accepted);
        Assert.AreEqual(
            fullValue.Replace("\r\n", "\n", StringComparison.Ordinal),
            occurrence.Occurrence?.Value);
    }

    [TestMethod]
    public async Task TenThousandSourcesAggregateAndNavigateWithoutOccurrenceRetention()
    {
        const int sourceCount = 10_000;
        using var workspace = new DiscoveryWorkspace();
        var sourceSetId = SourceSetId.CreateNew();
        var sources = Enumerable.Range(1, sourceCount)
            .Select(index => new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId,
                Path.GetFullPath($"synthetic/book-{index:D5}.xml"),
                IsIncluded: true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile))
            .ToArray();
        var service = new DiscoveryService(
            new SyntheticScaleInterpreter(),
            new SyntheticScaleOccurrenceReader(),
            workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(correlation, sources);
        var title = result.Information.Single(item => item.InformationType == "title");
        var preview = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            title.Identity,
            GlobalOrdinal: sourceCount,
            TotalOccurrenceCount: sourceCount,
            sources[^1],
            LocalOrdinal: 1,
            ExpectedSourceOccurrenceCount: 1));

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(3, result.Information);
        Assert.AreEqual(sourceCount, title.TotalOccurrenceCount);
        Assert.AreEqual(sourceCount, title.LogicalSourceCount);
        Assert.IsNull(typeof(DiscoveredInformation).GetProperty("ContributingSources"));
        var contributorPage = await service.GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                correlation.OperationId,
                [title.Identity],
                StartIndex: 0,
                PageSize: DiscoveryContributorPaging.MaximumPageSize,
                ExpectedSourceCount: sourceCount));
        Assert.IsTrue(contributorPage.Accepted);
        Assert.AreEqual(sourceCount, contributorPage.Page?.TotalSourceCount);
        Assert.HasCount(
            DiscoveryContributorPaging.MaximumPageSize,
            contributorPage.Page!.Sources);
        Assert.IsTrue(preview.Accepted);
        Assert.AreEqual(sourceCount, preview.Occurrence?.Ordinal);
        Assert.AreEqual(sources[^1].SourceId, preview.Occurrence?.SourceId);
        Assert.AreEqual(Path.GetFileNameWithoutExtension(sources[^1].Path), preview.Occurrence?.Value);
    }

    [TestMethod]
    public async Task DiscoveryReportsActualCompletedSourceUnitsMonotonically()
    {
        using var workspace = new DiscoveryWorkspace();
        var first = workspace.CreateSource("first.xml", "<root><value>A</value></root>");
        var second = workspace.CreateSource("second.xml", "<root><value>B</value></root>");
        var progress = new RecordingProgress<DiscoveryProgressSnapshot>();

        var result = await CreateGenericService(workspace.Repository).RunAsync(
            OperationCorrelation.CreateNew(),
            [first, second],
            progress);

        Assert.IsTrue(result.Accepted);
        CollectionAssert.AreEqual(
            new[]
            {
                new DiscoveryProgressSnapshot(1, 2),
                new DiscoveryProgressSnapshot(2, 2)
            },
            progress.Values.ToArray());
    }

    [TestMethod]
    public async Task LogicalSourceUnionContributorPagingAndGlobalOrdinalsUseDurableIndex()
    {
        using var workspace = new DiscoveryWorkspace();
        var sources = new[]
        {
            workspace.CreateSource("source-1.xml", "<unused />"),
            workspace.CreateSource("source-2.xml", "<unused />"),
            workspace.CreateSource("source-3.xml", "<unused />"),
            workspace.CreateSource("source-4.xml", "<unused />")
        };
        var service = new DiscoveryService(
            new OverlappingLogicalInterpreter(),
            new OverlappingLogicalOccurrenceReader(),
            workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(correlation, sources);
        var logicalDetails = result.Information
            .Where(item => item.InformationType == "name")
            .ToArray();

        Assert.HasCount(2, logicalDetails);
        Assert.IsTrue(logicalDetails.All(item => item.LogicalSourceCount == 4));
        Assert.AreEqual(6, logicalDetails.Sum(item => item.TotalOccurrenceCount));

        var firstPage = await service.GetContributorsAsync(new DiscoveryContributorPageQuery(
            correlation.OperationId,
            logicalDetails.Select(item => item.Identity).ToArray(),
            StartIndex: 0,
            PageSize: 2,
            ExpectedSourceCount: 4));
        var secondPage = await service.GetContributorsAsync(new DiscoveryContributorPageQuery(
            correlation.OperationId,
            logicalDetails.Select(item => item.Identity).ToArray(),
            StartIndex: 2,
            PageSize: 2,
            ExpectedSourceCount: 4));

        Assert.IsTrue(firstPage.Accepted);
        Assert.IsTrue(secondPage.Accepted);
        CollectionAssert.AreEqual(
            sources.Select(source => source.SourceId).ToArray(),
            firstPage.Page!.Sources.Concat(secondPage.Page!.Sources)
                .Select(item => item.SourceId)
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 2, 1 },
            firstPage.Page.Sources.Concat(secondPage.Page.Sources)
                .Select(item => item.OccurrenceCount)
                .ToArray());

        var fourth = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            logicalDetails.Select(item => item.Identity).ToArray(),
            GlobalOrdinal: 4,
            TotalOccurrenceCount: 6));
        var sixthFromFreshService = await new DiscoveryService(
            new OverlappingLogicalInterpreter(),
            new OverlappingLogicalOccurrenceReader(),
            workspace.Repository).GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
                correlation.OperationId,
                logicalDetails.Select(item => item.Identity).ToArray(),
                GlobalOrdinal: 6,
                TotalOccurrenceCount: 6));

        Assert.AreEqual(sources[1].SourceId, fourth.Occurrence?.SourceId);
        Assert.AreEqual("second:source-2", fourth.Occurrence?.Value);
        Assert.AreEqual(sources[3].SourceId, sixthFromFreshService.Occurrence?.SourceId);
        Assert.AreEqual("second:source-4", sixthFromFreshService.Occurrence?.Value);
    }

    [TestMethod]
    public async Task PublishedIndexSurvivesFailedRerunAndIsInvalidatedBySuccessfulReplacement()
    {
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource("source-1.xml", "<unused />");
        var service = new DiscoveryService(
            new OverlappingLogicalInterpreter(),
            new OverlappingLogicalOccurrenceReader(),
            workspace.Repository);
        var firstCorrelation = OperationCorrelation.CreateNew();
        var first = await service.RunAsync(firstCorrelation, [source]);
        var firstLookup = new DiscoveryOccurrenceLookup(
            firstCorrelation.OperationId,
            first.Information.Select(item => item.Identity).ToArray(),
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1);

        var malformed = workspace.CreateSource("malformed.xml", "<root><value></root>");
        var failed = await CreateGenericService(workspace.Repository).RunAsync(
            OperationCorrelation.CreateNew(),
            [malformed]);
        var retained = await service.GetOccurrenceAsync(firstLookup);
        var retainedContributors = await service.GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                firstCorrelation.OperationId,
                first.Information.Select(item => item.Identity).ToArray(),
                0,
                1,
                1));

        Assert.IsFalse(failed.Accepted);
        Assert.IsTrue(retained.Accepted);
        Assert.IsTrue(retainedContributors.Accepted);

        var replacementCorrelation = OperationCorrelation.CreateNew();
        var replacement = await service.RunAsync(replacementCorrelation, [source]);
        var staleOccurrence = await service.GetOccurrenceAsync(firstLookup);
        var staleContributors = await service.GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                firstCorrelation.OperationId,
                first.Information.Select(item => item.Identity).ToArray(),
                0,
                1,
                1));

        Assert.IsTrue(replacement.Accepted);
        Assert.IsFalse(staleOccurrence.Accepted);
        Assert.AreEqual("discovery-preview-out-of-date", staleOccurrence.Failure?.Code);
        Assert.IsFalse(staleContributors.Accepted);
        Assert.AreEqual("discovery-contributors-out-of-date", staleContributors.Failure?.Code);
    }

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
        var service = CreateGenericService(workspace.Repository);
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

        var preview = await CreateGenericService(workspace.Repository).GetOccurrenceAsync(
            new DiscoveryOccurrenceLookup(
                correlation.OperationId,
                [firstBuyer.Identity, firstSeller.Identity],
                GlobalOrdinal: 2,
                TotalOccurrenceCount: 2));

        Assert.IsTrue(preview.Accepted);
        Assert.AreEqual("Seller A", preview.Occurrence?.Value);
        Assert.AreEqual(firstSeller.Identity, preview.Occurrence?.Identity);
        Assert.AreEqual(first.SourceId, preview.Occurrence?.SourceId);
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }

    [TestMethod]
    public async Task SameNameStructuralRowsRemainIndependentAcrossRepeatedPreviewSwitches()
    {
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "same-name-paths.xml",
            "<root><first><toolnbr>A-100</toolnbr></first>" +
            "<second><toolnbr>B-200</toolnbr></second></root>");
        var service = CreateGenericService(workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();
        var discovery = await service.RunAsync(correlation, [source]);
        var first = discovery.Information.Single(item =>
            item.StructuralPath == "/root/first/toolnbr");
        var second = discovery.Information.Single(item =>
            item.StructuralPath == "/root/second/toolnbr");

        var firstPreview = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            [first.Identity, second.Identity],
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 2));
        var secondPreview = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            [first.Identity, second.Identity],
            GlobalOrdinal: 2,
            TotalOccurrenceCount: 2));
        var firstAgain = await service.GetOccurrenceAsync(new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            [first.Identity, second.Identity],
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 2));

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
    public async Task MultiPathElementPreviewIgnoresEmptySiblingsAndRetainsDetailedIdentity()
    {
        using var workspace = new DiscoveryWorkspace();
        var source = workspace.CreateSource(
            "multi-path-empty-elements.xml",
            "<root><application><comment>First</comment></application>"
            + "<alternatives><alternative><comment>Second</comment></alternative>"
            + "<alternative><comment /></alternative></alternatives>"
            + "<other><branch><comment /></branch></other></root>");
        var service = CreateGenericService(workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();

        var discovery = await service.RunAsync(correlation, [source]);
        var comments = discovery.Information
            .Where(item => item.InformationType == "comment"
                && item.Identity.CandidateKind == SourceValueCandidateKind.Element)
            .OrderBy(item => item.Identity.StructuralPath, StringComparer.Ordinal)
            .ToArray();

        Assert.IsTrue(discovery.Accepted);
        Assert.HasCount(2, comments);
        Assert.AreEqual(2, comments.Sum(item => item.TotalOccurrenceCount));

        for (var index = 0; index < comments.Length; index++)
        {
            var member = comments[index];
            var preview = await service.GetOccurrenceAsync(
                new DiscoveryOccurrenceLookup(
                    correlation.OperationId,
                    comments.Select(item => item.Identity).ToArray(),
                    GlobalOrdinal: index + 1,
                    TotalOccurrenceCount: 2));

            Assert.IsTrue(preview.Accepted);
            Assert.AreEqual(member.Identity, preview.Occurrence?.Identity);
            Assert.AreEqual(index + 1, preview.Occurrence?.Ordinal);
            Assert.AreEqual(source.SourceId, preview.Occurrence?.SourceId);
        }
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
        var service = CreateService(workspace.Repository);
        var correlation = OperationCorrelation.CreateNew();

        var result = await service.RunAsync(
            correlation,
            [first, second, third]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, result.Completion.Outcome);
        Assert.IsEmpty(result.Issues);
        Assert.HasCount(3, result.Information);

        var names = result.Information.Single(item => item.InformationType == "Name");
        Assert.AreEqual(4, names.TotalOccurrenceCount);
        Assert.AreEqual("Alpha", names.SampleValue);
        Assert.AreEqual(3, names.LogicalSourceCount);
        var contributorResult = await service.GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                correlation.OperationId,
                [names.Identity],
                StartIndex: 0,
                PageSize: DiscoveryContributorPaging.DefaultPageSize,
                ExpectedSourceCount: 3));
        Assert.IsTrue(contributorResult.Accepted);
        var contributors = contributorResult.Page!.Sources;
        Assert.HasCount(3, contributors);
        Assert.AreEqual(
            names.TotalOccurrenceCount,
            contributors.Sum(source => source.OccurrenceCount));
        Assert.AreEqual(
            2,
            contributors.Single(source => source.SourceId == first.SourceId)
                .OccurrenceCount);
        Assert.AreEqual(
            1,
            contributors.Single(source => source.SourceId == second.SourceId)
                .OccurrenceCount);
        Assert.AreEqual(
            1,
            contributors.Single(source => source.SourceId == third.SourceId)
                .OccurrenceCount);
        CollectionAssert.AreEqual(
            new[] { first.SourceId, second.SourceId, third.SourceId },
            contributors.Select(source => source.SourceId).ToArray());
        Assert.AreEqual("shared.xml", contributors[0].SourceName);
        Assert.AreEqual("shared.xml", contributors[1].SourceName);
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
        var service = CreateService(workspace.Repository);
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
        var service = CreateService(workspace.Repository);
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
    [TestCategory("AlphaRegressionGate")]
    public async Task FreshServiceRetrievesOccurrenceFromDurablePublishedDiscoveryIndex()
    {
        using var workspace = new DiscoveryWorkspace();
        var usable = workspace.CreateSource(
            "usable.xml",
            "<catalog><name>Retained value</name></catalog>");
        var correlation = OperationCorrelation.CreateNew();
        var discovery = await CreateService(workspace.Repository).RunAsync(
            correlation,
            [usable]);
        var name = discovery.Information.Single();
        var lookup = new DiscoveryOccurrenceLookup(
            correlation.OperationId,
            [name.Identity],
            GlobalOrdinal: 1,
            TotalOccurrenceCount: 1);

        var occurrence = await CreateService(workspace.Repository).GetOccurrenceAsync(lookup);

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
            new[]
            {
                typeof(ISourceInterpreter),
                typeof(ISourceOccurrenceReader),
                typeof(StructuredInformationRepository)
            },
            fieldTypes);
    }

    [TestMethod]
    public void RunAsyncDoesNotRetainAWorkloadCollectionOfInterpretedDocuments()
    {
        var runAsync = typeof(DiscoveryService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(DiscoveryService.RunAsync)
                              && method.GetParameters().Length == 4);
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
        var service = CreateService(workspace.Repository, adapter);
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
        var service = CreateService(workspace.Repository);
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
    [TestCategory("AlphaRegressionGate")]
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

        var result = await CreateService(workspace.Repository).RunAsync(
            OperationCorrelation.CreateNew(),
            [first, malformed, second]);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, result.Completion.Outcome);
        Assert.HasCount(1, result.Information);
        Assert.AreEqual(2, result.Information[0].TotalOccurrenceCount);
        Assert.AreEqual(2, result.Information[0].LogicalSourceCount);
        var contributors = await CreateService(workspace.Repository).GetContributorsAsync(
            new DiscoveryContributorPageQuery(
                result.Completion.Correlation.OperationId,
                [result.Information[0].Identity],
                StartIndex: 0,
                PageSize: DiscoveryContributorPaging.DefaultPageSize,
                ExpectedSourceCount: 2));
        CollectionAssert.AreEqual(
            new[] { first.SourceId, second.SourceId },
            contributors.Page!.Sources
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

        var result = await CreateService(workspace.Repository).RunAsync(
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
        Assert.AreEqual(1, roundTripped.Information[0].LogicalSourceCount);

        var invalid = response with
        {
            Information =
            [
                new DiscoveredInformation(
                    response.Information[0].Identity,
                    3,
                    LogicalSourceCount: 0,
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

    private static DiscoveryService CreateService(
        StructuredInformationRepository repository,
        CatalogDiscoveryAdapter? adapter = null)
    {
        adapter ??= new CatalogDiscoveryAdapter();
        ISourceAdapter[] adapters = [adapter];
        var interpreter = new SourceInterpreter(
            adapters,
            NullLogger<SourceInterpreter>.Instance);
        var occurrenceReader = new SourceOccurrenceReader(
            adapters,
            NullLogger<SourceOccurrenceReader>.Instance);

        return new DiscoveryService(interpreter, occurrenceReader, repository);
    }

    private static DiscoveryService CreateGenericService(
        StructuredInformationRepository repository)
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
                NullLogger<SourceOccurrenceReader>.Instance),
            repository);
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

    private sealed class SyntheticScaleInterpreter : ISourceInterpreter
    {
        public Task<SourceInterpretationResult> InterpretAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SourceInterpretationResult.Usable(
                new InterpretedSourceDocument(
                    source.SourceId,
                    "synthetic.goodbooks-scale.v1",
                    [
                        new InterpretedSourceValue("title", "Synthetic title"),
                        new InterpretedSourceValue("authors", "Synthetic author"),
                        new InterpretedSourceValue("rating", "4.0")
                    ])));
        }
    }

    private sealed class OverlappingLogicalInterpreter : ISourceInterpreter
    {
        public Task<SourceInterpretationResult> InterpretAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceNumber = Path.GetFileNameWithoutExtension(source.Path)[^1] - '0';
            var values = new List<InterpretedSourceValue>();
            if (sourceNumber <= 3)
            {
                values.Add(CreateValue(source, "first", $"first:source-{sourceNumber}", 1));
            }

            if (sourceNumber >= 2)
            {
                values.Add(CreateValue(source, "second", $"second:source-{sourceNumber}", 2));
            }

            return Task.FromResult(SourceInterpretationResult.Usable(
                new InterpretedSourceDocument(
                    source.SourceId,
                    "synthetic.overlap.v1",
                    values)));
        }

        private static InterpretedSourceValue CreateValue(
            LoadedSourceContract source,
            string context,
            string value,
            long instanceOffset)
        {
            var lineage = new SourceValueLineage(
                source.SourceId,
                [
                    new SourceElementInstance("root", "", "root", 1),
                    new SourceElementInstance(context, "", context, 1 + instanceOffset),
                    new SourceElementInstance("name", "", "name", 10 + instanceOffset)
                ],
                traversalOrder: instanceOffset);
            return new InterpretedSourceValue("name", value, lineage);
        }
    }

    private sealed class OverlappingLogicalOccurrenceReader : ISourceOccurrenceReader
    {
        public Task<SourceOccurrenceReadResult> ReadAsync(
            LoadedSourceContract source,
            string informationType,
            string structuralPath,
            SourceValueCandidateKind candidateKind,
            string structuralIdentity,
            int localOrdinal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceNumber = Path.GetFileNameWithoutExtension(source.Path)[^1] - '0';
            var context = structuralPath.Contains("/first/", StringComparison.Ordinal)
                ? "first"
                : "second";
            var exists = context == "first" ? sourceNumber <= 3 : sourceNumber >= 2;
            return Task.FromResult(SourceOccurrenceReadResult.Accept(
                exists && localOrdinal == 1 ? $"{context}:source-{sourceNumber}" : null,
                exists ? 1 : 0));
        }
    }

    private sealed class SyntheticScaleOccurrenceReader : ISourceOccurrenceReader
    {
        public Task<SourceOccurrenceReadResult> ReadAsync(
            LoadedSourceContract source,
            string informationType,
            string structuralPath,
            SourceValueCandidateKind candidateKind,
            string structuralIdentity,
            int localOrdinal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SourceOccurrenceReadResult.Accept(
                Path.GetFileNameWithoutExtension(source.Path),
                actualOccurrenceCount: 1));
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
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(
                    System.IO.Path.Combine(Path, "LocalAppData")));
        }

        public string Path { get; }

        public StructuredInformationRepository Repository { get; }

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

            SqliteConnection.ClearAllPools();
            Directory.Delete(target, recursive: true);
        }
    }
}
