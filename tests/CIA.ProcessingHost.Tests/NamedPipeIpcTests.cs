using System.Buffers.Binary;
using System.Text;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Ipc;
using CIA.ProcessingHost.Ipc;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class NamedPipeIpcTests
{
    [TestMethod]
    public async Task DatabaseBuildCommandAndPublicationResponseRoundTripAsTypedContracts()
    {
        var correlation = OperationCorrelation.CreateNew();
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath("source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var mapping = new DatabaseMappingSnapshot(
        [
            new DatabaseColumnMapping("DatabaseName", ["sourceTag"])
        ]);
        var command = new BuildDatabaseCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            correlation,
            [source],
            mapping);
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [OperationItemStatus.ProcessedSuccessfully(source.SourceId.ToString())]);
        var response = new BuildDatabaseResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Accepted,
            completion,
            new DatabaseGenerationSummary(correlation.OperationId, mapping, 1),
            Failure: null);
        var reviewCommand = new GetDatabaseReviewPageCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            correlation.OperationId,
            StartRowOrdinal: 1,
            RowCount: DatabaseReviewLimits.MaximumRowsPerPage);
        var reviewPage = new DatabaseReviewPage(
            correlation.OperationId,
            startRowOrdinal: 1,
            requestedRowCount: DatabaseReviewLimits.MaximumRowsPerPage,
            totalMappedValueCount: 1,
            [
                new DatabaseReviewColumn(
                    "DatabaseName",
                    totalValueCount: 1,
                    [new DatabaseReviewValue(1, "exact", "sourceTag", source.SourceId)])
            ]);
        var reviewResponse = new GetDatabaseReviewPageResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            reviewCommand.MessageId,
            correlation.OperationId,
            CommandAcceptance.Accepted,
            reviewPage,
            Failure: null);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, reviewCommand);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, reviewResponse);
        stream.Position = 0;

        var commandResult = (BuildDatabaseCommand)await LengthPrefixedJsonMessageFramer
            .ReadAsync(stream);
        var responseResult = (BuildDatabaseResponse)await LengthPrefixedJsonMessageFramer
            .ReadAsync(stream);
        var reviewCommandResult = (GetDatabaseReviewPageCommand)await
            LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        var reviewResponseResult = (GetDatabaseReviewPageResponse)await
            LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        Assert.AreEqual(correlation, commandResult.Correlation);
        Assert.AreEqual(source, commandResult.Sources.Single());
        Assert.AreEqual("DatabaseName", commandResult.Mapping.Columns.Single().DatabaseTagName);
        Assert.AreEqual(CommandAcceptance.Accepted, responseResult.Acceptance);
        Assert.AreEqual(correlation, responseResult.Completion.Correlation);
        Assert.AreEqual(correlation.OperationId, responseResult.PublishedGeneration?.OperationId);
        Assert.AreEqual(1, responseResult.PublishedGeneration?.ValueCount);
        Assert.AreEqual(correlation.OperationId, reviewCommandResult.GenerationId);
        Assert.AreEqual(DatabaseReviewLimits.MaximumRowsPerPage, reviewCommandResult.RowCount);
        Assert.AreEqual("exact", reviewResponseResult.Page?.Columns[0].Values[0].Value);
        Assert.AreEqual(source.SourceId, reviewResponseResult.Page?.Columns[0].Values[0].SourceId);
    }

    [TestMethod]
    public async Task DesktopAndProcessingHostExchangeTypedContractsOverNamedPipe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = $"CIA.Tests.{Guid.NewGuid():N}";

        var acceptTask = ProcessingHostIpcServer
            .AcceptConnectionAsync(pipeName, timeout.Token)
            .AsTask();

        await using var client = await ProcessingHostIpcClient
            .ConnectAsync(pipeName, timeout.Token);
        await using var server = await acceptTask;

        Assert.IsTrue(client.IsConnected);
        Assert.IsTrue(server.IsConnected);

        var command = new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);

        var receiveCommandTask = server.ReceiveAsync(timeout.Token).AsTask();
        await client.SendAsync(command, timeout.Token);
        var receivedCommand = await receiveCommandTask;

        Assert.IsInstanceOfType(receivedCommand, typeof(EstablishConnectionCommand));
        Assert.AreEqual(command, receivedCommand);

        var acknowledgement = new CommandAcknowledgement(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Accepted,
            Failure: null);
        var availabilityEvent = new ProcessingHostAvailabilityEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            ProcessingHostAvailability.Ready);

        var receiveAcknowledgementTask = client.ReceiveAsync(timeout.Token).AsTask();
        await server.SendAsync(acknowledgement, timeout.Token);
        var receivedAcknowledgement = await receiveAcknowledgementTask;

        var receiveAvailabilityEventTask = client.ReceiveAsync(timeout.Token).AsTask();
        await server.SendAsync(availabilityEvent, timeout.Token);
        var receivedAvailabilityEvent = await receiveAvailabilityEventTask;

        Assert.IsInstanceOfType(receivedAcknowledgement, typeof(CommandAcknowledgement));
        Assert.AreEqual(acknowledgement, receivedAcknowledgement);
        Assert.IsInstanceOfType(receivedAvailabilityEvent, typeof(ProcessingHostAvailabilityEvent));
        Assert.AreEqual(availabilityEvent, receivedAvailabilityEvent);
    }

    [TestMethod]
    public async Task FramerUsesLittleEndianLengthPrefixAndPreservesMessageBoundaries()
    {
        var first = CreateConnectionCommand();
        var second = new ProcessingHostAvailabilityEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            ProcessingHostAvailability.Ready);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, first);
        var secondFrameStart = checked((int)stream.Position);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, second);

        var bytes = stream.ToArray();
        var firstPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(0, IpcProtocol.FrameHeaderLength));
        var secondPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.AsSpan(secondFrameStart, IpcProtocol.FrameHeaderLength));

        Assert.AreEqual(secondFrameStart - IpcProtocol.FrameHeaderLength, firstPayloadLength);
        Assert.AreEqual(bytes.Length - secondFrameStart - IpcProtocol.FrameHeaderLength, secondPayloadLength);

        stream.Position = 0;
        Assert.AreEqual(first, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        Assert.AreEqual(second, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    public async Task LifecycleCommandsRoundTripAsTypedLengthPrefixedJsonContracts()
    {
        IpcMessage[] messages =
        [
            new ProcessingHostLivenessCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow),
            new CancelOperationCommand(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                OperationId.CreateNew()),
            new StopProcessingHostCommand(Guid.CreateVersion7(), DateTimeOffset.UtcNow)
        ];

        await using var stream = new MemoryStream();

        foreach (var message in messages)
        {
            await LengthPrefixedJsonMessageFramer.WriteAsync(stream, message);
        }

        stream.Position = 0;

        foreach (var expected in messages)
        {
            Assert.AreEqual(expected, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        }

        Assert.AreEqual(stream.Length, stream.Position);
    }

    [TestMethod]
    public async Task SourceLoadingCommandAndResponseRoundTripAsTypedContracts()
    {
        var activeSettings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(5)
        };
        var command = new LoadSourcesCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            SourceSelectionKind.Folder,
            Path.GetFullPath("sources"),
            activeSettings);
        var archivePath = Path.GetFullPath("sources/package.zip");
        var extractionRoot = Path.GetFullPath("working/extraction");
        var archiveId = SourceId.CreateNew();
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.Combine(extractionRoot, "source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile)
        {
            ArchiveProvenance = new ArchiveSourceProvenance(
                archiveId,
                archivePath,
                [new ArchiveLineageItem(archiveId, archivePath, 1)],
                ArchiveNestingLevel: 1,
                ArchiveMemberPath: "source.xml",
                extractionRoot,
                ArchiveExtractionRetention.ManagedTemporary,
                activeSettings.MaximumArchiveNestingDepth,
                PersistentExtractionDirectory: null)
        };
        var response = new LoadSourcesResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Accepted,
            [source],
            Failure: null)
        {
            Issues =
            [
                new SourceIntakeIssue(
                    "archive-entry-failed",
                    "One independent archive member was skipped.",
                    archivePath,
                    ArchiveNestingLevel: 1,
                    EntryPath: "../bad/item.xml")
            ]
        };
        var progress = new SourceIntakeProgressEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            new SourceIntakeProgressSnapshot(
                archivePath,
                CurrentArchiveNestingLevel: 1,
                EncounteredItemCount: 3,
                LoadedSourceCount: 1,
                IssueCount: 1,
                FailureCount: 0,
                TotalItemCount: null));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, progress);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);
        stream.Position = 0;

        var roundTrippedCommand = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        Assert.AreEqual(command, roundTrippedCommand);
        Assert.AreEqual(
            5,
            ((LoadSourcesCommand)roundTrippedCommand).Settings.MaximumArchiveNestingDepth.Value);
        var roundTrippedProgress = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        Assert.AreEqual(progress, roundTrippedProgress);
        var roundTrippedResponse = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        Assert.IsInstanceOfType<LoadSourcesResponse>(roundTrippedResponse);
        Assert.AreEqual(response.CommandMessageId, ((LoadSourcesResponse)roundTrippedResponse).CommandMessageId);
        var typedResponse = (LoadSourcesResponse)roundTrippedResponse;
        Assert.HasCount(1, typedResponse.Sources);
        Assert.AreEqual(source.SourceId, typedResponse.Sources[0].SourceId);
        Assert.AreEqual(source.Path, typedResponse.Sources[0].Path);
        Assert.AreEqual(
            source.ArchiveProvenance?.OriginalArchiveSourceId,
            typedResponse.Sources[0].ArchiveProvenance?.OriginalArchiveSourceId);
        Assert.AreEqual(
            source.ArchiveProvenance?.ArchiveMemberPath,
            typedResponse.Sources[0].ArchiveProvenance?.ArchiveMemberPath);
        Assert.HasCount(1, typedResponse.Sources[0].ArchiveProvenance!.ArchiveLineage);
        CollectionAssert.AreEqual(response.Issues.ToArray(), typedResponse.Issues.ToArray());
    }

    [TestMethod]
    public async Task InvalidArchiveDepthIsRejectedAtTheIpcContractBoundary()
    {
        var invalidSettings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = default
        };
        var command = new LoadSourcesCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            SourceSelectionKind.Archive,
            Path.GetFullPath("sources.zip"),
            invalidSettings);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<IpcProtocolException>(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, command).AsTask());

        Assert.AreEqual(IpcProtocolError.InvalidContract, exception.Error);
        StringAssert.Contains(exception.Message, "maximum archive nesting depth");
    }

    [TestMethod]
    public async Task SourceProgressRejectsInconsistentDeterminateCounters()
    {
        var progress = new SourceIntakeProgressEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            new SourceIntakeProgressSnapshot(
                Path.GetFullPath("source.zip"),
                CurrentArchiveNestingLevel: 1,
                EncounteredItemCount: 8,
                LoadedSourceCount: 2,
                IssueCount: 0,
                FailureCount: 0,
                TotalItemCount: 5));
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<IpcProtocolException>(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, progress).AsTask());

        Assert.AreEqual(IpcProtocolError.InvalidContract, exception.Error);
        StringAssert.Contains(exception.Message, "counters");
    }

    [TestMethod]
    public async Task PersistentExtractionWithoutAnExplicitDestinationIsRejectedAtTheIpcBoundary()
    {
        var command = new LoadSourcesCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            SourceSelectionKind.Archive,
            Path.GetFullPath("sources.zip"),
            SourceLoadSettings.Default with
            {
                PersistentArchiveExtractionEnabled = true,
                PersistentArchiveExtractionDirectory = null
            });
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<IpcProtocolException>(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, command).AsTask());

        Assert.AreEqual(IpcProtocolError.InvalidContract, exception.Error);
        StringAssert.Contains(exception.Message, "explicitly configured destination");
    }

    [TestMethod]
    public async Task ExtractedSourceOutsideItsDeclaredRootIsRejectedAtTheIpcBoundary()
    {
        var archivePath = Path.GetFullPath("source.zip");
        var archiveId = SourceId.CreateNew();
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath("outside/source.xml"),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile)
        {
            ArchiveProvenance = new ArchiveSourceProvenance(
                archiveId,
                archivePath,
                [new ArchiveLineageItem(archiveId, archivePath, 1)],
                ArchiveNestingLevel: 1,
                ArchiveMemberPath: "source.xml",
                ExtractionRoot: Path.GetFullPath("working/extraction"),
                ArchiveExtractionRetention.ManagedTemporary,
                ArchiveNestingDepth.Default,
                PersistentExtractionDirectory: null)
        };
        var response = new LoadSourcesResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            CommandAcceptance.Accepted,
            [source],
            Failure: null);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<IpcProtocolException>(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, response).AsTask());

        Assert.AreEqual(IpcProtocolError.InvalidContract, exception.Error);
        StringAssert.Contains(exception.Message, "inside its extraction root");
    }

    [TestMethod]
    public async Task SourceRefreshCommandAndControlledResponseRoundTripWithRetainedIdentity()
    {
        var source = new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath("source.xml"),
            IsIncluded: false,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
        var command = new RefreshSourceCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            source);
        var response = new RefreshSourceResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            command.MessageId,
            CommandAcceptance.Rejected,
            source with { Status = LoadedSourceStatus.FailedValidation },
            new IpcFailure("malformed-xml", "The XML source is malformed."));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, command);
        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);
        stream.Position = 0;

        Assert.AreEqual(command, await LengthPrefixedJsonMessageFramer.ReadAsync(stream));
        var roundTrippedResponse = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);
        Assert.AreEqual(response, roundTrippedResponse);
        Assert.AreEqual(
            source.SourceId,
            ((RefreshSourceResponse)roundTrippedResponse).Source.SourceId);
    }

    [TestMethod]
    public async Task RejectedAcknowledgementRoundTripsControlledFailureDetails()
    {
        var acknowledgement = new CommandAcknowledgement(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            CommandAcceptance.Rejected,
            new IpcFailure(
                "invalid-lifecycle-sequence",
                "The command is not valid in the current Processing Host lifecycle state."));
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, acknowledgement);
        stream.Position = 0;

        var roundTripped = await LengthPrefixedJsonMessageFramer.ReadAsync(stream);

        Assert.IsInstanceOfType<CommandAcknowledgement>(roundTripped);
        Assert.AreEqual(acknowledgement, roundTripped);
        Assert.AreEqual(acknowledgement.Failure, ((CommandAcknowledgement)roundTripped).Failure);
    }

    [TestMethod]
    public async Task FramerReassemblesHeaderAndPayloadFromFragmentedReads()
    {
        var expected = CreateConnectionCommand();
        await using var completeFrame = new MemoryStream();
        await LengthPrefixedJsonMessageFramer.WriteAsync(completeFrame, expected);
        await using var fragmentedFrame = new FragmentedReadStream(
            completeFrame.ToArray(),
            maximumReadSize: 1);

        var actual = await LengthPrefixedJsonMessageFramer.ReadAsync(fragmentedFrame);

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(fragmentedFrame.Length, fragmentedFrame.Position);
    }

    [TestMethod]
    public async Task DiscoveryResponseAtObservedSourceScaleExceedsLegacyLimitAndRoundTrips()
    {
        const int observedSourceCount = 3481;
        const int observedInformationTypeCount = 30;
        const int legacyMaximumPayloadLength = 1024 * 1024;
        var correlation = OperationCorrelation.CreateNew();
        var sourceIds = Enumerable.Range(0, observedSourceCount)
            .Select(_ => SourceId.CreateNew())
            .ToArray();
        var contributions = sourceIds
            .Select((sourceId, index) => new DiscoveredSourceContribution(
                sourceId,
                $"source-{index:D4}.xml",
                1))
            .ToArray();
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            sourceIds.Select(sourceId => OperationItemStatus.ProcessedSuccessfully(
                sourceId.ToString())));
        var response = new RunDiscoveryResponse(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            CommandAcceptance.Accepted,
            completion,
            Enumerable.Range(0, observedInformationTypeCount)
                .Select(index => new DiscoveredInformation(
                    $"Tag{index:D2}",
                    observedSourceCount,
                    contributions,
                    "sample"))
                .ToArray(),
            [],
            Failure: null);
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonMessageFramer.WriteAsync(stream, response);

        Assert.IsGreaterThan(legacyMaximumPayloadLength, stream.Length);
        Assert.IsLessThanOrEqualTo(
            IpcProtocol.MaximumPayloadLength + IpcProtocol.FrameHeaderLength,
            stream.Length);
        stream.Position = 0;
        var roundTripped = (RunDiscoveryResponse)await LengthPrefixedJsonMessageFramer
            .ReadAsync(stream);
        Assert.HasCount(observedInformationTypeCount, roundTripped.Information);
        Assert.HasCount(
            observedSourceCount,
            roundTripped.Information[0].ContributingSources);
        Assert.AreEqual(correlation, roundTripped.Completion.Correlation);
    }

    [TestMethod]
    public async Task WriterRejectsInvalidContractBeforeWritingFrameBytes()
    {
        var invalidMessage = new ProcessingHostAvailabilityEvent(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            (ProcessingHostAvailability)int.MaxValue);
        await using var stream = new MemoryStream();

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, invalidMessage).AsTask(),
            IpcProtocolError.InvalidContract);

        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public async Task WriterRejectsCancellationWithoutValidOperationIdentity()
    {
        var invalidMessage = new CancelOperationCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            default);
        await using var stream = new MemoryStream();

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.WriteAsync(stream, invalidMessage).AsTask(),
            IpcProtocolError.InvalidContract);

        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(IpcProtocol.MaximumPayloadLength + 1)]
    public async Task FramerRejectsInvalidDeclaredLengths(int declaredLength)
    {
        await using var stream = CreateFrame([], declaredLength);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.InvalidFrameLength);
    }

    [TestMethod]
    public async Task FramerRejectsTruncatedPayload()
    {
        await using var stream = CreateFrame("{}"u8.ToArray(), declaredLength: 8);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.TruncatedFrame);
    }

    [TestMethod]
    public async Task FramerRejectsTruncatedHeader()
    {
        await using var stream = new MemoryStream([1, 0], writable: false);

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.TruncatedFrame);
    }

    [TestMethod]
    public async Task FramerRejectsMalformedJson()
    {
        await using var stream = CreateFrame("{not-json}"u8.ToArray());

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.MalformedJson);
    }

    [TestMethod]
    public async Task FramerRejectsUnknownMessageContract()
    {
        const string unknownMessage = """
            {
              "messageType": "unknownMessage",
              "messageId": "00000000-0000-0000-0000-000000000000",
              "timestampUtc": "2026-09-03T00:00:00.0000000+00:00"
            }
            """;
        await using var stream = CreateFrame(Encoding.UTF8.GetBytes(unknownMessage));

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.MalformedJson);
    }

    [TestMethod]
    public async Task FramerRejectsUnmappedJsonMembers()
    {
        var messageWithUnmappedMember = $$"""
            {
              "messageType": "processingHostLivenessCommand",
              "messageId": "{{Guid.CreateVersion7()}}",
              "timestampUtc": "{{DateTimeOffset.UtcNow:O}}",
              "unexpected": "boundary input"
            }
            """;
        await using var stream = CreateFrame(Encoding.UTF8.GetBytes(messageWithUnmappedMember));

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.MalformedJson);
    }

    [TestMethod]
    public async Task FramerRejectsStructurallyInvalidTypedContract()
    {
        var invalidContract = $$"""
            {
              "messageType": "establishConnectionCommand",
              "messageId": "00000000-0000-0000-0000-000000000000",
              "timestampUtc": "{{DateTimeOffset.UtcNow:O}}",
              "clientInstanceId": "{{Guid.CreateVersion7()}}",
              "protocolVersion": {{IpcProtocol.CurrentVersion}}
            }
            """;
        await using var stream = CreateFrame(Encoding.UTF8.GetBytes(invalidContract));

        await AssertProtocolErrorAsync(
            () => LengthPrefixedJsonMessageFramer.ReadAsync(stream).AsTask(),
            IpcProtocolError.InvalidContract);
    }

    [TestMethod]
    public void ProcessBoundaryContractsExcludeForbiddenObjectTypes()
    {
        var contractsAssembly = typeof(IpcMessage).Assembly;
        var referencedAssemblies = contractsAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        string[] forbiddenAssemblies =
        [
            "CIA.Core",
            "CIA.Desktop",
            "Microsoft.Data.Sqlite",
            "PresentationFramework",
            "WindowsBase"
        ];

        foreach (var forbiddenAssembly in forbiddenAssemblies)
        {
            CollectionAssert.DoesNotContain(referencedAssemblies, forbiddenAssembly);
        }

        var payloadTypes = contractsAssembly
            .GetExportedTypes()
            .Where(type => typeof(IpcMessage).IsAssignableFrom(type) || type == typeof(IpcFailure));

        foreach (var payloadType in payloadTypes)
        {
            Assert.IsFalse(typeof(Exception).IsAssignableFrom(payloadType));
            Assert.IsFalse(payloadType.Name.Contains("ViewModel", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Control", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Parser", StringComparison.Ordinal));
            Assert.IsFalse(payloadType.Name.Contains("Storage", StringComparison.Ordinal));

            foreach (var property in payloadType.GetProperties())
            {
                Assert.AreNotEqual(typeof(object), property.PropertyType);
                Assert.IsFalse(typeof(Exception).IsAssignableFrom(property.PropertyType));
                Assert.AreNotEqual(
                    true,
                    property.PropertyType.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal));
            }
        }
    }

    private static EstablishConnectionCommand CreateConnectionCommand()
    {
        return new EstablishConnectionCommand(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            IpcProtocol.CurrentVersion);
    }

    private static MemoryStream CreateFrame(byte[] payload, int? declaredLength = null)
    {
        var frame = new byte[IpcProtocol.FrameHeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            frame.AsSpan(0, IpcProtocol.FrameHeaderLength),
            declaredLength ?? payload.Length);
        payload.CopyTo(frame, IpcProtocol.FrameHeaderLength);
        return new MemoryStream(frame, writable: false);
    }

    private static async Task AssertProtocolErrorAsync(
        Func<Task> action,
        IpcProtocolError expectedError)
    {
        try
        {
            await action();
            Assert.Fail($"Expected {nameof(IpcProtocolException)} with error {expectedError}.");
        }
        catch (IpcProtocolException exception)
        {
            Assert.AreEqual(expectedError, exception.Error);
        }
    }

    private sealed class FragmentedReadStream(byte[] buffer, int maximumReadSize)
        : MemoryStream(buffer, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(
                destination[..Math.Min(destination.Length, maximumReadSize)],
                cancellationToken);
        }
    }
}
