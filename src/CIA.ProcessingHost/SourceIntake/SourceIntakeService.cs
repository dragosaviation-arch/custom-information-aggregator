using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace CIA.ProcessingHost.SourceIntake;

public sealed class SourceIntakeService(ArchiveExtractionService archiveExtraction)
{
    public Task<SourceIntakeResult> LoadAsync(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Task.Run(
            () => Load(selectionKind, path, settings, cancellationToken),
            cancellationToken);
    }

    public Task<SourceIntakeResult> ReloadArchiveAsync(
        ArchiveSourceProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var settings = SourceLoadSettings.Default with
        {
            MaximumArchiveNestingDepth = provenance.MaximumArchiveNestingDepth,
            PersistentArchiveExtractionEnabled =
                provenance.Retention == ArchiveExtractionRetention.Persistent,
            PersistentArchiveExtractionDirectory = provenance.PersistentExtractionDirectory
        };

        return Task.Run(
            () => LoadArchive(
                provenance.OriginalArchivePath,
                settings,
                cancellationToken,
                provenance.OriginalArchiveSourceId),
            cancellationToken);
    }

    private SourceIntakeResult Load(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.MaximumArchiveNestingDepth.Value < 1
            || settings.PersistentArchiveExtractionEnabled
            && (string.IsNullOrWhiteSpace(settings.PersistentArchiveExtractionDirectory)
                || !Path.IsPathFullyQualified(settings.PersistentArchiveExtractionDirectory)))
        {
            return Reject(
                "invalid-load-settings",
                "Archive depth and persistent extraction settings must be valid before loading sources.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);

            return selectionKind switch
            {
                SourceSelectionKind.XmlFile => LoadXmlFile(fullPath),
                SourceSelectionKind.Archive => LoadArchive(fullPath, settings, cancellationToken),
                SourceSelectionKind.Folder => LoadFolder(fullPath, settings, cancellationToken),
                _ => Reject("unsupported-selection", "The requested source-selection kind is not supported.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsControlledPathFailure(exception))
        {
            return Reject("source-unreadable", "The selected source path could not be read.");
        }
    }

    private static SourceIntakeResult LoadXmlFile(string path)
    {
        if (!File.Exists(path))
        {
            return Reject("source-not-found", "The selected XML source file does not exist.");
        }

        if (!string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return Reject("unsupported-source", "The selected file is not a supported XML source.");
        }

        VerifyReadableFile(path);
        return Accept(CreateSource(path, LoadedSourceKind.XmlFile));
    }

    private SourceIntakeResult LoadArchive(
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken,
        SourceId? retainedOriginalArchiveSourceId = null)
    {
        if (!File.Exists(path))
        {
            return Reject("source-not-found", "The selected archive source file does not exist.");
        }

        if (!IsSupportedArchive(path))
        {
            return Reject("unsupported-archive", "The selected file is not a supported archive.");
        }

        var extraction = archiveExtraction.Extract(
            path,
            settings,
            cancellationToken,
            retainedOriginalArchiveSourceId);
        return new SourceIntakeResult(
            extraction.Accepted,
            extraction.Sources,
            extraction.Failure)
        {
            Issues = extraction.Issues
        };
    }

    private SourceIntakeResult LoadFolder(
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return Reject("source-not-found", "The selected source folder does not exist.");
        }

        var searchOption = settings.TraverseSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        var sources = new List<LoadedSourceContract>();
        var issues = new List<SourceIntakeIssue>();

        foreach (var filePath in Directory.EnumerateFiles(path, "*", searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(filePath);

            if (settings.IncludeXmlFiles
                && string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
            {
                VerifyReadableFile(fullPath);
                sources.Add(CreateSource(fullPath, LoadedSourceKind.XmlFile));
                continue;
            }

            if (!settings.IncludeArchiveFiles)
            {
                continue;
            }

            try
            {
                if (!IsSupportedArchive(fullPath))
                {
                    continue;
                }

                var archiveResult = LoadArchive(fullPath, settings, cancellationToken);
                if (archiveResult.Accepted)
                {
                    sources.AddRange(archiveResult.Sources);
                    issues.AddRange(archiveResult.Issues);
                }
                else
                {
                    issues.AddRange(archiveResult.Issues);
                    issues.Add(new SourceIntakeIssue(
                        archiveResult.Failure?.Code ?? "archive-load-failed",
                        archiveResult.Failure?.Description
                            ?? "An archive in the selected folder could not be processed.",
                        fullPath,
                        ArchiveNestingLevel: 1,
                        EntryPath: null));
                }
            }
            catch (Exception exception) when (IsControlledPathFailure(exception))
            {
                issues.Add(new SourceIntakeIssue(
                    "archive-unreadable",
                    "An archive in the selected folder could not be read and was skipped.",
                    fullPath,
                    ArchiveNestingLevel: 1,
                    EntryPath: null));
            }
        }

        var distinctSources = sources
            .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctSources.Length == 0 && issues.Count > 0)
        {
            return new SourceIntakeResult(
                false,
                Array.Empty<LoadedSourceContract>(),
                new IpcFailure(
                    "folder-no-usable-sources",
                    "The selected folder did not contain any usable supported sources under the active load settings."))
            {
                Issues = issues
            };
        }

        return Accept(distinctSources, issues);
    }

    private static bool IsSupportedArchive(string path)
    {
        try
        {
            return ArchiveFactory.GetArchiveInformation(path) is not null;
        }
        catch (Exception exception) when (IsUnsupportedArchive(exception))
        {
            return false;
        }
    }

    private static void VerifyReadableFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);
    }

    private static LoadedSourceContract CreateSource(string path, LoadedSourceKind kind)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            kind);
    }

    private static SourceIntakeResult Accept(params IReadOnlyList<LoadedSourceContract> sources)
    {
        return Accept(sources, []);
    }

    private static SourceIntakeResult Accept(
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<SourceIntakeIssue> issues)
    {
        return new SourceIntakeResult(true, sources, Failure: null)
        {
            Issues = issues
        };
    }

    private static SourceIntakeResult Reject(string code, string description)
    {
        return new SourceIntakeResult(
            false,
            Array.Empty<LoadedSourceContract>(),
            new IpcFailure(code, description));
    }

    private static bool IsUnsupportedArchive(Exception exception)
    {
        return exception is InvalidOperationException
            or ArchiveOperationException
            or InvalidFormatException
            or NotSupportedException
            or EndOfStreamException;
    }

    private static bool IsControlledPathFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }
}

public sealed record SourceIntakeResult(
    bool Accepted,
    IReadOnlyList<LoadedSourceContract> Sources,
    IpcFailure? Failure)
{
    public IReadOnlyList<SourceIntakeIssue> Issues { get; init; } = [];
}
