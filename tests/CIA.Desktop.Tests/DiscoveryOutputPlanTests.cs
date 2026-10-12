using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;
using CIA.Desktop.Discovery;

namespace CIA.Desktop.Tests;

[TestClass]
public sealed class DiscoveryOutputPlanTests
{
    [TestMethod]
    public void EveryValidParentHasBaselinePlanBeforeAnyOutputModelIsAvailable()
    {
        var first = SourceSetId.CreateNew();
        var second = SourceSetId.CreateNew();
        var plan = new ActiveDiscoveryOutputPlan();

        plan.Synchronize([first, second], [], highestExistingSetNumber: 2);

        Assert.HasCount(2, plan.Current.ParentPlans);
        CollectionAssert.AreEqual(
            new[] { first, second },
            plan.Current.ParentPlans.Select(parent => parent.ParentSourceSetId).ToArray());
        Assert.IsTrue(plan.Current.ParentPlans.All(parent => parent.OutputSets.Count == 0));
    }

    [TestMethod]
    public void BaselineIsEnabledLosslessMinimalAndContainsNoDuplicateModelsOrSources()
    {
        var specification = CreateSpecification(("Set 1", 1, true));

        var proposals = DiscoveryOutputPlanBuilder.Create(specification);
        var plan = new ActiveDiscoveryOutputPlan();
        plan.Synchronize(proposals, highestExistingSetNumber: 1);

        Assert.HasCount(2, proposals);
        Assert.HasCount(2, plan.Current.OutputSets);
        Assert.IsTrue(plan.Current.OutputSets.All(set => set.IsEnabled));
        CollectionAssert.AreEquivalent(
            specification.Datasets.Single().Fields
                .Select(field => field.RowFamilyId)
                .Distinct()
                .ToArray(),
            plan.Current.OutputSets.SelectMany(set => set.RowFamilyIds).ToArray());
        Assert.AreEqual(
            proposals.Count,
            proposals.Select(proposal => proposal.ModelKey).Distinct().Count());
        Assert.IsFalse(typeof(DiscoveryOutputSetDefinition).GetProperties().Any(property =>
            property.Name.Contains("Source", StringComparison.Ordinal)
            && property.Name != nameof(DiscoveryOutputSetDefinition.ParentSourceSetId)));
        Assert.AreNotEqual(typeof(OutputSetId), typeof(RowFamilyId));
    }

