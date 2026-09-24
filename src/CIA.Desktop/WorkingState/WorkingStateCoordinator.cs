using CIA.Contracts.Operations;
using CIA.Contracts.WorkingState;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Sources;
using CIA.Desktop.Workflow;
using Microsoft.Extensions.Logging;

namespace CIA.Desktop.WorkingState;

public interface IWorkingStateCoordinator
{
    WorkflowOperationReadiness EvaluateSaveReadiness();

    WorkflowOperationReadiness EvaluateRestoreReadiness(string? packagePath);

    Task<WorkingStateCoordinatorResult> SaveAsync(
        string targetPath,
        CancellationToken cancellationToken = default);

    Task<WorkingStateCoordinatorResult> SaveAsync(
        string targetPath,
        WorkingStatePublicationMode publicationMode,
        CancellationToken cancellationToken = default);

    Task<WorkingStateCoordinatorResult> RestoreAsync(
        string packagePath,
        CancellationToken cancellationToken = default);
}

public sealed class WorkingStateCoordinator(
    ActiveLoadedSourceSet sourceSet,
    ActiveDiscoveryConfiguration discoveryConfiguration,
    DatabaseBuildCoordinator databaseBuildCoordinator,
    IApplicationWorkflowCoordinator workflowCoordinator,
    IWorkingStateClient client,
    ILogger<WorkingStateCoordinator> logger) : IWorkingStateCoordinator
{
    public WorkflowOperationReadiness EvaluateSaveReadiness()
    {
        var workflowReadiness = WorkflowOperationReadiness.FromWorkflow(
            workflowCoordinator.EvaluateOperationPrerequisites(
                WorkflowOperationKind.WorkingStateSave));
        if (!workflowReadiness.NormalOperationReady)
        {
            return workflowReadiness;
        }

        return EvaluateDatabaseSaveCoherence();
    }

    public WorkflowOperationReadiness EvaluateRestoreReadiness(string? packagePath)
    {
        var workflowReadiness = WorkflowOperationReadiness.FromWorkflow(
            workflowCoordinator.EvaluateOperationPrerequisites(
                WorkflowOperationKind.WorkingStateRestore));
        if (!workflowReadiness.NormalOperationReady)
        {
            return workflowReadiness;
        }

        return string.IsNullOrWhiteSpace(packagePath)
            ? WorkflowOperationReadiness.RequiresUserInput(
                "Select a .cia working-state package before restoring working state.")
            : WorkflowOperationReadiness.Ready();
    }

    public Task<WorkingStateCoordinatorResult> SaveAsync(
        string targetPath,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            targetPath,
            WorkingStatePublicationMode.ReplaceExisting,
            cancellationToken);

    public async Task<WorkingStateCoordinatorResult> SaveAsync(
        string targetPath,
        WorkingStatePublicationMode publicationMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(publicationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(publicationMode));
        }

        var readiness = EvaluateSaveReadiness();
        if (!readiness.NormalOperationReady)
        {
            return WorkingStateCoordinatorResult.Reject(
                readiness.UnavailableReason ?? "Working-state save is not currently ready.");
        }

        var begin = await workflowCoordinator.BeginOperationAsync(
                WorkflowOperationKind.WorkingStateSave,
                cancellationToken)
            .ConfigureAwait(false);
        if (!begin.Accepted || begin.Operation is null)
        {
            return WorkingStateCoordinatorResult.Reject(
                begin.Rejection?.Reason ?? "Working-state save could not start.");
        }

        try
        {
            var coherence = EvaluateDatabaseSaveCoherence();
            if (!coherence.NormalOperationReady)
            {
                workflowCoordinator.CompleteOperation(
                    begin.Operation.OperationId,
                    OperationOutcome.Failed);
                return WorkingStateCoordinatorResult.Reject(
                    coherence.UnavailableReason
                        ?? "Working-state save is not currently ready.");
            }

            var snapshot = CaptureSnapshot();
            var result = await client.SaveAsync(
                    begin.Operation,
                    targetPath,
                    snapshot,
                    publicationMode,
                    cancellationToken)
                .ConfigureAwait(false);
            var completion = workflowCoordinator.CompleteOperation(result.Completion);
            return result.Accepted && completion.Accepted && result.Manifest is not null
                ? WorkingStateCoordinatorResult.Accept(result.Manifest)
                : WorkingStateCoordinatorResult.Reject(
                    result.FailureDescription ?? completion.Rejection?.Reason ?? "Working-state save failed.");
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
            logger.LogError(exception, "Working-state save coordination failed");
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
            return WorkingStateCoordinatorResult.Reject("Working-state save failed unexpectedly.");
        }
    }

    public async Task<WorkingStateCoordinatorResult> RestoreAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var readiness = EvaluateRestoreReadiness(packagePath);
        if (!readiness.NormalOperationReady)
        {
            return WorkingStateCoordinatorResult.Reject(
                readiness.UnavailableReason ?? "Working-state restore is not currently ready.");
        }

        var begin = await workflowCoordinator.BeginOperationAsync(
                WorkflowOperationKind.WorkingStateRestore,
                cancellationToken)
            .ConfigureAwait(false);
        if (!begin.Accepted || begin.Operation is null)
        {
            return WorkingStateCoordinatorResult.Reject(
                begin.Rejection?.Reason ?? "Working-state restore could not start.");
        }

        try
        {
            var result = await client.RestoreAsync(
                    begin.Operation,
                    packagePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Accepted || result.Manifest is null)
            {
                workflowCoordinator.CompleteOperation(result.Completion);
                return WorkingStateCoordinatorResult.Reject(
                    result.FailureDescription ?? "Working-state restore failed.");
            }

            var snapshot = result.Manifest.Snapshot;
            WorkingStateContractValidator.Validate(snapshot);
            sourceSet.RestoreWorkingState(
                snapshot.SourceSets,
                snapshot.ActiveSourceSetId,
                snapshot.Sources);
            discoveryConfiguration.RestoreWorkingState(
                snapshot.DiscoveryConfiguration,
                snapshot.DatabaseTagOverrides);
            databaseBuildCoordinator.AdoptRestoredGeneration(snapshot.DatabaseGeneration);

            var stateResult = workflowCoordinator.RecordWorkingStateRestored(
                sourceSet.CreateIncludedReadySnapshot().Count > 0,
                snapshot.DatabaseGeneration is not null);
            if (!stateResult.Accepted)
            {
                throw new InvalidOperationException(
                    stateResult.Rejection?.Reason ?? "Restored workflow state was rejected.");
            }

            var completion = workflowCoordinator.CompleteOperation(result.Completion);
            return completion.Accepted
                ? WorkingStateCoordinatorResult.Accept(result.Manifest)
                : WorkingStateCoordinatorResult.Reject(
                    completion.Rejection?.Reason ?? "Restored workflow completion was rejected.");
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
            logger.LogError(exception, "Working-state restore coordination failed");
            workflowCoordinator.CompleteOperation(
                begin.Operation.OperationId,
                OperationOutcome.Failed);
            return WorkingStateCoordinatorResult.Reject("Working-state restore failed unexpectedly.");
        }
    }

    public WorkingStateSnapshot CaptureSnapshot()
    {
        var sourceSets = sourceSet.CreateWorkingStateSourceSetSnapshot();
        var currentConfiguration = discoveryConfiguration.Current;
        var layouts = currentConfiguration.SourceSets.ToDictionary(
            sourceSet => sourceSet.SourceSetId,
            sourceSet => sourceSet.RepeatedDataLayout);
        var configuration = new CIA.Contracts.Discovery.DiscoveryConfigurationSnapshot(
            currentConfiguration.Items,
            sourceSets
                .Select(sourceSet => new CIA.Contracts.Discovery.SourceSetDiscoveryConfiguration(
                    sourceSet.SourceSetId,
                    layouts.GetValueOrDefault(
                        sourceSet.SourceSetId,
                        CIA.Contracts.Discovery.RepeatedDataLayout.AlignRepeatedGroupsByPosition)))
                .ToArray());
        return new WorkingStateSnapshot(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            databaseBuildCoordinator.CurrentGeneration,
            sourceSets,
            sourceSet.ActiveSourceSet?.SourceSetId,
            sourceSet.CreateWorkingStateSourceSnapshot(),
            configuration,
            discoveryConfiguration.DatabaseTagOverridesByIdentity
                .OrderBy(item => item.Key.SourceSetId.Value)
                .ThenBy(item => item.Key.StructuralIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Key.InformationType, StringComparer.Ordinal)
                .Select(item => new WorkingStateDatabaseTagOverride(item.Key, item.Value))
                .ToArray());
    }

    private WorkflowOperationReadiness EvaluateDatabaseSaveCoherence()
    {
        return databaseBuildCoordinator.CurrentGeneration is not null
            && workflowCoordinator.Current.Database != WorkflowArtifactStatus.Current
                ? WorkflowOperationReadiness.Unavailable(
                    workflowPrerequisitesSatisfied: true,
                    WorkflowRejectionCode.OperationFailed,
                    "The published Database is out of date. Update or rebuild the Database before saving working state, or remove the published Database to save without one.")
                : WorkflowOperationReadiness.Ready();
    }
}

public sealed record WorkingStateCoordinatorResult(
    bool Accepted,
    WorkingStateManifest? Manifest,
    string? FailureDescription)
{
    public static WorkingStateCoordinatorResult Accept(WorkingStateManifest manifest) =>
        new(true, manifest, FailureDescription: null);

    public static WorkingStateCoordinatorResult Reject(string description) =>
        new(false, Manifest: null, description);
}
