using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using CIA.ProcessingHost.SourceInterpretation;

namespace CIA.ProcessingHost.SourceIntake;

public sealed class SourceRefreshService(
    SourceIntakeService sourceIntake,
    ISourceInterpreter sourceInterpreter)
{
    public async Task<SourceRefreshHostResult> RefreshAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ArchiveProvenance is not null)
        {
            return await RefreshArchiveMemberAsync(source, cancellationToken).ConfigureAwait(false);
        }

        var selectionKind = source.Kind == LoadedSourceKind.XmlFile
            ? SourceSelectionKind.XmlFile
            : SourceSelectionKind.Archive;
        var intake = await sourceIntake.LoadAsync(
            selectionKind,
            source.Path,
            SourceLoadSettings.Default,
            cancellationToken).ConfigureAwait(false);

        if (!intake.Accepted)
        {
            var failure = intake.Failure
                ?? new IpcFailure("source-refresh-rejected", "The source could not be reloaded.");
            return SourceRefreshHostResult.Reject(
                RetainIdentity(source, MapIntakeFailure(failure.Code)),
                failure);
        }

        var reloaded = intake.Sources.SingleOrDefault(candidate =>
            string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase)
            && candidate.Kind == source.Kind);

        if (reloaded is null)
        {
            return SourceRefreshHostResult.Reject(
                RetainIdentity(source, LoadedSourceStatus.FailedValidation),
                new IpcFailure(
                    "invalid-refresh-result",
                    "Source intake did not return the requested source during refresh."));
        }

        if (source.Kind == LoadedSourceKind.Archive)
        {
            return SourceRefreshHostResult.Accept(
                RetainIdentity(source, LoadedSourceStatus.Ready));
        }

        return await InterpretAsync(
            RetainIdentity(source, reloaded, LoadedSourceStatus.Ready),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRefreshHostResult> RefreshArchiveMemberAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken)
    {
        var provenance = source.ArchiveProvenance!;
        var intake = await sourceIntake
            .ReloadArchiveAsync(provenance, cancellationToken)
            .ConfigureAwait(false);

        if (!intake.Accepted)
        {
            var failure = intake.Failure
                ?? new IpcFailure("source-refresh-rejected", "The source archive could not be reloaded.");
            return SourceRefreshHostResult.Reject(
                RetainIdentity(source, MapIntakeFailure(failure.Code)),
                failure);
        }

        var reloaded = intake.Sources.FirstOrDefault(candidate =>
            candidate.Kind == source.Kind
            && candidate.ArchiveProvenance is { } candidateProvenance
            && candidateProvenance.OriginalArchiveSourceId == provenance.OriginalArchiveSourceId
            && string.Equals(
                candidateProvenance.ArchiveMemberPath,
                provenance.ArchiveMemberPath,
                StringComparison.OrdinalIgnoreCase)
            && candidateProvenance.ArchiveLineage
                .Skip(1)
                .Select(item => item.Path)
                .SequenceEqual(
                    provenance.ArchiveLineage.Skip(1).Select(item => item.Path),
                    StringComparer.OrdinalIgnoreCase));

        if (reloaded is null)
        {
            return SourceRefreshHostResult.Reject(
                RetainIdentity(source, LoadedSourceStatus.Unavailable),
                new IpcFailure(
                    "archive-member-not-found",
                    "The source archive no longer contains the requested XML source."));
        }

        return await InterpretAsync(
            RetainIdentity(source, reloaded, LoadedSourceStatus.Ready),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRefreshHostResult> InterpretAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken)
    {
        var interpretation = await sourceInterpreter
            .InterpretAsync(source, cancellationToken)
            .ConfigureAwait(false);

        return interpretation.Status switch
        {
            SourceInterpretationStatus.Usable => SourceRefreshHostResult.Accept(
                RetainIdentity(source, LoadedSourceStatus.Ready)),
            SourceInterpretationStatus.Unsupported => SourceRefreshHostResult.Reject(
                RetainIdentity(source, LoadedSourceStatus.Unsupported),
                ToIpcFailure(interpretation)),
            SourceInterpretationStatus.FailedValidation => SourceRefreshHostResult.Reject(
                RetainIdentity(source, LoadedSourceStatus.FailedValidation),
                ToIpcFailure(interpretation)),
            _ => throw new InvalidOperationException("The source interpretation status is not supported.")
        };
    }

    private static LoadedSourceContract RetainIdentity(
        LoadedSourceContract source,
        LoadedSourceStatus status)
    {
        return source with { Status = status };
    }

    private static LoadedSourceContract RetainIdentity(
        LoadedSourceContract source,
        LoadedSourceContract reloaded,
        LoadedSourceStatus status)
    {
        return reloaded with
        {
            SourceId = source.SourceId,
            SourceSetId = source.SourceSetId,
            IsIncluded = source.IsIncluded,
            Status = status
        };
    }

    private static IpcFailure ToIpcFailure(SourceInterpretationResult result)
    {
        return result.Failure is { } failure
            ? new IpcFailure(failure.Code, failure.Description)
            : new IpcFailure(
                "source-interpretation-failed",
                "The source could not be interpreted.");
    }

    private static LoadedSourceStatus MapIntakeFailure(string failureCode)
    {
        return failureCode switch
        {
            "unsupported-source" or "unsupported-archive" or "unsupported-input"
                => LoadedSourceStatus.Unsupported,
            _ => LoadedSourceStatus.Unavailable
        };
    }
}

public sealed record SourceRefreshHostResult(
    bool Accepted,
    LoadedSourceContract Source,
    IpcFailure? Failure)
{
    internal static SourceRefreshHostResult Accept(LoadedSourceContract source)
    {
        return new SourceRefreshHostResult(true, source, null);
    }

    internal static SourceRefreshHostResult Reject(
        LoadedSourceContract source,
        IpcFailure failure)
    {
        return new SourceRefreshHostResult(false, source, failure);
    }
}
