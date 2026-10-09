using System.Collections.Specialized;
using CIA.Contracts.Discovery;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Discovery;
using CIA.Desktop.Hosting;
using CIA.Desktop.Presentation;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DiscoveryWorkspaceViewModelTests
{
    [TestMethod]
    public void DiscoveryGridSampleIsSingleLineAndBoundedDefensively()
    {
        var sourceId = SourceId.CreateNew();
        var item = new DiscoveredInformationItemViewModel(
            new DiscoveredInformation(
                "reviews_widget",
                1,
                [new DiscoveredSourceContribution(sourceId, "book.xml", 1)],
                "\r\n<style>\t" + new string('x', 500) + "\n</style>"),
            "Set 1",
            DiscoveryInformationDisposition.Neutral);

        Assert.IsLessThanOrEqualTo(
            DiscoverySampleValueFormatter.MaximumLength,
            item.SampleValue.Length);
        Assert.IsFalse(item.SampleValue.Any(
            character => character is '\r' or '\n' or '\t'));
        Assert.EndsWith("…", item.SampleValue);
    }

    [TestMethod]
    public async Task DiscoveryTransportFailureRetainsBoundedCauseInDiagnostics()
    {
        var source = CreateSource("transport-failure.xml");
        var history = new RecordingProcessingHistoryRecorder();
        using var workflow = CreateWorkflowCoordinator(history);
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        using var viewModel = new DiscoveryWorkspaceViewModel(
            new TransportFailureDiscoveryClient(),
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        var diagnostic = history.Diagnostics.Single();
        Assert.AreEqual("discovery-ipc-protocol-failure", diagnostic.FailureCode);
        Assert.AreEqual(
            "Discovery could not receive a valid response from the Processing Host.",
            diagnostic.UserFacingDescription);
        Assert.AreEqual(
            "IPC protocol failure: Discovery result exceeded the bounded frame contract.",
            diagnostic.TechnicalDetail);
        Assert.IsLessThanOrEqualTo(1024, diagnostic.TechnicalDetail!.Length);
    }

    [TestMethod]
    public async Task SameNameIdentitySwitchSupersedesPendingPreviewAndUsesFullIdentity()
    {
        var source = CreateSource("same-name-source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new SameNameSwitchDiscoveryClient();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await client.FirstRequestStarted;
        var first = viewModel.Information.Single(item =>
            item.StructuralPath == "/root/first/toolnbr");
        var second = viewModel.Information.Single(item =>
            item.StructuralPath == "/root/second/slot");

        viewModel.SelectedInformation = second;

        Assert.AreSame(second, viewModel.SelectedInformation);
        Assert.AreEqual(second.TotalOccurrenceCount, viewModel.OccurrenceTotal);
        Assert.AreEqual(1, client.OccurrenceCallCount,
            "A second source read started while the first same-name preview was still active.");

        client.CompleteFirstRequest();
        await client.SecondRequestCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual(1, client.MaximumConcurrentRequests);
        Assert.HasCount(2, client.Lookups);
        Assert.AreEqual(first.Identity, client.Lookups[0].Identity);
        Assert.AreEqual(second.Identity, client.Lookups[1].Identity);
        Assert.AreEqual(second.Identity.SourceSetId, client.Lookups[1].Identity.SourceSetId);
        Assert.AreEqual(second.StructuralPath, client.Lookups[1].Identity.StructuralPath);
        Assert.AreEqual(second.CandidateKind, client.Lookups[1].Identity.CandidateKind);
        Assert.AreEqual(second.StructuralIdentity, client.Lookups[1].Identity.StructuralIdentity);
        Assert.AreEqual(second.InformationType, client.Lookups[1].Identity.InformationType);
        Assert.AreEqual("second identity value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(second.TotalOccurrenceCount, viewModel.OccurrenceTotal);
    }

    [TestMethod]
    public async Task RapidSameNameSwitchKeepsOnlyLatestPendingIdentity()
    {
        var source = CreateSource("rapid-switch-source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new SameNameSwitchDiscoveryClient();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await client.FirstRequestStarted;
        var first = viewModel.Information.Single(item =>
            item.StructuralPath == "/root/first/toolnbr");
        var second = viewModel.Information.Single(item =>
            item.StructuralPath == "/root/second/slot");

        viewModel.SelectedInformation = second;
        viewModel.SelectedInformation = first;

        Assert.AreEqual(1, client.OccurrenceCallCount,
            "Rapid selection changes accumulated concurrent source reads.");

        client.CompleteFirstRequest();
        await client.SecondRequestCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual(1, client.MaximumConcurrentRequests);
        Assert.HasCount(2, client.Lookups);
        Assert.AreEqual(first.Identity, client.Lookups[0].Identity);
        Assert.AreEqual(first.Identity, client.Lookups[1].Identity);
        Assert.AreSame(first, viewModel.SelectedInformation);
        Assert.AreEqual("first identity value", viewModel.OccurrencePreviewText);
    }

    [TestMethod]
    public async Task SameNamePreviewFailureIsContainedForTheSelectedIdentity()
    {
        var source = CreateSource("same-name-failure.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var occurrenceCallCount = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                var loadedSource = sources.Single();
                return Accept(
                    correlation,
                    sources,
                    [
                        new DiscoveredInformation(
                            new DiscoveryInformationIdentity(
                                loadedSource.SourceSetId,
                                "/root/first/toolnbr",
                                "toolnbr",
                                SourceValueCandidateKind.Element,
                                "/root/first/toolnbr"),
                            1,
                            [new DiscoveredSourceContribution(
                                loadedSource.SourceId,
                                "source.xml",
                                1)],
                            "first sample"),
                        new DiscoveredInformation(
                            new DiscoveryInformationIdentity(
                                loadedSource.SourceSetId,
                                "/root/second/slot",
                                "toolnbr",
                                SourceValueCandidateKind.Structural,
                                "/root/second/slot[@key='tool']"),
                            2,
                            [new DiscoveredSourceContribution(
                                loadedSource.SourceId,
                                "source.xml",
                                2)],
                            "second sample")
                    ]);
            },
            lookup => ++occurrenceCallCount == 1
                ? AcceptOccurrence(
                    lookup.Identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    source.SourceId,
                    "first identity value")
                : throw new IOException("Contained preview read failure."));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);
        var second = viewModel.Information.Single(item =>
            item.StructuralPath == "/root/second/slot");

        viewModel.SelectedInformation = second;
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreSame(second, viewModel.SelectedInformation);
        Assert.AreEqual(2, viewModel.OccurrenceTotal);
        Assert.AreEqual(0, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(
            "The selected occurrence could not be retrieved.",
            viewModel.OccurrencePreviewText);
        Assert.IsFalse(viewModel.IsOccurrenceLoading);
    }

    [TestMethod]
    public async Task CandidateKindAndStructuralIdentityRemainDistinctInPresentationAndConfiguration()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                var loadedSource = sources.Single();
                return Accept(
                    correlation,
                    sources,
                    [
                        new DiscoveredInformation(
                            new DiscoveryInformationIdentity(
                                loadedSource.SourceSetId,
                                "/root/item/code",
                                "code",
                                SourceValueCandidateKind.Element,
                                "/root/item/code"),
                            1,
                            [new DiscoveredSourceContribution(loadedSource.SourceId, "source.xml", 1)],
                            "element"),
                        new DiscoveredInformation(
                            new DiscoveryInformationIdentity(
                                loadedSource.SourceSetId,
                                "/root/item",
                                "code",
                                SourceValueCandidateKind.Attribute,
                                "/root/item/@code"),
                            1,
                            [new DiscoveredSourceContribution(loadedSource.SourceId, "source.xml", 1)],
                            "attribute")
                    ]);
            });
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var element = viewModel.Information.Single(item =>
            item.CandidateKind == SourceValueCandidateKind.Element);
        var attribute = viewModel.Information.Single(item =>
            item.CandidateKind == SourceValueCandidateKind.Attribute);
        viewModel.ToggleSelectionCommand.Execute(attribute);

        Assert.AreNotEqual(element.Identity, attribute.Identity);
        Assert.AreEqual("Element", element.CandidateKindText);
        Assert.AreEqual("Attribute", attribute.CandidateKindText);
        Assert.AreEqual("/root/item/@code", attribute.StructuralIdentity);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            configuration.Current.Items.Single(item =>
                item.Identity == attribute.Identity).Disposition);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            configuration.Current.Items.Single(item =>
                item.Identity == element.Identity).Disposition);
    }

    [TestMethod]
    public async Task SetAwareRowsKeepConfigurationPreviewAndLayoutsIndependent()
    {
        var first = CreateSource("first.xml");
        var second = CreateSource("second.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, loading) = await LoadSourcesAsync(workflow, first, second);
        var secondSet = loading.CreateSourceSet("Set 2").SourceSet!;
        Assert.IsTrue(loading.ReassignSources([sourceSet.Items[1]], secondSet.SourceSetId).Accepted);

        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                var setOneSource = sources.Single(source =>
                    source.SourceSetId != secondSet.SourceSetId);
                var setTwoSource = sources.Single(source =>
                    source.SourceSetId == secondSet.SourceSetId);
                return Accept(
                    correlation,
                    sources,
                    [
                        CreateSetAwareInformation(
                            setOneSource,
                            "/root/buyer/name",
                            "Buyer A"),
                        CreateSetAwareInformation(
                            setOneSource,
                            "/root/seller/name",
                            "Seller A"),
                        CreateSetAwareInformation(
                            setTwoSource,
                            "/root/buyer/name",
                            "Buyer B")
                    ]);
            },
            lookup => AcceptOccurrence(
                lookup.Identity,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                lookup.Identity.SourceSetId == secondSet.SourceSetId
                    ? second.SourceId
                    : first.SourceId,
                lookup.Identity.StructuralPath.Contains("seller", StringComparison.Ordinal)
                    ? "Seller A"
                    : "Buyer value"));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.HasCount(2, viewModel.Information);
        var setOneName = viewModel.Information.Single(item =>
            item.SourceSetName == "Set 1");
        var setTwoBuyer = viewModel.Information.Single(item =>
            item.SourceSetName == "Set 2" && item.StructuralPath == "/root/buyer/name");
        Assert.HasCount(2, setOneName.DetailedIdentities);
        Assert.IsTrue(setOneName.DetailedIdentities.Any(identity =>
            identity.StructuralPath == "/root/buyer/name"));
        Assert.IsTrue(setOneName.DetailedIdentities.Any(identity =>
            identity.StructuralPath == "/root/seller/name"));
        Assert.AreNotEqual(setOneName.Identity, setTwoBuyer.Identity);

        viewModel.ToggleSelectionCommand.Execute(setOneName);
        viewModel.SelectedInformation = setTwoBuyer;
        await AwaitSelectedOccurrenceAsync(viewModel);
        viewModel.SelectedDatabaseTag = "Set Two Name";

        Assert.IsTrue(setOneName.IsSelected);
        Assert.IsTrue(setOneName.DetailedIdentities.All(identity =>
            configuration.Current.Items.Single(item => item.Identity == identity).Disposition
                == DiscoveryInformationDisposition.Selected));
        Assert.IsFalse(setTwoBuyer.IsSelected);
        Assert.AreEqual("name", setOneName.DatabaseTag);
        Assert.AreEqual("Set Two Name", setTwoBuyer.DatabaseTag);
        Assert.AreEqual(setTwoBuyer.Identity, client.LastOccurrenceLookup?.Identity);

        CollectionAssert.AreEquivalent(
            Enum.GetValues<RepeatedDataLayout>(),
            viewModel.RepeatedDataLayoutOptions.Select(option => option.Mode).ToArray());
        Assert.IsTrue(viewModel.SourceSetLayouts.All(layout =>
            layout.SelectedLayout == RepeatedDataLayout.AlignRepeatedGroupsByPosition));

        var database = await workflow.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);
        workflow.CompleteOperation(database.Operation!.OperationId, OperationOutcome.CompletedSuccessfully);
        var extraction = await workflow.BeginOperationAsync(WorkflowOperationKind.Extraction);
        workflow.CompleteOperation(extraction.Operation!.OperationId, OperationOutcome.CompletedSuccessfully);
        var firstLayout = viewModel.SourceSetLayouts.Single(layout =>
            layout.SourceSetId == setOneName.SourceSetId);
        var secondLayout = viewModel.SourceSetLayouts.Single(layout =>
            layout.SourceSetId == setTwoBuyer.SourceSetId);

        firstLayout.SelectedLayout = RepeatedDataLayout.StructuralRows;

        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Database);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Extraction);
        Assert.AreEqual(RepeatedDataLayout.StructuralRows, firstLayout.SelectedLayout);
        Assert.AreEqual(
            RepeatedDataLayout.AlignRepeatedGroupsByPosition,
            secondLayout.SelectedLayout);
        Assert.AreEqual(
            RepeatedDataLayout.StructuralRows,
            configuration.Current.SourceSets.Single(item =>
                item.SourceSetId == firstLayout.SourceSetId).RepeatedDataLayout);

        var identityBeforeRename = setTwoBuyer.Identity;
        Assert.IsTrue(loading.RenameSourceSet(secondSet.SourceSetId, "Renamed Set").Accepted);
        Assert.AreEqual("Renamed Set", setTwoBuyer.SourceSetName);
        Assert.AreEqual(identityBeforeRename, setTwoBuyer.Identity);
    }

    [TestMethod]
    public async Task ElementRowsAcrossPathsBecomeOneLogicalFieldWithAggregatePreviewOrder()
    {
        var source = CreateSource("logical-fields.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var occurrenceLookups = new List<DiscoveryOccurrenceLookup>();
        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                var loadedSource = sources.Single();
                return Accept(
                    correlation,
                    sources,
                    [
                        CreateDetailedInformation(
                            loadedSource,
                            "/root/first/toolnbr",
                            "toolnbr",
                            SourceValueCandidateKind.Element,
                            "/root/first/toolnbr",
                            4,
                            "first"),
                        CreateDetailedInformation(
                            loadedSource,
                            "/root/second/toolnbr",
                            "toolnbr",
                            SourceValueCandidateKind.Element,
                            "/root/second/toolnbr",
                            6,
                            "second"),
                        CreateDetailedInformation(
                            loadedSource,
                            "/root/third/toolnbr",
                            "toolnbr",
                            SourceValueCandidateKind.Element,
                            "/root/third/toolnbr",
                            6,
                            "third")
                    ]);
            },
            lookup =>
            {
                occurrenceLookups.Add(lookup);
                var identity = lookup.GlobalOrdinal <= 4
                    ? lookup.DetailedIdentities[0]
                    : lookup.GlobalOrdinal <= 10
                        ? lookup.DetailedIdentities[1]
                        : lookup.DetailedIdentities[2];
                return AcceptOccurrence(
                    identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    source.SourceId,
                    $"{identity.StructuralPath}:{lookup.GlobalOrdinal}");
            });
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        var logicalField = viewModel.Information.Single();
        Assert.AreEqual("toolnbr", logicalField.InformationType);
        Assert.AreEqual(16, logicalField.TotalOccurrenceCount);
        Assert.HasCount(3, logicalField.DetailedIdentities);
        Assert.AreEqual(1, logicalField.SourceCount);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(16, viewModel.OccurrenceTotal);

        viewModel.OccurrenceOrdinalInput = "5";
        await AwaitOrdinalJumpAsync(viewModel);

        Assert.AreEqual(5, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("/root/second/toolnbr:5", viewModel.OccurrencePreviewText);
        Assert.AreEqual(
            "/root/second/toolnbr",
            occurrenceLookups[^1].DetailedIdentities[1].StructuralPath);
        Assert.AreEqual(5, occurrenceLookups[^1].GlobalOrdinal);
    }

    [TestMethod]
    public void LogicalGroupingPreservesCandidateKindNamespaceAndStructuralSlots()
    {
        var sourceSetId = SourceSetId.CreateNew();
        var sourceId = SourceId.CreateNew();
        var information = new[]
        {
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{https://example.test/one}root/{https://example.test/one}first/{https://example.test/one}name",
                "one:name",
                SourceValueCandidateKind.Element,
                "/{https://example.test/one}root/{https://example.test/one}first/{https://example.test/one}name"),
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{https://example.test/one}root/{https://example.test/one}second/{https://example.test/one}name",
                "one:name",
                SourceValueCandidateKind.Element,
                "/{https://example.test/one}root/{https://example.test/one}second/{https://example.test/one}name"),
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{urn:two}root/{urn:two}name",
                "two:name",
                SourceValueCandidateKind.Element,
                "/{urn:two}root/{urn:two}name"),
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{urn:one}root/{urn:one}item",
                "one:name",
                SourceValueCandidateKind.Attribute,
                "/{urn:one}root/{urn:one}item/@{urn:one}name"),
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{urn:one}root/{urn:one}record/{urn:one}slot",
                "value",
                SourceValueCandidateKind.Structural,
                "/{urn:one}root/{urn:one}record/{urn:one}slot[@key='B']"),
            CreateDetailedInformation(
                sourceSetId,
                sourceId,
                "/{urn:one}root/{urn:one}record/{urn:one}slot",
                "value",
                SourceValueCandidateKind.Structural,
                "/{urn:one}root/{urn:one}record/{urn:one}slot[@key='C']")
        };

        var logical = DiscoveredInformationItemViewModel.CreateLogicalItems(
            information,
            _ => "Set 1",
            _ => DiscoveryInformationDisposition.Neutral,
            _ => null).ToArray();

        Assert.HasCount(5, logical);
        Assert.HasCount(2, logical.Single(item =>
            item.CandidateKind == SourceValueCandidateKind.Element
            && item.InformationType == "one:name").DetailedIdentities);
        Assert.HasCount(1, logical.Single(item =>
            item.InformationType == "two:name").DetailedIdentities);
        Assert.HasCount(1, logical.Single(item =>
            item.CandidateKind == SourceValueCandidateKind.Attribute).DetailedIdentities);
        Assert.HasCount(2, logical.Where(item =>
            item.CandidateKind == SourceValueCandidateKind.Structural).ToArray());
    }

    [TestMethod]
    public async Task LogicalConfigurationActionsFanOutToEveryDetailedIdentity()
    {
        var source = CreateSource("logical-configuration.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var configuration = new ActiveDiscoveryConfiguration();
        var client = new StubDiscoveryClient((correlation, sources) =>
        {
            var loadedSource = sources.Single();
            return Accept(
                correlation,
                sources,
                [
                    CreateDetailedInformation(
                        loadedSource,
                        "/root/a/toolname",
                        "toolname",
                        SourceValueCandidateKind.Element,
                        "/root/a/toolname"),
                    CreateDetailedInformation(
                        loadedSource,
                        "/root/b/toolname",
                        "toolname",
                        SourceValueCandidateKind.Element,
                        "/root/b/toolname")
                ]);
        });
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);
        var logicalField = viewModel.Information.Single();

        viewModel.ToggleSelectionCommand.Execute(logicalField);

        Assert.AreEqual("1 / 1 selected", viewModel.SelectionSummary);
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            configuration.Current.Items.Single(item => item.Identity == identity).Disposition
                == DiscoveryInformationDisposition.Selected));

        viewModel.ToggleSelectionCommand.Execute(logicalField);
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            configuration.Current.Items.Single(item => item.Identity == identity).Disposition
                == DiscoveryInformationDisposition.Neutral));

        viewModel.ToggleSelectionCommand.Execute(logicalField);
        viewModel.ToggleBlacklistCommand.Execute(logicalField);
        Assert.IsTrue(logicalField.IsBlacklisted);
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            configuration.Current.Items.Single(item => item.Identity == identity).Disposition
                == DiscoveryInformationDisposition.Blacklisted));

        viewModel.ToggleBlacklistCommand.Execute(logicalField);
        viewModel.SelectedDatabaseTag = "Tool Name";
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            configuration.DatabaseTagOverridesByIdentity[identity] == "Tool Name"));

        viewModel.SelectedDatabaseTag = "Renamed Tool";
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            configuration.DatabaseTagOverridesByIdentity[identity] == "Renamed Tool"));

        viewModel.ClearDatabaseTagOverrideCommand.Execute(null);
        Assert.IsTrue(logicalField.DetailedIdentities.All(identity =>
            !configuration.DatabaseTagOverridesByIdentity.ContainsKey(identity)));
        Assert.HasCount(2, configuration.Current.Items);
    }

    [TestMethod]
    public async Task RunUsesIncludedReadySourcesAndPresentsReconciledResults()
    {
        var first = CreateSource("first.xml");
        var excluded = CreateSource("excluded.xml") with { IsIncluded = false };
        var unavailable = CreateSource("unavailable.xml") with
        {
            Status = LoadedSourceStatus.Unavailable
        };
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(
            workflow,
            first,
            excluded,
            unavailable);
        var client = new StubDiscoveryClient(
            (correlation, sources) =>
            {
                Assert.HasCount(1, sources);
                var source = sources[0];
                var information = new DiscoveredInformation(
                    "SerialNumber",
                    3,
                    [new DiscoveredSourceContribution(source.SourceId, "first.xml", 3)],
                    "SN-001");
                return Accept(correlation, sources, [information]);
            });
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        Assert.AreEqual("Discovery not available", viewModel.DiscoveryStateText);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.HasCount(1, client.LastSources);
        Assert.AreEqual(first.SourceId, client.LastSources[0].SourceId);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);
        Assert.AreEqual("Discovery complete", viewModel.StatusTitle);
        Assert.HasCount(1, viewModel.Information);
        var result = viewModel.Information[0];
        Assert.AreEqual("SerialNumber", result.InformationType);
        Assert.AreEqual(3, result.TotalOccurrenceCount);
        Assert.AreEqual(1, result.SourceCount);

        viewModel.InspectSourcesCommand.Execute(result);

        Assert.IsTrue(viewModel.IsSourceInspectionOpen);
        Assert.AreSame(result, viewModel.InspectedInformation);
        StringAssert.Contains(viewModel.SourceInspectionSummary, "matches tag aggregate");
    }

    [TestMethod]
    public async Task SourceSelectionChangeMakesRetainedDiscoveryResultOutOfDate()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, loading) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "PartNumber",
                    1,
                    [new DiscoveredSourceContribution(source.SourceId, "source.xml", 1)],
                    "PN-1")]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var inclusion = loading.SetInclusion(sourceSet.Items, isIncluded: false);

        Assert.IsTrue(inclusion.Accepted);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("0 / 1 sources included", viewModel.IncludedSourceSummary);
        Assert.IsFalse(viewModel.RunDiscoveryCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task SourceInspectionLoadsOnlyOneBoundedContributorPageAtATime()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = new DiscoveredInformation(
            "Name",
            250,
            250,
            "sample");
        var contributors = Enumerable.Range(1, 250)
            .Select(index => new DiscoveredSourceContribution(
                SourceId.CreateNew(),
                $"source-{index:D3}.xml",
                1))
            .ToArray();
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, [information]),
            contributorResultFactory: query => new DiscoveryContributorClientResult(
                true,
                new DiscoveryContributorPage(
                    query.DiscoveryOperationId,
                    query.StartIndex,
                    contributors.Length,
                    contributors.Length,
                    contributors.Skip(query.StartIndex).Take(query.PageSize).ToArray()),
                null,
                null));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var logical = viewModel.Information.Single();
        await viewModel.InspectSourcesCommand.ExecuteAsync(logical);

        Assert.HasCount(100, viewModel.InspectedSources);
        Assert.AreEqual("source-001.xml", viewModel.InspectedSources[0].SourceName);
        Assert.AreEqual(0, client.ContributorQueries[0].StartIndex);
        Assert.AreEqual(DiscoveryContributorPaging.DefaultPageSize,
            client.ContributorQueries[0].PageSize);
        Assert.IsTrue(viewModel.NextSourceInspectionPageCommand.CanExecute(null));

        await viewModel.NextSourceInspectionPageCommand.ExecuteAsync(null);

        Assert.HasCount(100, viewModel.InspectedSources);
        Assert.AreEqual("source-101.xml", viewModel.InspectedSources[0].SourceName);
        Assert.AreEqual(100, client.ContributorQueries[1].StartIndex);
        Assert.HasCount(2, client.ContributorQueries);
    }

    [TestMethod]
    public async Task PresentationSupportsSearchSortingAndPaginationWithoutChangingResults()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = Enumerable.Range(1, 30)
            .Reverse()
            .Select(index => new DiscoveredInformation(
                $"Tag{index:00}",
                index,
                [new DiscoveredSourceContribution(source.SourceId, "source.xml", index)],
                $"Value {index:00}"))
            .ToArray();
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, information));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow)
        {
            PageSize = 25
        };

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(2, viewModel.PageCount);
        Assert.HasCount(25, viewModel.Information);
        Assert.AreEqual("Tag01", viewModel.Information[0].InformationType);

        viewModel.SortCommand.Execute("Occurrences");
        Assert.AreEqual(1, viewModel.Information[0].TotalOccurrenceCount);
        viewModel.SortCommand.Execute("Occurrences");
        Assert.AreEqual(30, viewModel.Information[0].TotalOccurrenceCount);

        viewModel.SearchText = "Tag29";
        Assert.HasCount(1, viewModel.Information);
        var filtered = viewModel.Information[0];
        Assert.AreEqual("Tag29", filtered.InformationType);
        Assert.AreEqual(1, filtered.SourceCount);
    }

    [TestMethod]
    public async Task SearchMatchesOnlyVisibleTagAndDatabaseTagAndResetsPaging()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = Enumerable.Range(1, 26)
            .Select(index => CreateDetailedInformation(
                source,
                $"/root/item/tag{index:00}",
                $"Tag{index:00}",
                SourceValueCandidateKind.Element,
                $"/root/item/tag{index:00}"))
            .Concat(
            [
                CreateDetailedInformation(
                    source,
                    "/root/item/visible-tag",
                    "VisibleTag",
                    SourceValueCandidateKind.Element,
                    "/root/item/visible-tag"),
                CreateDetailedInformation(
                    source,
                    "/root/item/mapped-source",
                    "MappedSource",
                    SourceValueCandidateKind.Element,
                    "/root/item/mapped-source"),
                CreateDetailedInformation(
                    source,
                    "/root/item",
                    "Code",
                    SourceValueCandidateKind.Attribute,
                    "/root/hidden-structure/@code"),
                CreateDetailedInformation(
                    source,
                    "/root/item/sample-carrier",
                    "SampleCarrier",
                    SourceValueCandidateKind.Element,
                    "/root/item/sample-carrier",
                    sampleValue: "hidden-sample")
            ])
            .ToArray();
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, information));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow)
        {
            PageSize = 25
        };

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        viewModel.SelectedInformation = viewModel.Information.Single(item =>
            item.InformationType == "MappedSource");
        viewModel.SelectedDatabaseTag = "Visible Mapping";

        viewModel.SearchText = "visibletag";
        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("VisibleTag", viewModel.Information[0].InformationType);

        viewModel.SearchText = "VISIBLE MAPPING";
        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("MappedSource", viewModel.Information[0].InformationType);

        foreach (var hiddenValue in new[]
                 {
                     "hidden-structure",
                     "hidden-sample",
                     "Attribute",
                     "Set 1"
                 })
        {
            viewModel.SearchText = hiddenValue;
            Assert.IsEmpty(viewModel.Information, hiddenValue);
        }

        viewModel.SearchText = string.Empty;
        Assert.AreEqual(30, viewModel.FilteredCount);
        Assert.AreEqual(2, viewModel.PageCount);
        viewModel.NextPageCommand.Execute(null);
        Assert.AreEqual(2, viewModel.CurrentPage);

        viewModel.SearchText = "Tag01";
        Assert.AreEqual(1, viewModel.CurrentPage);
        Assert.HasCount(1, viewModel.Information);
    }

    [TestMethod]
    public async Task OccurrencePreviewLoadsFirstValueAndNavigatesWithinBoundaries()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var values = new[] { "First value", "Second value", "Third value" };
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "Tag",
                    values.Length,
                    [new DiscoveredSourceContribution(source.SourceId, "source.xml", values.Length)],
                    values[0])]),
            lookup => AcceptOccurrence(
                lookup.InformationType,
                lookup.GlobalOrdinal,
                values.Length,
                source.SourceId,
                values[lookup.GlobalOrdinal - 1]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("First value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("1", viewModel.OccurrenceOrdinalInput);
        Assert.AreEqual("of 3", viewModel.OccurrenceTotalText);
        Assert.IsFalse(viewModel.PreviousOccurrenceCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextOccurrenceCommand.CanExecute(null));

        await viewModel.NextOccurrenceCommand.ExecuteAsync(null);
        await viewModel.NextOccurrenceCommand.ExecuteAsync(null);

        Assert.AreEqual("Third value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.IsFalse(viewModel.NextOccurrenceCommand.CanExecute(null));

        await viewModel.PreviousOccurrenceCommand.ExecuteAsync(null);

        Assert.AreEqual("Second value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(2, viewModel.CurrentOccurrenceOrdinal);
    }

    [TestMethod]
    public async Task SelectingTagImmediatelyShowsKnownTotalThenLoadsFirstOccurrence()
    {
        const int occurrenceCount = 202;
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new DeferredOccurrenceDiscoveryClient(source.SourceId, occurrenceCount);
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual("ecoef", viewModel.SelectedInformation?.InformationType);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.AreEqual("of 202", viewModel.OccurrenceTotalText);
        Assert.AreEqual("Loading occurrence...", viewModel.OccurrencePreviewText);
        Assert.IsTrue(viewModel.IsOccurrenceLoading);

        client.CompleteFirstOccurrence("Exact first value");
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("Exact first value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.IsFalse(viewModel.PreviousOccurrenceCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextOccurrenceCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task FirstOccurrenceFailureRetainsSelectedTagAndKnownTotal()
    {
        const int occurrenceCount = 202;
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "ecoef",
                    occurrenceCount,
                    [new DiscoveredSourceContribution(
                        source.SourceId,
                        "source.xml",
                        occurrenceCount)],
                    "sample")]),
            _ => new DiscoveryOccurrenceClientResult(
                false,
                Occurrence: null,
                "discovery-preview-source-unavailable",
                "The occurrence preview could not be retrieved."));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("ecoef", viewModel.SelectedInformation?.InformationType);
        Assert.AreEqual(occurrenceCount, viewModel.OccurrenceTotal);
        Assert.AreEqual("of 202", viewModel.OccurrenceTotalText);
        Assert.AreEqual(0, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(
            "The occurrence preview could not be retrieved.",
            viewModel.OccurrencePreviewText);
        Assert.AreNotEqual(
            "Select a discovered tag to inspect its occurrence value.",
            viewModel.OccurrencePreviewText);
    }

    [TestMethod]
    public async Task DirectOrdinalRejectsInvalidInputAndTagChangeLoadsFirstOccurrence()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Alpha"] = ["Alpha 1", "Alpha 2", "Alpha 3"],
            ["Beta"] = ["Beta 1", "Beta 2"]
        };
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                values.Select(pair => new DiscoveredInformation(
                    pair.Key,
                    pair.Value.Length,
                    [new DiscoveredSourceContribution(
                        source.SourceId,
                        "source.xml",
                        pair.Value.Length)],
                    pair.Value[0])).ToArray()),
            lookup => AcceptOccurrence(
                lookup.InformationType,
                lookup.GlobalOrdinal,
                values[lookup.InformationType].Length,
                source.SourceId,
                values[lookup.InformationType][lookup.GlobalOrdinal - 1]));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        viewModel.OccurrenceOrdinalInput = "3";
        await AwaitOrdinalJumpAsync(viewModel);

        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        var requestsBeforeInvalidInput = client.OccurrenceCallCount;

        viewModel.OccurrenceOrdinalInput = "not-a-number";
        await AwaitOrdinalJumpAsync(viewModel);
        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("3", viewModel.OccurrenceOrdinalInput);

        viewModel.OccurrenceOrdinalInput = "4";
        await AwaitOrdinalJumpAsync(viewModel);
        Assert.AreEqual("Alpha 3", viewModel.OccurrencePreviewText);
        Assert.AreEqual(3, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual("3", viewModel.OccurrenceOrdinalInput);
        Assert.AreEqual(requestsBeforeInvalidInput, client.OccurrenceCallCount);

        viewModel.SelectedInformation = viewModel.Information.Single(
            information => information.InformationType == "Beta");
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.AreEqual("Beta 1", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(2, viewModel.OccurrenceTotal);
    }

    [TestMethod]
    public async Task GlobalOrdinalMapsToOrderedContributingSourceAndLocalOrdinal()
    {
        var first = CreateSource("first.xml");
        var second = CreateSource("second.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, first, second);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "Tag",
                    5,
                    [
                        new DiscoveredSourceContribution(first.SourceId, "first.xml", 2),
                        new DiscoveredSourceContribution(second.SourceId, "second.xml", 3)
                    ],
                    "First source value")]),
            lookup => AcceptOccurrence(
                lookup.InformationType,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                lookup.GlobalOrdinal <= 2 ? first.SourceId : second.SourceId,
                $"Global occurrence {lookup.GlobalOrdinal}"));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);

        Assert.IsNotNull(client.LastOccurrenceLookup);
        Assert.AreEqual(1, client.LastOccurrenceLookup.GlobalOrdinal);
        Assert.HasCount(1, client.LastOccurrenceLookup.DetailedIdentities);

        viewModel.OccurrenceOrdinalInput = "4";
        await AwaitOrdinalJumpAsync(viewModel);

        Assert.AreEqual(4, client.LastOccurrenceLookup?.GlobalOrdinal);
        Assert.AreEqual("Global occurrence 4", viewModel.OccurrencePreviewText);
    }

    [TestMethod]
    public async Task FailedDiscoveryReportsRealIssueCountAndFailureStage()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => new DiscoveryClientResult(
                false,
                [],
                [new DiscoverySourceIssue(
                    source.SourceId,
                    "unsupported-xml-structure",
                    "The source structure is not supported.")],
                OperationCompletion.FromTerminalOutcome(
                    correlation,
                    OperationOutcome.Failed,
                    sources.Select(item => OperationItemStatus.Failed(
                        item.SourceId.ToString(),
                        "unsupported-xml-structure"))),
                "discovery-no-usable-sources",
                "Discovery could not interpret any source in the active source set."));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual("Discovery failed", viewModel.StatusTitle);
        StringAssert.Contains(viewModel.ResultSummary, "1 issues");
        Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);
        Assert.AreNotEqual("Stage: Complete", viewModel.ProgressStage);
    }

    [TestMethod]
    public async Task FailedReRunRetainsPriorRowsButMarksThemOutOfDate()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var callCount = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => ++callCount == 1
                ? Accept(
                    correlation,
                    sources,
                    [new DiscoveredInformation(
                        "RetainedTag",
                        3,
                        [new DiscoveredSourceContribution(source.SourceId, "source.xml", 3)],
                        "retained value")])
                : Reject(correlation, sources, "processing-host-unavailable"),
            lookup => AcceptOccurrence(
                lookup.InformationType,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                source.SourceId,
                lookup.GlobalOrdinal == 1 ? "retained value" : "retained second value"));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        await AwaitSelectedOccurrenceAsync(viewModel);
        var selectedInformation = viewModel.SelectedInformation;
        var collectionChangeCount = 0;
        ((INotifyCollectionChanged)viewModel.Information).CollectionChanged +=
            (_, _) => collectionChangeCount++;

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.HasCount(1, viewModel.Information);
        Assert.AreEqual("RetainedTag", viewModel.Information[0].InformationType);
        Assert.AreSame(selectedInformation, viewModel.SelectedInformation);
        Assert.AreEqual("retained value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(1, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(3, viewModel.OccurrenceTotal);
        Assert.AreEqual(0, collectionChangeCount);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
        Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        Assert.AreEqual("Discovery re-run failed", viewModel.StatusTitle);
        StringAssert.Contains(viewModel.StatusDetail, "Previous Discovery results are retained");
        Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);

        await viewModel.NextOccurrenceCommand.ExecuteAsync(null);

        Assert.AreEqual("retained second value", viewModel.OccurrencePreviewText);
        Assert.AreEqual(2, viewModel.CurrentOccurrenceOrdinal);
        Assert.AreEqual(2, client.LastOccurrenceLookup?.GlobalOrdinal);
    }

    [TestMethod]
    public async Task FailedReRunCannotTemporarilyPresentCurrentWithDeferredWorkflowNotifications()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var callCount = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => ++callCount == 1
                ? Accept(correlation, sources, CreateInformation(source.SourceId, "RetainedTag"))
                : Reject(correlation, sources, "processing-host-unavailable"));
        var queuedContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        DiscoveryWorkspaceViewModel viewModel;

        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            viewModel = new DiscoveryWorkspaceViewModel(
                client,
                new ActiveDiscoveryConfiguration(),
                sourceSet,
                workflow);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
            queuedContext.Drain();
            Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);

            await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

            Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Discovery);
            Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
            Assert.AreEqual("Discovery re-run failed", viewModel.StatusTitle);
            Assert.AreEqual("Stage: Failed", viewModel.ProgressStage);

            queuedContext.Drain();
            Assert.AreEqual("Discovery out of date", viewModel.DiscoveryStateText);
        }
    }

    [TestMethod]
    public async Task SelectionAndBlacklistRemainDistinctInTheActiveConfiguration()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var information = CreateInformation(source.SourceId, "Alpha", "Beta");
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(correlation, sources, information));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");

        viewModel.ToggleSelectionCommand.Execute(alpha);

        Assert.IsTrue(alpha.IsSelected);
        Assert.IsFalse(alpha.IsBlacklisted);
        Assert.AreEqual("1 / 2 selected", viewModel.SelectionSummary);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));

        viewModel.ToggleBlacklistCommand.Execute(alpha);

        Assert.IsFalse(alpha.IsSelected);
        Assert.IsTrue(alpha.IsBlacklisted);
        Assert.AreEqual("0 / 2 selected", viewModel.SelectionSummary);
        Assert.IsFalse(viewModel.ToggleSelectionCommand.CanExecute(alpha));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpha"));

        viewModel.ToggleBlacklistCommand.Execute(alpha);
        Assert.IsFalse(alpha.IsBlacklisted);
        Assert.AreEqual("Neutral", alpha.DispositionText);
        Assert.IsTrue(viewModel.ToggleSelectionCommand.CanExecute(alpha));

        viewModel.ToggleSelectionCommand.Execute(alpha);
        viewModel.ToggleSelectionCommand.Execute(alpha);
        Assert.IsFalse(alpha.IsSelected);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Database);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task BulkSelectionAffectsOnlyTheFilteredVisibleSubset()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                CreateInformation(source.SourceId, "Alpha", "Alpine", "Beta", "Gamma")));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpine = viewModel.Information.Single(item => item.InformationType == "Alpine");
        viewModel.ToggleBlacklistCommand.Execute(alpine);
        viewModel.SearchText = "Alp";

        viewModel.SelectVisibleCommand.Execute(null);

        Assert.AreEqual(1, configuration.Current.SelectedCount);
        Assert.AreEqual(1, configuration.Current.BlacklistedCount);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpine"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Beta"));
        Assert.AreEqual("1 / 4 selected", viewModel.SelectionSummary);

        viewModel.ShowBlacklisted = false;
        Assert.AreEqual(1, viewModel.FilteredCount);
        viewModel.DeselectVisibleCommand.Execute(null);

        Assert.AreEqual(0, configuration.Current.SelectedCount);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Blacklisted,
            GetDisposition(configuration, "Alpine"));
        Assert.AreEqual(WorkflowArtifactStatus.Unavailable, workflow.Current.Database);
        Assert.AreEqual(1, client.CallCount);
    }

    [TestMethod]
    public async Task ReRunRetainsMatchingActiveSelectionAndDropsUnavailableIdentities()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var run = 0;
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                ++run == 1
                    ? CreateInformation(source.SourceId, "Alpha", "Removed")
                    : CreateInformation(source.SourceId, "Alpha", "Added")));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");
        viewModel.ToggleSelectionCommand.Execute(alpha);

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);

        Assert.AreEqual(2, client.CallCount);
        Assert.HasCount(2, configuration.Current.Items);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Selected,
            GetDisposition(configuration, "Alpha"));
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Added"));
        Assert.IsFalse(configuration.Current.Items.Any(item => item.InformationType == "Removed"));
        Assert.AreEqual("1 / 2 selected", viewModel.SelectionSummary);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual("Discovery current", viewModel.DiscoveryStateText);
        Assert.AreEqual("Stage: Complete", viewModel.ProgressStage);
    }

    [TestMethod]
    public async Task DatabaseTagOverrideAssignChangeClearPreservesIdentityAndSelectionSeparation()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                CreateInformation(source.SourceId, "Alpha", "Beta")));
        var configuration = new ActiveDiscoveryConfiguration();
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            configuration,
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");

        Assert.AreEqual("Alpha", alpha.DatabaseTag);
        Assert.IsFalse(alpha.HasDatabaseTagOverride);

        viewModel.SelectedInformation = alpha;
        viewModel.SelectedDatabaseTag = "Mapped Alpha";
        Assert.AreEqual("Mapped Alpha", alpha.DatabaseTag);
        Assert.AreEqual("Mapped Alpha", alpha.DatabaseTagOverride);
        Assert.IsTrue(alpha.HasDatabaseTagOverride);
        Assert.AreEqual("Revert", viewModel.DatabaseTagOverrideActionText);

        viewModel.SelectedDatabaseTag = "Changed Alpha";
        viewModel.ToggleSelectionCommand.Execute(alpha);
        viewModel.ToggleBlacklistCommand.Execute(alpha);
        viewModel.ToggleBlacklistCommand.Execute(alpha);

        Assert.AreEqual("Alpha", alpha.InformationType);
        Assert.AreEqual("Changed Alpha", alpha.DatabaseTag);
        Assert.AreEqual("Changed Alpha", configuration.DatabaseTagOverrides["Alpha"]);
        Assert.AreEqual(
            DiscoveryInformationDisposition.Neutral,
            GetDisposition(configuration, "Alpha"));
        var selectionProfileJson = System.Text.Json.JsonSerializer.Serialize(
            configuration.Current);
        Assert.IsFalse(selectionProfileJson.Contains("Changed Alpha", StringComparison.Ordinal));

        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");
        Assert.AreEqual("Changed Alpha", alpha.DatabaseTag);

        viewModel.SelectedInformation = alpha;
        viewModel.ClearDatabaseTagOverrideCommand.Execute(null);

        Assert.AreEqual("Alpha", alpha.InformationType);
        Assert.AreEqual("Alpha", alpha.DatabaseTag);
        Assert.IsFalse(alpha.HasDatabaseTagOverride);
        Assert.IsFalse(configuration.DatabaseTagOverrides.ContainsKey("Alpha"));
        Assert.AreEqual("Default", viewModel.DatabaseTagOverrideActionText);
    }

    [TestMethod]
    public async Task ModifiedDatabaseTagFilterShowsOnlyOverrideMappings()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                CreateInformation(source.SourceId, "Alpha", "Beta", "Gamma")));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var alpha = viewModel.Information.Single(item => item.InformationType == "Alpha");
        viewModel.SelectedInformation = alpha;
        viewModel.SelectedDatabaseTag = "Mapped Alpha";

        viewModel.ShowOnlyDatabaseTagOverrides = true;

        Assert.AreEqual(1, viewModel.FilteredCount);
        Assert.HasCount(1, viewModel.Information);
        Assert.AreSame(alpha, viewModel.Information[0]);
        Assert.IsTrue(viewModel.Information[0].HasDatabaseTagOverride);

        viewModel.SearchText = "Mapped Alpha";
        Assert.HasCount(1, viewModel.Information);
        viewModel.ClearDatabaseTagOverrideCommand.Execute(null);
        Assert.AreEqual(0, viewModel.FilteredCount);
        Assert.IsEmpty(viewModel.Information);
    }

    [TestMethod]
    public async Task DatabaseTagOverrideMakesDependentDatabaseStaleWithoutRebuild()
    {
        var source = CreateSource("source.xml");
        using var workflow = CreateWorkflowCoordinator();
        var (sourceSet, _) = await LoadSourcesAsync(workflow, source);
        var client = new StubDiscoveryClient(
            (correlation, sources) => Accept(
                correlation,
                sources,
                CreateInformation(source.SourceId, "Alpha")));
        using var viewModel = new DiscoveryWorkspaceViewModel(
            client,
            new ActiveDiscoveryConfiguration(),
            sourceSet,
            workflow);
        await viewModel.RunDiscoveryCommand.ExecuteAsync(null);
        var database = await workflow.BeginOperationAsync(WorkflowOperationKind.DatabaseBuild);
        workflow.CompleteOperation(
            database.Operation!.OperationId,
            OperationOutcome.CompletedSuccessfully);
        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Database);

        viewModel.SelectedDatabaseTag = "Mapped Alpha";

        Assert.AreEqual(WorkflowArtifactStatus.Current, workflow.Current.Discovery);
        Assert.AreEqual(WorkflowArtifactStatus.Stale, workflow.Current.Database);
        Assert.AreEqual(1, client.CallCount);
        Assert.IsNull(workflow.Current.ActiveOperation);
    }

    private static IReadOnlyList<DiscoveredInformation> CreateInformation(
        SourceId sourceId,
        params string[] informationTypes)
    {
        return informationTypes
            .Select(informationType => new DiscoveredInformation(
                informationType,
                1,
                [new DiscoveredSourceContribution(sourceId, "source.xml", 1)],
                $"{informationType} value"))
            .ToArray();
    }

    private static DiscoveredInformation CreateSetAwareInformation(
        LoadedSourceContract source,
        string structuralPath,
        string sampleValue)
    {
        return new DiscoveredInformation(
            new DiscoveryInformationIdentity(source.SourceSetId, structuralPath, "name"),
            1,
            [new DiscoveredSourceContribution(source.SourceId, Path.GetFileName(source.Path), 1)],
            sampleValue);
    }

    private static DiscoveredInformation CreateDetailedInformation(
        LoadedSourceContract source,
        string structuralPath,
        string informationType,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int occurrenceCount = 1,
        string sampleValue = "sample")
    {
        return CreateDetailedInformation(
            source.SourceSetId,
            source.SourceId,
            structuralPath,
            informationType,
            candidateKind,
            structuralIdentity,
            occurrenceCount,
            sampleValue);
    }

    private static DiscoveredInformation CreateDetailedInformation(
        SourceSetId sourceSetId,
        SourceId sourceId,
        string structuralPath,
        string informationType,
        SourceValueCandidateKind candidateKind,
        string structuralIdentity,
        int occurrenceCount = 1,
        string sampleValue = "sample")
    {
        return new DiscoveredInformation(
            new DiscoveryInformationIdentity(
                sourceSetId,
                structuralPath,
                informationType,
                candidateKind,
                structuralIdentity),
            occurrenceCount,
            [new DiscoveredSourceContribution(sourceId, "source.xml", occurrenceCount)],
            sampleValue);
    }

    private static DiscoveryInformationDisposition GetDisposition(
        ActiveDiscoveryConfiguration configuration,
        string informationType)
    {
        return configuration.Current.Items
            .Single(item => item.InformationType == informationType)
            .Disposition;
    }

    private static async Task<(ActiveLoadedSourceSet SourceSet, SourceLoadingCoordinator Loading)>
        LoadSourcesAsync(
            ApplicationWorkflowCoordinator workflow,
            params LoadedSourceContract[] sources)
    {
        var sourceSet = new ActiveLoadedSourceSet();
        var client = new StubSourceIntakeClient(
            new SourceIntakeClientResult(
                true,
                sources,
                FailureCode: null,
                FailureDescription: null));
        var loading = new SourceLoadingCoordinator(client, sourceSet, workflow);

        var result = await loading.AddAsync(SourceSelectionKind.XmlFile, sources[0].Path);

        Assert.IsTrue(result.Accepted);
        Assert.HasCount(sources.Length, sourceSet.Items);
        return (sourceSet, loading);
    }

    private static LoadedSourceContract CreateSource(string name)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            Path.GetFullPath(name),
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
    }

    private static ApplicationWorkflowCoordinator CreateWorkflowCoordinator(
        RecordingProcessingHistoryRecorder? history = null)
    {
        return new ApplicationWorkflowCoordinator(
            new StubProcessingHostSupervisor(),
            history ?? new RecordingProcessingHistoryRecorder());
    }

    private static DiscoveryClientResult Accept(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<DiscoveredInformation> information)
    {
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            sources.Select(source => OperationItemStatus.ProcessedSuccessfully(
                source.SourceId.ToString())));
        return new DiscoveryClientResult(
            true,
            information,
            [],
            completion,
            FailureCode: null,
            FailureDescription: null);
    }

    private static DiscoveryClientResult Reject(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        string failureCode)
    {
        return new DiscoveryClientResult(
            false,
            [],
            [],
            OperationCompletion.FromTerminalOutcome(
                correlation,
                OperationOutcome.Failed,
                sources.Select(source => OperationItemStatus.Unprocessed(
                    source.SourceId.ToString(),
                    failureCode))),
            failureCode,
            "The Processing Host could not complete Discovery.");
    }

    private static DiscoveryOccurrenceClientResult AcceptOccurrence(
        DiscoveryInformationIdentity identity,
        int ordinal,
        int totalOccurrenceCount,
        SourceId sourceId,
        string value)
    {
        return new DiscoveryOccurrenceClientResult(
            true,
            new DiscoveredOccurrence(
                identity,
                ordinal,
                totalOccurrenceCount,
                sourceId,
                value),
            FailureCode: null,
            FailureDescription: null);
    }

    private static DiscoveryOccurrenceClientResult AcceptOccurrence(
        string informationType,
        int ordinal,
        int totalOccurrenceCount,
        SourceId sourceId,
        string value)
    {
        return new DiscoveryOccurrenceClientResult(
            true,
            new DiscoveredOccurrence(
                informationType,
                ordinal,
                totalOccurrenceCount,
                sourceId,
                value),
            FailureCode: null,
            FailureDescription: null);
    }

    private static async Task AwaitSelectedOccurrenceAsync(
        DiscoveryWorkspaceViewModel viewModel)
    {
        if (viewModel.SelectInformationCommand.ExecutionTask is { } task)
        {
            await task;
        }
    }

    private static async Task AwaitOrdinalJumpAsync(DiscoveryWorkspaceViewModel viewModel)
    {
        await viewModel.JumpToOccurrenceCommand.ExecuteAsync(null);
    }

    private sealed class StubDiscoveryClient : IDiscoveryClient
    {
        private readonly Func<
            OperationCorrelation,
            IReadOnlyList<LoadedSourceContract>,
            DiscoveryClientResult> _resultFactory;
        private readonly Func<
            DiscoveryOccurrenceLookup,
            DiscoveryOccurrenceClientResult>? _occurrenceResultFactory;
        private readonly Func<
            DiscoveryContributorPageQuery,
            DiscoveryContributorClientResult>? _contributorResultFactory;
        private IReadOnlyList<DiscoveredInformation> _lastInformation = [];

        public StubDiscoveryClient(
            Func<
                OperationCorrelation,
                IReadOnlyList<LoadedSourceContract>,
                DiscoveryClientResult> resultFactory,
            Func<
                DiscoveryOccurrenceLookup,
                DiscoveryOccurrenceClientResult>? occurrenceResultFactory = null,
            Func<
                DiscoveryContributorPageQuery,
                DiscoveryContributorClientResult>? contributorResultFactory = null)
        {
            _resultFactory = resultFactory;
            _occurrenceResultFactory = occurrenceResultFactory;
            _contributorResultFactory = contributorResultFactory;
        }

        public IReadOnlyList<LoadedSourceContract> LastSources { get; private set; } = [];

        public int CallCount { get; private set; }

        public int OccurrenceCallCount { get; private set; }

        public DiscoveryOccurrenceLookup? LastOccurrenceLookup { get; private set; }

        public List<DiscoveryOccurrenceLookup> OccurrenceLookups { get; } = [];

        public List<DiscoveryContributorPageQuery> ContributorQueries { get; } = [];

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSources = sources;
            var result = _resultFactory(correlation, sources);
            _lastInformation = result.Information;
            return Task.FromResult(result);
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            OccurrenceCallCount++;
            LastOccurrenceLookup = lookup;
            OccurrenceLookups.Add(lookup);

            if (_occurrenceResultFactory is not null)
            {
                return Task.FromResult(_occurrenceResultFactory(lookup));
            }

            var localOrdinal = lookup.GlobalOrdinal;
            DiscoveredInformation? information = null;
            foreach (var identity in lookup.DetailedIdentities)
            {
                information = _lastInformation.Single(item => item.Identity == identity);
                if (localOrdinal <= information.TotalOccurrenceCount)
                {
                    break;
                }

                localOrdinal -= information.TotalOccurrenceCount;
            }

            Assert.IsNotNull(information);
            Assert.IsNotEmpty(LastSources);
            return Task.FromResult(AcceptOccurrence(
                information.Identity,
                lookup.GlobalOrdinal,
                lookup.TotalOccurrenceCount,
                LastSources[0].SourceId,
                lookup.GlobalOrdinal == 1
                    ? information.SampleValue
                    : $"{lookup.InformationType} occurrence {lookup.GlobalOrdinal}"));
        }

        public Task<DiscoveryContributorClientResult> GetContributorsAsync(
            DiscoveryContributorPageQuery query,
            CancellationToken cancellationToken = default)
        {
            ContributorQueries.Add(query);
            if (_contributorResultFactory is not null)
            {
                return Task.FromResult(_contributorResultFactory(query));
            }

            var totalOccurrences = _lastInformation
                .Where(item => query.DetailedIdentities.Contains(item.Identity))
                .Sum(item => item.TotalOccurrenceCount);
            var contributors = LastSources
                .Select((source, index) => new DiscoveredSourceContribution(
                    source.SourceId,
                    Path.GetFileName(source.Path),
                    LastSources.Count == 1 ? totalOccurrences : 1))
                .Skip(query.StartIndex)
                .Take(query.PageSize)
                .ToArray();
            return Task.FromResult(new DiscoveryContributorClientResult(
                true,
                new DiscoveryContributorPage(
                    query.DiscoveryOperationId,
                    query.StartIndex,
                    query.ExpectedSourceCount,
                    totalOccurrences,
                    contributors),
                FailureCode: null,
                FailureDescription: null));
        }
    }

    private sealed class TransportFailureDiscoveryClient : IDiscoveryClient
    {
        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DiscoveryClientResult(
                false,
                [],
                [],
                OperationCompletion.FromTerminalOutcome(
                    correlation,
                    OperationOutcome.Failed,
                    sources.Select(source => OperationItemStatus.Unprocessed(
                        source.SourceId.ToString(),
                        "discovery-ipc-protocol-failure"))),
                "discovery-ipc-protocol-failure",
                "Discovery could not receive a valid response from the Processing Host.")
            {
                FailureTechnicalDetail =
                    "IPC protocol failure: Discovery result exceeded the bounded frame contract."
            });
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DeferredOccurrenceDiscoveryClient(
        SourceId sourceId,
        int occurrenceCount) : IDiscoveryClient
    {
        private readonly TaskCompletionSource<DiscoveryOccurrenceClientResult> _firstOccurrence =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Accept(
                correlation,
                sources,
                [new DiscoveredInformation(
                    "ecoef",
                    occurrenceCount,
                    [new DiscoveredSourceContribution(sourceId, "source.xml", occurrenceCount)],
                    "sample")]));
        }

        public Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ecoef", lookup.InformationType);
            Assert.AreEqual(1, lookup.GlobalOrdinal);
            return _firstOccurrence.Task;
        }

        public void CompleteFirstOccurrence(string value)
        {
            _firstOccurrence.SetResult(AcceptOccurrence(
                "ecoef",
                1,
                occurrenceCount,
                sourceId,
                value));
        }
    }

    private sealed class SameNameSwitchDiscoveryClient : IDiscoveryClient
    {
        private readonly TaskCompletionSource _firstRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRequestCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _activeRequests;
        private int _maximumConcurrentRequests;
        private int _occurrenceCallCount;
        private SourceId _sourceId;

        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public Task SecondRequestCompleted => _secondRequestCompleted.Task;

        public int OccurrenceCallCount => Volatile.Read(ref _occurrenceCallCount);

        public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);

        public List<DiscoveryOccurrenceLookup> Lookups { get; } = [];

        public Task<DiscoveryClientResult> RunAsync(
            OperationCorrelation correlation,
            IReadOnlyList<LoadedSourceContract> sources,
            CancellationToken cancellationToken = default)
        {
            var source = sources.Single();
            _sourceId = source.SourceId;
            var firstIdentity = new DiscoveryInformationIdentity(
                source.SourceSetId,
                "/root/first/toolnbr",
                "toolnbr",
                SourceValueCandidateKind.Element,
                "/root/first/toolnbr");
            var secondIdentity = new DiscoveryInformationIdentity(
                source.SourceSetId,
                "/root/second/slot",
                "toolnbr",
                SourceValueCandidateKind.Structural,
                "/root/second/slot[@key='tool']");
            return Task.FromResult(Accept(
                correlation,
                sources,
                [
                    new DiscoveredInformation(
                        firstIdentity,
                        2,
                        [new DiscoveredSourceContribution(source.SourceId, "same-name.xml", 2)],
                        "first sample"),
                    new DiscoveredInformation(
                        secondIdentity,
                        3,
                        [new DiscoveredSourceContribution(source.SourceId, "same-name.xml", 3)],
                        "second sample")
                ]));
        }

        public async Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
            DiscoveryOccurrenceLookup lookup,
            CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _occurrenceCallCount);
            lock (_gate)
            {
                Lookups.Add(lookup);
            }

            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximumConcurrentRequests(active);

            try
            {
                if (callNumber == 1)
                {
                    _firstRequestStarted.TrySetResult();
                    await _releaseFirstRequest.Task;
                }

                return AcceptOccurrence(
                    lookup.Identity,
                    lookup.GlobalOrdinal,
                    lookup.TotalOccurrenceCount,
                    _sourceId,
                    lookup.Identity.StructuralPath.Contains("first", StringComparison.Ordinal)
                        ? "first identity value"
                        : "second identity value");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                if (callNumber == 2)
                {
                    _secondRequestCompleted.TrySetResult();
                }
            }
        }

        public void CompleteFirstRequest()
        {
            _releaseFirstRequest.TrySetResult();
        }

        private void UpdateMaximumConcurrentRequests(int activeRequests)
        {
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentRequests);
                if (activeRequests <= maximum
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrentRequests,
                        activeRequests,
                        maximum) == maximum)
                {
                    return;
                }
            }
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }

    private sealed class StubSourceIntakeClient(SourceIntakeClientResult result)
        : ISourceIntakeClient
    {
        public Task<SourceIntakeClientResult> LoadAsync(
            SourceSelectionKind selectionKind,
            string path,
            SourceLoadSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(result);

        public Task<SourceRefreshClientResult> RefreshAsync(
            LoadedSourceContract source,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new SourceRefreshClientResult(
                    true,
                    source,
                    FailureCode: null,
                    FailureDescription: null));
    }

    private sealed class StubProcessingHostSupervisor : IProcessingHostSupervisor
    {
        public ProcessingHostLifecycleSnapshot Current { get; } = new(
            ProcessingHostLifecycleState.Ready,
            HostDesired: true,
            ProcessId: 1234,
            FailureCode: null);

        public event EventHandler<ProcessingHostLifecycleSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessingHostLifecycleSnapshot> EnsureAvailableAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<bool> RequestOperationCancellationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
