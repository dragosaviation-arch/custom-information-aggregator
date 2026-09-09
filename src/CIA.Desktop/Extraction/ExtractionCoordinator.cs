using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Desktop.Database;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Extraction;

public sealed class ExtractionCoordinator(
    DatabaseBuildCoordinator databaseBuildCoordinator,
    IApplicationWorkflowCoordinator workflowCoordinator,
    IExtractionClient extractionClient,
    ILogger<ExtractionCoordinator> logger)
{
    private readonly object _stateGate = new();
    private ExtractionResultSummary? _currentResult;
    private OperationCompletion? _currentCompletion;

    public ExtractionResultSummary? CurrentResult
    {
        get
        {
            lock (_stateGate)
            {
                return _currentResult;
            }
        }
    }

    public OperationCompletion? CurrentCompletion
    {
        get
        {
            lock (_stateGate)
            {
                return _currentCompletion;
            }
        }
    }

    public event EventHandler<ExtractionResultSummary>? PublishedResultChanged;

    public bool CanExtract()
    {
        return workflowCoordinator.Current.Database == WorkflowArtifactStatus.Current
            && workflowCoordinator.Current.ActiveOperation is null
            && databaseBuildCoordinator.CurrentGeneration is not null;
    }

    public async Task<WorkflowCommandResult> ExtractAsync(
        CancellationToken cancellationToken = default)
    {
        var databaseGeneration = databaseBuildCoordinator.CurrentGeneration;
        if (!CanExtract() || databaseGeneration is null)
        {
            return WorkflowCommandResult.Reject(
                WorkflowRejectionCode.DatabaseNotCurrent,
                "Extraction requires the active published Database to be current.");
        }

        var begin = await workflowCoordinator
            .BeginOperationAsync(WorkflowOperationKind.Extraction, cancellationToken)
            .ConfigureAwait(false);
        if (!begin.Accepted || begin.Operation is null)
        {
            return begin;
        }

        try
        {
            var result = await extractionClient
                .ExtractAsync(begin.Operation, databaseGeneration, cancellationToken)
                .ConfigureAwait(false);
            if (result.Accepted
                && (result.PublishedResult is null
                    || result.PublishedResult.OperationId != begin.Operation.OperationId
                    || !DatabaseGenerationsEqual(
                        databaseGeneration,
                        result.PublishedResult.DatabaseGeneration)))
            {
                workflowCoordinator.CompleteOperation(
                    begin.Operation.OperationId,
                    OperationOutcome.Failed);
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationMismatch,
                    "The Extraction Result does not match its initiating Database snapshot.");
            }

            var completion = workflowCoordinator.CompleteOperation(result.Completion);
            if (!completion.Accepted)
            {
                return completion;
            }

            if (!result.Accepted || result.PublishedResult is null)
            {
                return WorkflowCommandResult.Reject(
                    WorkflowRejectionCode.OperationFailed,
                    result.FailureDescription ?? "Extraction did not publish a result.");
            }

            lock (_stateGate)
            {
                _currentResult = result.PublishedResult;
                _currentCompletion = result.Completion;
            }

            PublishedResultChanged?.Invoke(this, result.PublishedResult);
            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Extraction coordination failed for operation {OperationId}",
                begin.Operation.OperationId);
            return workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
        }
    }

    private static bool DatabaseGenerationsEqual(
        DatabaseGenerationSummary expected,
        DatabaseGenerationSummary actual)
    {
        return expected.OperationId == actual.OperationId
            && expected.ValueCount == actual.ValueCount
            && expected.Mapping.Columns.Count == actual.Mapping.Columns.Count
            && expected.Mapping.Columns.Zip(actual.Mapping.Columns).All(pair =>
                string.Equals(
                    pair.First.DatabaseTagName,
                    pair.Second.DatabaseTagName,
                    StringComparison.Ordinal)
                && pair.First.SourceInformationTypes.SequenceEqual(
                    pair.Second.SourceInformationTypes,
                    StringComparer.Ordinal));
    }
}
