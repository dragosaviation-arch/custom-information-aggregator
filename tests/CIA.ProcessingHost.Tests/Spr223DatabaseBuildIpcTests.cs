using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Diagnostics;
using CIA.Core.Runtime;
using CIA.ProcessingHost.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CIA.ProcessingHost.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Spr223DatabaseBuildIpcTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ArchiveBackedSiblingOutputSetsSurviveProductionJsonRoundTrip()
    {
        var parentSourceSetId = SourceSetId.CreateNew();
        var first = CreateArchiveSource(parentSourceSetId);
        var second = CloneWithSeparateLineage(first);
        var command = new BuildDatabaseCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            OperationCorrelation.CreateNew(),
            CreateSpecification([first], [second]));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        stream.Position = 0;
        var roundTripped = Assert.IsInstanceOfType<BuildDatabaseCommand>(
            await LengthPrefixedJsonMessageFramer.ReadAsync(stream));

        Assert.HasCount(2, roundTripped.Specification.Datasets);
        var roundTrippedSources = roundTripped.Specification.Datasets
            .Select(dataset => dataset.Sources.Single())
            .ToArray();
        var firstProvenance = roundTrippedSources[0].ArchiveProvenance;
        var secondProvenance = roundTrippedSources[1].ArchiveProvenance;
        Assert.IsNotNull(firstProvenance);
        Assert.IsNotNull(secondProvenance);
        Assert.AreEqual(first.SourceId, roundTrippedSources[0].SourceId);
        Assert.AreEqual(first.SourceId, roundTrippedSources[1].SourceId);
        Assert.AreNotSame(firstProvenance.ArchiveLineage, secondProvenance.ArchiveLineage);
        CollectionAssert.AreEqual(
            firstProvenance.ArchiveLineage.ToArray(),
            secondProvenance.ArchiveLineage.ToArray());
    }

    [TestMethod]
    public void EquivalentLineageCollectionsPassWhileChangedBoundariesAndOrderingFail()
    {
        var parentSourceSetId = SourceSetId.CreateNew();
        var source = CreateArchiveSource(parentSourceSetId);
        _ = CreateSpecification([source], [CloneWithSeparateLineage(source)]);

        var provenance = source.ArchiveProvenance!;
        LoadedSourceContract[] changedSources =
        [
            source with { SourceId = SourceId.CreateNew() },
            source with { Path = Path.Combine(provenance.ExtractionRoot, "different.xml") },
            source with { Kind = LoadedSourceKind.Archive, ArchiveProvenance = null },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    OriginalArchiveSourceId = SourceId.CreateNew()
                }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    OriginalArchivePath = Path.GetFullPath("different.zip")
                }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    ArchiveLineage =
                    [
                        provenance.ArchiveLineage[0],
                        provenance.ArchiveLineage[1] with { Path = "different.zip" }
                    ]
                }
            },
            source with
            {
                ArchiveProvenance = provenance with { ArchiveNestingLevel = 3 }
            },
            source with
            {
                ArchiveProvenance = provenance with { ArchiveMemberPath = "different.xml" }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    ExtractionRoot = Path.GetFullPath("different-extraction")
                }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    Retention = ArchiveExtractionRetention.Persistent,
                    PersistentExtractionDirectory = Path.GetTempPath()
                }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    MaximumArchiveNestingDepth = ArchiveNestingDepth.From(4)
                }
            },
            source with
            {
                ArchiveProvenance = provenance with
                {
                    PersistentExtractionDirectory = Path.GetFullPath("persistent")
                }
            }
        ];

        foreach (var changed in changedSources)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateSpecification([source], [changed]));
        }

        var secondSource = CreateArchiveSource(parentSourceSetId);
        Assert.ThrowsExactly<ArgumentException>(
            () => CreateSpecification(
                [source, secondSource],
                [CloneWithSeparateLineage(secondSource), CloneWithSeparateLineage(source)]));
    }

    [TestMethod]
    public async Task DirectXmlAndSingleArchiveOutputSetRemainValidControls()
    {
        var parentSourceSetId = SourceSetId.CreateNew();
        var direct = CreateDirectSource(parentSourceSetId, Path.GetFullPath("direct.xml"));
        var directSpecification = CreateSpecification(
            [direct],
            [direct with { ArchiveProvenance = null }]);
        var archiveSpecification = CreateSpecification([CreateArchiveSource(parentSourceSetId)]);

        await RoundTripAsync(directSpecification);
        await RoundTripAsync(archiveSpecification);
    }

    [TestMethod]
    public async Task InvalidBuildContractReceivesCorrelatedRejectionAndHostAcceptsNextBuild()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR223.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            var pipeName = $"CIA.Tests.SPR223.{Guid.NewGuid():N}";
            string[] arguments =
            [
                $"--{ProcessingHostRuntimeContract.PipeNameConfigurationKey}={pipeName}",
                $"--{ProcessingHostRuntimeContract.ParentProcessIdConfigurationKey}={Environment.ProcessId}",
                $"--{ApplicationLogPaths.DirectoryConfigurationKey}={workingDirectory}",
                $"--{ApplicationPaths.LocalApplicationDataDirectoryConfigurationKey}="
                    + Path.Combine(workingDirectory, "LocalAppData")
            ];
            using var host = ProcessingHostApplicationHost.Create(arguments);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync(timeout.Token);

            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await CompleteReadinessHandshakeAsync(pipe, timeout.Token);

            var sourcePath = Path.Combine(workingDirectory, "source.xml");
            await File.WriteAllTextAsync(
                sourcePath,
                "<root><value>accepted</value></root>",
                timeout.Token);
            var parentSourceSetId = SourceSetId.CreateNew();
            var source = CreateDirectSource(parentSourceSetId, sourcePath);
            var invalidCommand = new BuildDatabaseCommand(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                OperationCorrelation.CreateNew(),
                CreateSpecification([source], [source]));
            await WriteBuildWithChangedSecondSourcePathAsync(
                pipe,
                invalidCommand,
                Path.Combine(workingDirectory, "different.xml"),
                timeout.Token);

            var rejection = Assert.IsInstanceOfType<CommandAcknowledgement>(
                await LengthPrefixedJsonMessageFramer.ReadAsync(pipe, timeout.Token));
            Assert.AreEqual(invalidCommand.MessageId, rejection.CommandMessageId);
            Assert.AreEqual(CommandAcceptance.Rejected, rejection.Acceptance);
            Assert.AreEqual("invalid-contract", rejection.Failure?.Code);
            Assert.IsFalse(lifetime.ApplicationStopping.IsCancellationRequested);

            var validCommand = new BuildDatabaseCommand(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                OperationCorrelation.CreateNew(),
                CreateSpecification([source]));
            await LengthPrefixedJsonMessageFramer.WriteAsync(pipe, validCommand, timeout.Token);
            BuildDatabaseResponse? response = null;
            while (response is null)
            {
                var message = await LengthPrefixedJsonMessageFramer.ReadAsync(pipe, timeout.Token);
                response = message as BuildDatabaseResponse;
            }

            Assert.AreEqual(validCommand.MessageId, response.CommandMessageId);
            Assert.AreEqual(
                CommandAcceptance.Accepted,
                response.Acceptance,
                $"{response.Failure?.Code}: {response.Failure?.Description}");
            Assert.IsNotNull(response.PublishedGeneration);
            Assert.IsFalse(lifetime.ApplicationStopping.IsCancellationRequested);

            var stop = new StopProcessingHostCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow);
            await LengthPrefixedJsonMessageFramer.WriteAsync(pipe, stop, timeout.Token);
            var stopAcknowledgement = Assert.IsInstanceOfType<CommandAcknowledgement>(
                await LengthPrefixedJsonMessageFramer.ReadAsync(pipe, timeout.Token));
            Assert.AreEqual(CommandAcceptance.Accepted, stopAcknowledgement.Acceptance);
            await WaitForCancellationAsync(lifetime.ApplicationStopping, timeout.Token);
            await host.StopAsync(timeout.Token);
            host.Dispose();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task RoundTripAsync(DatabaseBuildSpecification specification)
    {
        var command = new BuildDatabaseCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            OperationCorrelation.CreateNew(),
            specification);
        await using var stream = new MemoryStream();
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        stream.Position = 0;
        var roundTripped = Assert.IsInstanceOfType<BuildDatabaseCommand>(
            await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        Assert.HasCount(specification.Datasets.Count, roundTripped.Specification.Datasets);
    }

    private static DatabaseBuildSpecification CreateSpecification(
        params IReadOnlyList<LoadedSourceContract>[] sourceBoundaries)
    {
        var datasets = sourceBoundaries.Select((sources, index) =>
        {
            var parentSourceSetId = sources[0].SourceSetId;
            var familyId = new RowFamilyId(
                parentSourceSetId,
                RowFamilyKind.Root,
                sourceBoundaries.Length == 1 ? "/root" : $"/root-{index + 1}");
            var identity = new DiscoveryInformationIdentity(
                parentSourceSetId,
                $"{familyId.AnchorPath}/value",
                "value",
                SourceValueCandidateKind.Element,
                $"{familyId.AnchorPath}/value");
            return new DatabaseDatasetBuildSpecification(
                OutputSetId.CreateNew(),
                parentSourceSetId,
                $"Output {index + 1}",
                index + 1,
                RepeatedDataLayout.StructuralRows,
                sources,
                [new RowFamilyDefinition(familyId, null)],
                [new DatabaseFieldMapping(familyId, "Value", false, [identity])]);
        }).ToArray();
        return new DatabaseBuildSpecification(datasets);
    }

    private static LoadedSourceContract CreateArchiveSource(SourceSetId parentSourceSetId)
    {
        var root = Path.Combine(Path.GetTempPath(), "CIA.SPR223.Archive", Guid.NewGuid().ToString("N"));
        var originalArchiveSourceId = SourceId.CreateNew();
        var originalArchivePath = Path.Combine(root, "books.zip");
        var extractionRoot = Path.Combine(root, "extracted");
        return CreateDirectSource(parentSourceSetId, Path.Combine(extractionRoot, "book.xml")) with
        {
            ArchiveProvenance = new ArchiveSourceProvenance(
                originalArchiveSourceId,
                originalArchivePath,
                [
                    new ArchiveLineageItem(originalArchiveSourceId, originalArchivePath, 1),
                    new ArchiveLineageItem(SourceId.CreateNew(), "nested.zip", 2)
                ],
                ArchiveNestingLevel: 2,
                ArchiveMemberPath: "book.xml",
                extractionRoot,
                ArchiveExtractionRetention.ManagedTemporary,
                ArchiveNestingDepth.Default,
                PersistentExtractionDirectory: null)
        };
    }

    private static LoadedSourceContract CloneWithSeparateLineage(LoadedSourceContract source)
    {
        var provenance = source.ArchiveProvenance;
        return source with
        {
            ArchiveProvenance = provenance is null
                ? null
                : provenance with
                {
                    ArchiveLineage = provenance.ArchiveLineage
                        .Select(item => item with { })
                        .ToArray()
                }
        };
    }

    private static LoadedSourceContract CreateDirectSource(
        SourceSetId parentSourceSetId,
        string path) => new(
            SourceId.CreateNew(),
            parentSourceSetId,
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);

    private static async Task CompleteReadinessHandshakeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var command = new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command, cancellationToken);
        var acknowledgement = Assert.IsInstanceOfType<CommandAcknowledgement>(
            await LengthPrefixedJsonMessageFramer.ReadAsync(stream, cancellationToken));
        Assert.AreEqual(command.MessageId, acknowledgement.CommandMessageId);
        var availability = Assert.IsInstanceOfType<ProcessingHostAvailabilityEvent>(
            await LengthPrefixedJsonMessageFramer.ReadAsync(stream, cancellationToken));
        Assert.AreEqual(ProcessingHostAvailability.Ready, availability.Availability);
    }

    private static async Task WriteBuildWithChangedSecondSourcePathAsync(
        Stream destination,
        BuildDatabaseCommand command,
        string changedPath,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await LengthPrefixedJsonMessageFramer.WriteAsync(buffer, command, cancellationToken);
        var framed = buffer.ToArray();
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(framed.AsSpan(0, 4));
        var root = JsonNode.Parse(framed.AsSpan(4, payloadLength))!;
        root["specification"]!["datasets"]![1]!["sources"]![0]!["path"] = changedPath;
        var payload = Encoding.UTF8.GetBytes(root.ToJsonString());
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await destination.WriteAsync(header, cancellationToken);
        await destination.WriteAsync(payload, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken signal,
        CancellationToken timeout)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => completion.TrySetResult());
        await completion.Task.WaitAsync(timeout);
    }
}
