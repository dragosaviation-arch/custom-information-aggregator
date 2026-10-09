using System.IO;
using CIA.Contracts.Discovery;
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
    private const int MaximumTechnicalDetailLength = 512;

    public async Task<DiscoveryClientResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(correlation, sources, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DiscoveryClientResult> RunAsync(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        IProgress<DiscoveryProgressSnapshot>? progress,
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
                    progress,
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
        catch (IpcProtocolException exception)
        {
            logger.LogWarning(
                exception,
                "Discovery received an invalid IPC response for operation {OperationId}",
                correlation.OperationId);
            return Reject(
                correlation,
                sources,
                "discovery-ipc-protocol-failure",
                "Discovery could not receive a valid response from the Processing Host.",
                CreateTechnicalDetail("IPC protocol failure", exception.Message));
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                exception,
                "Discovery lost its Processing Host connection for operation {OperationId}",
                correlation.OperationId);
            return Reject(
                correlation,
                sources,
                "discovery-ipc-transport-failure",
                "Discovery lost its connection to the Processing Host.",
                CreateTechnicalDetail("IPC transport failure", exception.Message));
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
        DiscoveryOccurrenceLookup lookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectOccurrence("processing-host-unavailable");
            }

            var response = await requestClient.RequestDiscoveryOccurrenceAsync(
                    lookup,
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
                lookup.GlobalOrdinal,
                lookup.InformationType,
                lookup.DiscoveryOperationId);
            return RejectOccurrence("processing-host-unavailable");
        }
    }

    public async Task<DiscoveryContributorClientResult> GetContributorsAsync(
        DiscoveryContributorPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var host = await hostSupervisor.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            if (host.State != ProcessingHostLifecycleState.Ready)
            {
                return RejectContributors("processing-host-unavailable");
            }

            var response = await requestClient.RequestDiscoveryContributorsAsync(
                    query,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DiscoveryContributorClientResult(
                response.Acceptance == CommandAcceptance.Accepted,
                response.Page,
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
                "Discovery contributors could not be retrieved from operation {OperationId}",
                query.DiscoveryOperationId);
            return RejectContributors("processing-host-unavailable");
        }
    }

    private static DiscoveryClientResult Reject(
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        string failureCode,
        string failureDescription = "The Processing Host could not complete Discovery.",
        string? failureTechnicalDetail = null)
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
            failureDescription)
        {
            FailureTechnicalDetail = failureTechnicalDetail
        };
    }

    private static DiscoveryOccurrenceClientResult RejectOccurrence(string failureCode)
    {
        return new DiscoveryOccurrenceClientResult(
            false,
            Occurrence: null,
            failureCode,
            "The Processing Host could not retrieve the Discovery occurrence.");
    }

    private static DiscoveryContributorClientResult RejectContributors(string failureCode)
    {
        return new DiscoveryContributorClientResult(
            false,
            Page: null,
            failureCode,
            "The Processing Host could not retrieve the Discovery contributors.");
    }

    private static string CreateTechnicalDetail(string category, string message)
    {
        var normalizedMessage = string.Join(
            ' ',
            message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var detail = $"{category}: {normalizedMessage}";
        return detail.Length <= MaximumTechnicalDetailLength
            ? detail
            : detail[..(MaximumTechnicalDetailLength - 1)] + '…';
    }
}
