using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Export;

public sealed class ProcessingHostWorkbookExportClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostWorkbookExportClient> logger) : IWorkbookExportClient
{
    public async Task<WorkbookExportClientResult> ExportAsync(
        OperationCorrelation correlation,
        ExtractionResultSummary extractionResult,
        ExportConfigurationSnapshot configuration,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(extractionResult);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, "processing-host-unavailable");
            }

            var response = await requestClient.RequestWorkbookExportAsync(
                    correlation,
                    extractionResult,
                    configuration,
                    targetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WorkbookExportClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Completion,
                response.Workbook,
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
                "Export {OperationId} could not be completed by the Processing Host",
                correlation.OperationId);
            return Reject(correlation, "processing-host-unavailable");
        }
    }

    private static WorkbookExportClientResult Reject(
        OperationCorrelation correlation,
        string failureCode)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            [OperationItemStatus.Unprocessed("workbook-publication", failureCode)]);
        return new WorkbookExportClientResult(
            false,
            completion,
            Workbook: null,
            failureCode,
            "The Processing Host could not complete the workbook export.");
    }
}
