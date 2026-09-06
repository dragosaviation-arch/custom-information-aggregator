using CIA.Contracts.Ipc;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Desktop.Hosting;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.Discovery;

public sealed class ProcessingHostDiscoveryClient(
    IProcessingHostSupervisor hostSupervisor,
    ProcessingHostSupervisor requestClient,
    ILogger<ProcessingHostDiscoveryClient> logger) : IDiscoveryClient
{
    public async Task<DiscoveryClientResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(sources);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return Reject(correlation, sources, "processing-host-unavailable");
            }

            var response = await requestClient.RequestDiscoveryAsync(
                    correlation,
                    sources,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DiscoveryClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Information,
                response.Issues,
                response.Completion,
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
                "Discovery could not be completed by the Processing Host for operation {OperationId}",
                correlation.OperationId);
            return Reject(correlation, sources, "processing-host-unavailable");
        }
    }

    public async Task<DiscoveryOccurrenceClientResult> GetOccurrenceAsync(
        OperationId discoveryOperationId,
        string informationType,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectOccurrence("processing-host-unavailable");
            }

            var response = await requestClient.RequestDiscoveryOccurrenceAsync(
                    discoveryOperationId,
                    informationType,
                    ordinal,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DiscoveryOccurrenceClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Occurrence,
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
                "Discovery occurrence {OccurrenceOrdinal} for {InformationType} could not be retrieved from operation {OperationId}",
                ordinal,
                informationType,
                discoveryOperationId);
            return RejectOccurrence("processing-host-unavailable");
        }
    }

    private static DiscoveryClientResult Reject(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        string failureCode)
    {
        var completion = OperationCompletion.FromTerminalOutcome(
            correlation,
            OperationOutcome.Failed,
            sources.Select(source => OperationItemStatus.Unprocessed(
                source.SourceId.ToString(),
                failureCode)).ToArray());
        return new DiscoveryClientResult(
            false,
            Array.Empty<CIA.Contracts.Discovery.DiscoveredInformation>(),
            Array.Empty<CIA.Contracts.Discovery.DiscoverySourceIssue>(),
            completion,
            failureCode,
            "The Processing Host could not complete Discovery.");
    }

    private static DiscoveryOccurrenceClientResult RejectOccurrence(string failureCode)
    {
        return new DiscoveryOccurrenceClientResult(
            false,
            Occurrence: null,
            failureCode,
            "The Processing Host could not retrieve the Discovery occurrence.");
    }
}