    [TestMethod]
    public void ParentPlansAreIndependentAndNumbersContinueGloballyInDeterministicOrder()
    {
        var specification = CreateSpecification(
            ("Set 1", 1, true),
            ("Set 2", 2, false));
        var plan = new ActiveDiscoveryOutputPlan();

        plan.Synchronize(
            DiscoveryOutputPlanBuilder.Create(specification),
            highestExistingSetNumber: 2);

        var sets = plan.Current.OutputSets;
        Assert.HasCount(3, sets);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, sets.Select(set => set.SetNumber).ToArray());
        Assert.AreEqual(2, sets.Select(set => set.ParentSourceSetId).Distinct().Count());
        var unchangedSecondParent = sets.Where(set =>
            set.ParentSourceSetId == specification.Datasets[1].SourceSetId).ToArray();
        Assert.IsTrue(plan.SetEnabled(sets[0].OutputSetId, isEnabled: false));
        Assert.IsTrue(unchangedSecondParent.All(expected => plan.Current.OutputSets.Single(set =>
            set.OutputSetId == expected.OutputSetId).IsEnabled));
        foreach (var parent in sets.GroupBy(set => set.ParentSourceSetId))
        {
            CollectionAssert.AreEqual(
                Enumerable.Range(1, parent.Count()).ToArray(),
                parent.Select(set => set.ModelOrder).ToArray());
            Assert.IsTrue(parent.All(set => set.RowFamilyIds.All(family =>
                family.SourceSetId == parent.Key)));
        }
    }

    [TestMethod]
    public void EnableRenameAndRefreshPreserveStableIdentitySettingsAndNumber()
    {
        var specification = CreateSpecification(("Set 1", 1, true));
        var proposals = DiscoveryOutputPlanBuilder.Create(specification);
        var plan = new ActiveDiscoveryOutputPlan();
        plan.Synchronize(proposals, highestExistingSetNumber: 1);
        var repeated = plan.Current.OutputSets.Single(set =>
            set.RowFamilyIds.Single().Kind == RowFamilyKind.Repeated);

        Assert.IsTrue(plan.SetEnabled(repeated.OutputSetId, isEnabled: false));
        Assert.IsTrue(plan.CanRename(
            repeated.OutputSetId,
            "Book records",
            ["Set 1"],
            out var normalizedName,
            out var failureReason), failureReason);
        Assert.IsTrue(plan.Rename(repeated.OutputSetId, normalizedName));

        plan.Synchronize(proposals, highestExistingSetNumber: 1);
        var refreshed = plan.Current.OutputSets.Single(set =>
            set.ModelKey == repeated.ModelKey);

        Assert.AreEqual(repeated.OutputSetId, refreshed.OutputSetId);
        Assert.AreEqual(repeated.SetNumber, refreshed.SetNumber);
        Assert.AreEqual("Book records", refreshed.Name);
        Assert.IsFalse(refreshed.IsEnabled);
        Assert.IsFalse(plan.CanRename(
            refreshed.OutputSetId,
            "Set 1",
            ["Set 1"],
            out _,
            out _));
    }

    [TestMethod]
    public void RefreshRetiresInvalidModelsAndAllocatesNewIdentityOnlyForNewStableKey()
    {
        var initialSpecification = CreateSpecification(("Set 1", 1, true));
        var initialProposals = DiscoveryOutputPlanBuilder.Create(initialSpecification);
        var plan = new ActiveDiscoveryOutputPlan();
        plan.Synchronize(initialProposals, highestExistingSetNumber: 1);
        var retained = plan.Current.OutputSets.Single(set =>
            set.RowFamilyIds.Single().Kind == RowFamilyKind.Repeated);
        var retainedProposal = initialProposals.Single(proposal =>
            proposal.ModelKey == retained.ModelKey);
        var nestedFamily = new RowFamilyId(
            retained.ParentSourceSetId,
            RowFamilyKind.Repeated,
            "/root/book/review");
        var replacementProposals = new[]
        {
            retainedProposal with { ModelOrder = 1 },
            new DiscoveryOutputModelProposal(
                retained.ParentSourceSetId,
                new DiscoveryOutputModelKey("family-projection:v1:nested-review"),
                "Set 1 — review",
                2,
                [nestedFamily])
        };

        plan.Synchronize(replacementProposals, highestExistingSetNumber: 1);

        var refreshed = plan.Current.OutputSets;
        Assert.HasCount(2, refreshed);
        Assert.AreEqual(
            retained.OutputSetId,
            refreshed.Single(set => set.ModelKey == retained.ModelKey).OutputSetId);
        var newSet = refreshed.Single(set => set.ModelKey != retained.ModelKey);
        Assert.AreNotEqual(retained.OutputSetId, newSet.OutputSetId);
        Assert.IsGreaterThan(retained.SetNumber, newSet.SetNumber);
        Assert.IsFalse(refreshed.Any(set =>
            set.RowFamilyIds.Single().Kind == RowFamilyKind.Root));
    }

    private static DatabaseBuildSpecification CreateSpecification(
        params (string Name, int Ordinal, bool IncludeRepeated, bool IncludeNested)[] definitions)
    {
        if (definitions.Length == 0)
        {
            definitions = [("Set 1", 1, true, false)];
        }

        var datasets = definitions.Select(definition =>
        {
            var sourceSetId = SourceSetId.CreateNew();
            var source = new LoadedSourceContract(
                SourceId.CreateNew(),
                sourceSetId,
                Path.GetFullPath($"plan-{definition.Ordinal}.xml"),
                true,
                LoadedSourceStatus.Ready,
                LoadedSourceKind.XmlFile);
            var root = new RowFamilyDefinition(
                new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/root"),
                null);
            var repeated = new RowFamilyDefinition(
                new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, "/root/book"),
                root.Id);
            var nested = new RowFamilyDefinition(
                new RowFamilyId(sourceSetId, RowFamilyKind.Repeated, "/root/book/review"),
                repeated.Id);
            var families = new List<RowFamilyDefinition> { root };
            if (definition.IncludeRepeated)
            {
                families.Add(repeated);
            }

            if (definition.IncludeNested)
            {
                families.Add(nested);
            }

            var fields = families.Select((family, index) => new DatabaseFieldMapping(
                family.Id,
                $"Field {index + 1}",
                false,
                [
                    new DiscoveryInformationIdentity(
                        sourceSetId,
                        $"{family.Id.AnchorPath}/value{index + 1}",
                        $"value{index + 1}",
                        SourceValueCandidateKind.Element,
                        $"{family.Id.AnchorPath}/value{index + 1}")
                ])).ToArray();
            return new DatabaseDatasetBuildSpecification(
                sourceSetId,
                definition.Name,
                definition.Ordinal,
                RepeatedDataLayout.StructuralRows,
                [source],
                families,
                fields);
        }).ToArray();
        return new DatabaseBuildSpecification(datasets);
    }

    private static DatabaseBuildSpecification CreateSpecification(
        params (string Name, int Ordinal, bool IncludeRepeated)[] definitions) =>
        CreateSpecification(definitions.Select(definition =>
            (definition.Name, definition.Ordinal, definition.IncludeRepeated, false)).ToArray());
}
