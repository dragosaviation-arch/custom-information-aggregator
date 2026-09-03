using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Operations;

public enum OperationItemState
{
    ProcessedSuccessfully = 1,
    Failed = 2,
    Unprocessed = 3
}

public sealed record OperationItemStatus
{
    [JsonConstructor]
    public OperationItemStatus(
        string itemId,
        OperationItemState state,
        IReadOnlyList<string> requiredItemIds,
        string? failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(requiredItemIds);

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        if (failureCode is not null && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException(
                "An item failure code cannot be empty or whitespace.",
                nameof(failureCode));
        }

        if (state == OperationItemState.ProcessedSuccessfully && failureCode is not null)
        {
            throw new ArgumentException(
                "A successfully processed item cannot contain failure information.",
                nameof(failureCode));
        }

        var dependencies = requiredItemIds.ToArray();

        if (dependencies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Required item IDs cannot be empty or whitespace.",
                nameof(requiredItemIds));
        }

        if (dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
        {
            throw new ArgumentException(
                "Required item IDs must be unique.",
                nameof(requiredItemIds));
        }

        if (dependencies.Contains(itemId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "An operation item cannot depend on itself.",
                nameof(requiredItemIds));
        }

        ItemId = itemId;
        State = state;
        RequiredItemIds = new ReadOnlyCollection<string>(dependencies);
        FailureCode = failureCode;
    }

    public string ItemId { get; }

    public OperationItemState State { get; }

    public IReadOnlyList<string> RequiredItemIds { get; }

    public string? FailureCode { get; }

    public static OperationItemStatus ProcessedSuccessfully(
        string itemId,
        IEnumerable<string>? requiredItemIds = null)
    {
        return Create(
            itemId,
            OperationItemState.ProcessedSuccessfully,
            requiredItemIds,
            failureCode: null);
    }

    public static OperationItemStatus Failed(
        string itemId,
        string? failureCode = null,
        IEnumerable<string>? requiredItemIds = null)
    {
        return Create(itemId, OperationItemState.Failed, requiredItemIds, failureCode);
    }

    public static OperationItemStatus Unprocessed(
        string itemId,
        string? failureCode = null,
        IEnumerable<string>? requiredItemIds = null)
    {
        return Create(itemId, OperationItemState.Unprocessed, requiredItemIds, failureCode);
    }

    private static OperationItemStatus Create(
        string itemId,
        OperationItemState state,
        IEnumerable<string>? requiredItemIds,
        string? failureCode)
    {
        return new OperationItemStatus(
            itemId,
            state,
            requiredItemIds?.ToArray() ?? [],
            failureCode);
    }
}
