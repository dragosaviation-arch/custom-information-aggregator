using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace CIA.ProcessingHost.SourceIntake;

public sealed class SourceIntakeService
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

    private static SourceIntakeResult Load(
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);

            return selectionKind switch
            {
                SourceSelectionKind.XmlFile => LoadXmlFile(fullPath),
                SourceSelectionKind.Archive => LoadArchive(fullPath),
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

    private static SourceIntakeResult LoadArchive(string path)
    {
        if (!File.Exists(path))
        {
            return Reject("source-not-found", "The selected archive source file does not exist.");
        }

        if (!IsSupportedArchive(path))
        {
            return Reject("unsupported-archive", "The selected file is not a supported archive.");
        }

        return Accept(CreateSource(path, LoadedSourceKind.Archive));
    }

    private static SourceIntakeResult LoadFolder(
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

            if (settings.IncludeArchiveFiles && IsSupportedArchive(fullPath))
            {
                sources.Add(CreateSource(fullPath, LoadedSourceKind.Archive));
            }
        }

        var distinctSources = sources
            .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Accept(distinctSources);
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
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            kind);
    }

    private static SourceIntakeResult Accept(params IReadOnlyList<LoadedSourceContract> sources)
    {
        return new SourceIntakeResult(true, sources, Failure: null);
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
    IpcFailure? Failure);
