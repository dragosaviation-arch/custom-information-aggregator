namespace CIA.Contracts.Operations;

public enum OperationOutcome
{
    CompletedSuccessfully = 1,
    CompletedWithIssues = 2,
    Failed = 3,
    Cancelled = 4,
    InterruptedIncomplete = 5
}
