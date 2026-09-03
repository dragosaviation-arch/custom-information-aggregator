using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Operations;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class OperationOutcomeSemanticsTests
{
    [TestMethod]
    public void CompletedItemsClassifySuccessfulAndPartialOutcomes()
    {
        var correlation = OperationCorrelation.CreateNew();

        var successful = OperationCompletion.FromCompletedItems(
            correlation,
            [OperationItemStatus.ProcessedSuccessfully("source-a")]);
        var partial = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "source-read-failed"),
                OperationItemStatus.Unprocessed("source-c", "dependency-unavailable")
            ]);

        Assert.AreEqual(OperationOutcome.CompletedSuccessfully, successful.Outcome);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, partial.Outcome);
    }

    [TestMethod]
    public void WholeFailureAndCancellationRemainDistinctFromItemIssues()
    {
        var correlation = OperationCorrelation.CreateNew();
        var items = new[]
        {
            OperationItemStatus.ProcessedSuccessfully("source-a"),
            OperationItemStatus.Unprocessed("source-b", "operation-stopped")
        };

        var failed = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            items);
        var cancelled = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Cancelled,
            items);

        Assert.AreEqual(OperationOutcome.Failed, failed.Outcome);
        Assert.AreEqual(OperationOutcome.Cancelled, cancelled.Outcome);
    }

    [TestMethod]
    public void FailedAndUnprocessedItemsCannotBeRepresentedAsSuccessfulResults()
    {
        var completion = OperationCompletion.FromCompletedItems(
            OperationCorrelation.CreateNew(),
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed"),
                OperationItemStatus.Unprocessed("source-c", "not-scheduled")
            ]);

        Assert.IsTrue(completion.CanRetainResultFor("source-a"));
        Assert.IsFalse(completion.CanRetainResultFor("source-b"));
        Assert.IsFalse(completion.CanRetainResultFor("source-c"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => completion.CreateResultContext("source-b"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => completion.CreateResultContext("source-c"));
    }

    [TestMethod]
    public void IndependentFailureDoesNotBlockUnrelatedSuccessfulItem()
    {
        var completion = OperationCompletion.FromCompletedItems(
            OperationCorrelation.CreateNew(),
            [
                OperationItemStatus.Failed("source-a", "parse-failed"),
                OperationItemStatus.ProcessedSuccessfully("source-b")
            ]);

        Assert.AreEqual(OperationOutcome.CompletedWithIssues, completion.Outcome);
        Assert.IsTrue(completion.AreDependenciesSatisfiedFor("source-b"));
        Assert.IsTrue(completion.CanRetainResultFor("source-b"));
    }

    [TestMethod]
    public void FailurePropagationIsLimitedToDeclaredDependencies()
    {
        var completion = OperationCompletion.FromCompletedItems(
            OperationCorrelation.CreateNew(),
            [
                OperationItemStatus.Failed("source-a", "parse-failed"),
                OperationItemStatus.Unprocessed(
                    "dependent-result",
                    "dependency-unavailable",
                    ["source-a"]),
                OperationItemStatus.ProcessedSuccessfully("independent-result")
            ]);

        Assert.IsFalse(completion.AreDependenciesSatisfiedFor("dependent-result"));
        Assert.IsTrue(completion.AreDependenciesSatisfiedFor("independent-result"));
        Assert.IsTrue(completion.CanRetainResultFor("independent-result"));
    }

    [TestMethod]
    public void SuccessfulItemCannotDependOnFailedOrUnprocessedItem()
    {
        var correlation = OperationCorrelation.CreateNew();

        Assert.ThrowsExactly<ArgumentException>(
            () => OperationCompletion.FromCompletedItems(
                correlation,
                [
                    OperationItemStatus.Failed("source-a", "parse-failed"),
                    OperationItemStatus.ProcessedSuccessfully(
                        "dependent-result",
                        ["source-a"])
                ]));
    }

    [TestMethod]
    public void PartialResultRetainsOriginatingIncompleteContext()
    {
        var correlation = OperationCorrelation.CreateNew();
        var completion = OperationCompletion.FromCompletedItems(
            correlation,
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed")
            ]);

        var context = completion.CreateResultContext("source-a");

        Assert.AreEqual(correlation, context.OriginatingOperation);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, context.OriginatingOutcome);
        Assert.AreEqual("source-a", context.OriginatingItem.ItemId);
        Assert.IsTrue(context.OriginatingOperationWasIncomplete);
    }

    [TestMethod]
    public void SharedCompletionAndResultContextRoundTripThroughStrictJson()
    {
        var completion = OperationCompletion.FromCompletedItems(
            OperationCorrelation.CreateNew(),
            [
                OperationItemStatus.ProcessedSuccessfully("source-a"),
                OperationItemStatus.Failed("source-b", "parse-failed")
            ]);
        var resultContext = completion.CreateResultContext("source-a");

        var completionJson = JsonSerializer.Serialize(completion, SerializerOptions);
        var resultJson = JsonSerializer.Serialize(resultContext, SerializerOptions);
        var completionRoundTrip = JsonSerializer.Deserialize<OperationCompletion>(
            completionJson,
            SerializerOptions);
        var resultRoundTrip = JsonSerializer.Deserialize<OperationResultContext>(
            resultJson,
            SerializerOptions);

        Assert.IsNotNull(completionRoundTrip);
        Assert.IsNotNull(resultRoundTrip);
        Assert.AreEqual(completion.Correlation, completionRoundTrip.Correlation);
        Assert.AreEqual(completion.Outcome, completionRoundTrip.Outcome);
        Assert.HasCount(2, completionRoundTrip.Items);
        Assert.AreEqual(resultContext.OriginatingOperation, resultRoundTrip.OriginatingOperation);
        Assert.AreEqual(resultContext.OriginatingOutcome, resultRoundTrip.OriginatingOutcome);
        Assert.AreEqual(resultContext.OriginatingItem.ItemId, resultRoundTrip.OriginatingItem.ItemId);
    }

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
