using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Extraction;

public sealed class ProcessingHostExtractionClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostExtractionClient> logger) : IExtractionClient
{
    public async Task<ExtractionClientResult> ExtractAsync(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(databaseGeneration);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, "processing-host-unavailable");
            }

            var response = await requestClient.RequestExtractionAsync(
                    correlation,
                    databaseGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ExtractionClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Completion,
                response.PublishedResult,
                response.Failure?.Code,
                response.Failure?.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Extraction {OperationId} could not be completed by the Processing Host",
                correlation.OperationId);
            return Reject(correlation, "processing-host-unavailable");
        }
    }

    private static ExtractionClientResult Reject(
        OperationCorrelation correlation,
        string failureCode)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            [OperationItemStatus.Unprocessed("extraction-publication", failureCode)]);
        return new ExtractionClientResult(
            false,
            completion,
            PublishedResult: null,
            failureCode,
            "The Processing Host could not complete Extraction.");
    }
}
