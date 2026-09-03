using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Operations;

public sealed record OperationCompletion
{
    [JsonConstructor]
    public OperationCompletion(
        OperationCorrelation correlation,
        OperationOutcome outcome,
        IReadOnlyList<OperationItemStatus> items)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(items);

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }

        var itemArray = items.ToArray();

        if (itemArray.Any(item => item is null))
        {
            throw new ArgumentException("Operation items cannot contain null values.", nameof(items));
        }

        var itemsById = itemArray.ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        ValidateDependencies(itemArray, itemsById);
        ValidateOutcome(outcome, itemArray);

        Correlation = correlation;
        Outcome = outcome;
        Items = new ReadOnlyCollection<OperationItemStatus>(itemArray);
    }

    public OperationCorrelation Correlation { get; }

    public OperationOutcome Outcome { get; }

    public IReadOnlyList<OperationItemStatus> Items { get; }

    public static OperationCompletion FromCompletedItems(
        OperationCorrelation correlation,
        IEnumerable<OperationItemStatus> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var itemArray = items.ToArray();

        if (itemArray.Any(item => item is null))
        {
            throw new ArgumentException("Operation items cannot contain null values.", nameof(items));
        }

        var outcome = itemArray.Any(item => item.State != OperationItemState.ProcessedSuccessfully)
            ? OperationOutcome.CompletedWithIssues
            : OperationOutcome.CompletedSuccessfully;

        return new OperationCompletion(correlation, outcome, itemArray);
    }

    public static OperationCompletion FromTerminalOutcome(
        OperationCorrelation correlation,
        OperationOutcome outcome,
        IEnumerable<OperationItemStatus> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (outcome is OperationOutcome.CompletedSuccessfully
            or OperationOutcome.CompletedWithIssues)
        {
            throw new ArgumentException(
                "Completed operation outcomes must be classified from their item states.",
                nameof(outcome));
        }

        return new OperationCompletion(correlation, outcome, items.ToArray());
    }

    public bool AreDependenciesSatisfiedFor(string itemId)
    {
        var item = GetItem(itemId);
        var itemsById = Items.ToDictionary(candidate => candidate.ItemId, StringComparer.Ordinal);

        return item.RequiredItemIds.All(
            dependencyId =>
                itemsById[dependencyId].State == OperationItemState.ProcessedSuccessfully);
    }

    public bool CanRetainResultFor(string itemId)
    {
        return GetItem(itemId).State == OperationItemState.ProcessedSuccessfully;
    }

    public OperationResultContext CreateResultContext(string itemId)
    {
        var item = GetItem(itemId);

        if (item.State != OperationItemState.ProcessedSuccessfully)
        {
            throw new InvalidOperationException(
                $"Operation item '{itemId}' did not produce a valid result.");
        }

        return new OperationResultContext(Correlation, Outcome, item);
    }

    private OperationItemStatus GetItem(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        return Items.FirstOrDefault(
                candidate => candidate.ItemId.Equals(itemId, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Operation item '{itemId}' is not part of this completion.",
                nameof(itemId));
    }

    private static void ValidateDependencies(
        IReadOnlyList<OperationItemStatus> items,
        IReadOnlyDictionary<string, OperationItemStatus> itemsById)
    {
        foreach (var item in items)
        {
            foreach (var dependencyId in item.RequiredItemIds)
            {
                if (!itemsById.TryGetValue(dependencyId, out var dependency))
                {
                    throw new ArgumentException(
                        $"Operation item '{item.ItemId}' references unknown required item '{dependencyId}'.",
                        nameof(items));
                }

                if (item.State == OperationItemState.ProcessedSuccessfully
                    && dependency.State != OperationItemState.ProcessedSuccessfully)
                {
                    throw new ArgumentException(
                        $"Operation item '{item.ItemId}' cannot be successful because required item '{dependencyId}' was not successfully processed.",
                        nameof(items));
                }
            }
        }
    }

    private static void ValidateOutcome(
        OperationOutcome outcome,
        IReadOnlyList<OperationItemStatus> items)
    {
        var hasItemIssue = items.Any(
            item => item.State != OperationItemState.ProcessedSuccessfully);

        if (outcome == OperationOutcome.CompletedSuccessfully && hasItemIssue)
        {
            throw new ArgumentException(
                "An operation with failed or unprocessed items cannot be completed successfully.",
                nameof(outcome));
        }

        if (outcome == OperationOutcome.CompletedWithIssues && !hasItemIssue)
        {
            throw new ArgumentException(
                "An operation completed with issues must contain a failed or unprocessed item.",
                nameof(outcome));
        }
    }
}

public sealed record OperationResultContext
{
    [JsonConstructor]
    public OperationResultContext(
        OperationCorrelation originatingOperation,
        OperationOutcome originatingOutcome,
        OperationItemStatus originatingItem)
    {
        ArgumentNullException.ThrowIfNull(originatingOperation);
        ArgumentNullException.ThrowIfNull(originatingItem);

        if (!Enum.IsDefined(originatingOutcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(originatingOutcome),
                originatingOutcome,
                null);
        }

        if (originatingItem.State != OperationItemState.ProcessedSuccessfully)
        {
            throw new ArgumentException(
                "A retained operation result must originate from a successfully processed item.",
                nameof(originatingItem));
        }

        OriginatingOperation = originatingOperation;
        OriginatingOutcome = originatingOutcome;
        OriginatingItem = originatingItem;
    }

    public OperationCorrelation OriginatingOperation { get; }

    public OperationOutcome OriginatingOutcome { get; }

    public OperationItemStatus OriginatingItem { get; }

    [JsonIgnore]
    public bool OriginatingOperationWasIncomplete =>
        OriginatingOutcome != OperationOutcome.CompletedSuccessfully;
}
