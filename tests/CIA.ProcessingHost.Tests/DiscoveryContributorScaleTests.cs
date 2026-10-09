using System.Diagnostics;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Repository;
using Microsoft.Data.Sqlite;

namespace CIA.ProcessingHost.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DiscoveryContributorScaleTests
{
    private const int DetailedIdentityCount = 68;
    private const int LogicalFieldCount = 44;
    private const int BatchSize = 1_024;

    [TestMethod]
    [DataRow(1_000)]
    [DataRow(10_000)]
    [DataRow(30_000)]
    public async Task SharedSchemaContributorIndexStaysDurableAndPagesStayBounded(
        int sourceCount)
    {
        using var workspace = new ScaleWorkspace();
        var repository = workspace.Repository;
        var correlation = OperationCorrelation.CreateNew();
        var sourceSetId = SourceSetId.CreateNew();
        var identities = CreateIdentities(sourceSetId);
        var indexDuration = TimeSpan.Zero;

        var stopwatch = Stopwatch.StartNew();
        await repository.BeginDiscoveryIndexAsync(correlation);
        stopwatch.Stop();
        indexDuration += stopwatch.Elapsed;
        for (var start = 0; start < sourceCount; start += BatchSize)
        {
            var count = Math.Min(BatchSize, sourceCount - start);
            var batch = new List<DiscoveryIndexedSource>(count);
            for (var offset = 0; offset < count; offset++)
            {
                var sourceIndex = start + offset;
                var source = new LoadedSourceContract(
                    SourceId.CreateNew(),
                    sourceSetId,
                    Path.Combine(workspace.Path, $"book-{sourceIndex:D5}.xml"),
                    IsIncluded: true,
                    LoadedSourceStatus.Ready,
                    LoadedSourceKind.XmlFile);
                batch.Add(new DiscoveryIndexedSource(
                    source,
                    Path.GetFileName(source.Path),
                    sourceIndex,
                    identities.Select((identity, informationOrdinal) =>
                        new DiscoveryIndexContribution(
                            informationOrdinal,
                            identity,
                            OccurrenceCount: 1,
                            SampleValue: "sample",
                            DefinesInformation: sourceIndex == 0)).ToArray()));
            }

            stopwatch.Restart();
            await repository.AddDiscoverySourcesAsync(correlation.OperationId, batch);
            stopwatch.Stop();
            indexDuration += stopwatch.Elapsed;
        }

        stopwatch.Restart();
        var information = await repository.PublishDiscoveryIndexAsync(
            correlation.OperationId);
        stopwatch.Stop();
        indexDuration += stopwatch.Elapsed;

        Assert.HasCount(DetailedIdentityCount, information);
        Assert.AreEqual(
            LogicalFieldCount,
            information.Select(item => DiscoveryLogicalIdentity.Create(item.Identity))
                .Distinct()
                .Count());
        Assert.IsTrue(information.All(item =>
            item.TotalOccurrenceCount == sourceCount
            && item.LogicalSourceCount == sourceCount));

        var firstLogicalIdentity = DiscoveryLogicalIdentity.Create(information[0].Identity);
        var firstLogicalDetails = information
            .Where(item => DiscoveryLogicalIdentity.Create(item.Identity) == firstLogicalIdentity)
            .Select(item => item.Identity)
            .ToArray();
        var page = await repository.ReadDiscoveryContributorsAsync(
            new DiscoveryContributorPageQuery(
                correlation.OperationId,
                firstLogicalDetails,
                StartIndex: 0,
                PageSize: DiscoveryContributorPaging.MaximumPageSize,
                ExpectedSourceCount: sourceCount));

        Assert.IsNotNull(page);
        Assert.AreEqual(sourceCount, page.TotalSourceCount);
        Assert.HasCount(DiscoveryContributorPaging.MaximumPageSize, page.Sources);
        Assert.IsTrue(page.Sources.All(source =>
            source.OccurrenceCount == firstLogicalDetails.Length));
        TestContext.WriteLine(
            $"{sourceCount:N0}-source durable index write/publish: "
            + $"{indexDuration.TotalMilliseconds:N1} ms; "
            + $"repository: {new FileInfo(repository.DatabasePath).Length:N0} bytes.");
    }

    public TestContext TestContext { get; set; } = null!;

    private static DiscoveryInformationIdentity[] CreateIdentities(SourceSetId sourceSetId)
    {
        return Enumerable.Range(0, DetailedIdentityCount)
            .Select(index => new DiscoveryInformationIdentity(
                sourceSetId,
                $"/book/context-{index:D2}/tag-{index % LogicalFieldCount:D2}",
                $"tag-{index % LogicalFieldCount:D2}"))
            .ToArray();
    }

    private sealed class ScaleWorkspace : IDisposable
    {
        private static readonly string SafeRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR200.Scale.Tests");

        public ScaleWorkspace()
        {
            Path = System.IO.Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Repository = new StructuredInformationRepository(
                ApplicationPaths.FromLocalApplicationData(
                    System.IO.Path.Combine(Path, "LocalAppData")));
        }

        public string Path { get; }

        public StructuredInformationRepository Repository { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(Path))
            {
                return;
            }

            var safePrefix = System.IO.Path.GetFullPath(SafeRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an SPR-200 scale directory outside its root.");
            }

            Directory.Delete(target, recursive: true);
        }
    }
}
