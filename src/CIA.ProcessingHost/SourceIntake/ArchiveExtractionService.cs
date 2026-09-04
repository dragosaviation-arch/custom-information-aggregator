using CIA.Contracts.Ipc;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace CIA.ProcessingHost.SourceIntake;

public sealed class ArchiveExtractionService(ApplicationPaths applicationPaths)
{
    private const string ManagedExtractionDirectoryName = "ArchiveExtraction";

    public ArchiveExtractionResult Extract(
        string archivePath,
        SourceLoadSettings settings,
        CancellationToken cancellationToken = default,
        SourceId? retainedOriginalArchiveSourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(settings);

        var fullArchivePath = Path.GetFullPath(archivePath);
        var originalArchiveSourceId = retainedOriginalArchiveSourceId ?? SourceId.CreateNew();

        try
        {
            var location = CreateExtractionLocation(
                fullArchivePath,
                originalArchiveSourceId,
                settings);
            var rootLineage = new ArchiveLineageItem(
                originalArchiveSourceId,
                fullArchivePath,
                NestingLevel: 1);
            var pending = new Stack<ArchiveWorkItem>();
            pending.Push(new ArchiveWorkItem(
                fullArchivePath,
                originalArchiveSourceId,
                NestingLevel: 1,
                [rootLineage]));

            var sources = new List<LoadedSourceContract>();
            var issues = new List<SourceIntakeIssue>();

            while (pending.TryPop(out var archive))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessArchive(
                    archive,
                    location,
                    settings,
                    pending,
                    sources,
                    issues,
                    cancellationToken);
            }

            if (sources.Count == 0)
            {
                return ArchiveExtractionResult.Reject(
                    "archive-no-usable-sources",
                    "The archive did not contain any usable supported XML sources within the active nesting depth.",
                    location.Root,
                    location.Retention,
                    issues);
            }

            return ArchiveExtractionResult.Accept(
                sources,
                issues,
                location.Root,
                location.Retention);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsControlledArchiveFailure(exception))
        {
            return ArchiveExtractionResult.Reject(
                "archive-extraction-failed",
                "The archive could not be extracted into the configured CIA-managed location.",
                extractionRoot: null,
                ArchiveExtractionRetention.ManagedTemporary,
                []);
        }
    }

    private static void ProcessArchive(
        ArchiveWorkItem archiveItem,
        ArchiveExtractionLocation location,
        SourceLoadSettings settings,
        Stack<ArchiveWorkItem> pending,
        List<LoadedSourceContract> sources,
        List<SourceIntakeIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = ReaderFactory.OpenReader(archiveItem.ArchivePath);
            var archiveWorkingDirectory = Path.Combine(
                location.Root,
                archiveItem.ArchiveSourceId.ToString());

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;

                if (entry.IsDirectory)
                {
                    continue;
                }

                ProcessEntry(
                    archiveItem,
                    entry,
                    reader.OpenEntryStream,
                    archiveWorkingDirectory,
                    location,
                    settings,
                    pending,
                    sources,
                    issues,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsControlledArchiveFailure(exception))
        {
            issues.Add(new SourceIntakeIssue(
                archiveItem.NestingLevel == 1
                    ? "archive-unreadable"
                    : "nested-archive-unreadable",
                archiveItem.NestingLevel == 1
                    ? "The selected archive could not be read."
                    : "A nested archive could not be read and was skipped.",
                archiveItem.ArchivePath,
                archiveItem.NestingLevel,
                EntryPath: null));
        }
    }

    private static void ProcessEntry(
        ArchiveWorkItem archiveItem,
        IEntry entry,
        Func<Stream> openEntryStream,
        string archiveWorkingDirectory,
        ArchiveExtractionLocation location,
        SourceLoadSettings settings,
        Stack<ArchiveWorkItem> pending,
        List<LoadedSourceContract> sources,
        List<SourceIntakeIssue> issues,
        CancellationToken cancellationToken)
    {
        var entryPath = entry.Key;

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            issues.Add(new SourceIntakeIssue(
                "invalid-archive-entry",
                "An archive entry without a usable path was skipped.",
                archiveItem.ArchivePath,
                archiveItem.NestingLevel,
                EntryPath: null));
            return;
        }

        if (!TryResolveDestination(archiveWorkingDirectory, entryPath, out var destination))
        {
            issues.Add(new SourceIntakeIssue(
                "unsafe-archive-entry",
                "An archive entry whose path escaped the managed extraction root was skipped.",
                archiveItem.ArchivePath,
                archiveItem.NestingLevel,
                entryPath));
            return;
        }

        if (File.Exists(destination))
        {
            issues.Add(new SourceIntakeIssue(
                "duplicate-archive-entry",
                "A duplicate archive entry path was skipped without replacing the earlier extracted item.",
                archiveItem.ArchivePath,
                archiveItem.NestingLevel,
                entryPath));
            return;
        }

        try
        {
            ExtractEntry(openEntryStream, destination, cancellationToken);

            if (string.Equals(Path.GetExtension(destination), ".xml", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(CreateExtractedSource(
                    destination,
                    entryPath,
                    archiveItem,
                    location,
                    settings));
                return;
            }

            var archiveDetection = DetectArchive(destination);

            if (archiveDetection == ArchiveDetection.NotArchive)
            {
                File.Delete(destination);
                return;
            }

            if (archiveDetection == ArchiveDetection.Unreadable)
            {
                issues.Add(new SourceIntakeIssue(
                    "nested-archive-unreadable",
                    "A nested archive could not be read and was skipped.",
                    archiveItem.ArchivePath,
                    archiveItem.NestingLevel,
                    entryPath));
                return;
            }

            var nestedLevel = checked(archiveItem.NestingLevel + 1);

            if (!settings.MaximumArchiveNestingDepth.AllowsLevel(nestedLevel))
            {
                issues.Add(new SourceIntakeIssue(
                    "archive-depth-limit",
                    $"Nested archive traversal stopped before level {nestedLevel} because the configured maximum depth is {settings.MaximumArchiveNestingDepth.Value}.",
                    archiveItem.ArchivePath,
                    archiveItem.NestingLevel,
                    entryPath));
                return;
            }

            var nestedArchiveSourceId = SourceId.CreateNew();
            var nestedLineage = archiveItem.Lineage
                .Append(new ArchiveLineageItem(
                    nestedArchiveSourceId,
                    NormalizeContextPath(entryPath),
                    nestedLevel))
                .ToArray();
            pending.Push(new ArchiveWorkItem(
                destination,
                nestedArchiveSourceId,
                nestedLevel,
                nestedLineage));
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(destination);
            throw;
        }
        catch (Exception exception) when (IsControlledArchiveFailure(exception))
        {
            TryDeletePartialFile(destination);
            issues.Add(new SourceIntakeIssue(
                "archive-entry-failed",
                "An archive entry could not be extracted and was skipped.",
                archiveItem.ArchivePath,
                archiveItem.NestingLevel,
                entryPath));
        }
    }

    private ArchiveExtractionLocation CreateExtractionLocation(
        string originalArchivePath,
        SourceId originalArchiveSourceId,
        SourceLoadSettings settings)
    {
        string baseDirectory;
        ArchiveExtractionRetention retention;

        if (settings.PersistentArchiveExtractionEnabled)
        {
            baseDirectory = Path.GetFullPath(settings.PersistentArchiveExtractionDirectory!);
            var sourceDirectory = Path.GetDirectoryName(originalArchivePath)!;

            if (IsWithinDirectory(baseDirectory, sourceDirectory))
            {
                throw new InvalidOperationException(
                    "Persistent archive extraction cannot target the original source directory.");
            }

            retention = ArchiveExtractionRetention.Persistent;
        }
        else
        {
            baseDirectory = Path.Combine(
                applicationPaths.TempDirectory,
                ManagedExtractionDirectoryName);
            retention = ArchiveExtractionRetention.ManagedTemporary;
        }

        var extractionRoot = Path.GetFullPath(Path.Combine(
            baseDirectory,
            originalArchiveSourceId.ToString(),
            Guid.CreateVersion7().ToString("D")));

        if (!IsWithinDirectory(extractionRoot, baseDirectory))
        {
            throw new InvalidOperationException(
                "The archive extraction location escaped its configured storage root.");
        }

        Directory.CreateDirectory(extractionRoot);
        return new ArchiveExtractionLocation(extractionRoot, retention);
    }

    private static LoadedSourceContract CreateExtractedSource(
        string path,
        string entryPath,
        ArchiveWorkItem archiveItem,
        ArchiveExtractionLocation location,
        SourceLoadSettings settings)
    {
        var originalArchive = archiveItem.Lineage[0];
        var provenance = new ArchiveSourceProvenance(
            originalArchive.ArchiveSourceId,
            originalArchive.Path,
            archiveItem.Lineage,
            archiveItem.NestingLevel,
            NormalizeContextPath(entryPath),
            location.Root,
            location.Retention,
            settings.MaximumArchiveNestingDepth,
            location.Retention == ArchiveExtractionRetention.Persistent
                ? Path.GetFullPath(settings.PersistentArchiveExtractionDirectory!)
                : null);

        return new LoadedSourceContract(
            SourceId.CreateNew(),
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile)
        {
            ArchiveProvenance = provenance
        };
    }

    private static void ExtractEntry(
        Func<Stream> openEntryStream,
        string destination,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        using var source = openEntryStream();
        using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Write(buffer, 0, bytesRead);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryResolveDestination(
        string extractionRoot,
        string? archiveEntryPath,
        out string destination)
    {
        destination = string.Empty;

        if (string.IsNullOrWhiteSpace(archiveEntryPath))
        {
            return false;
        }

        try
        {
            var normalized = archiveEntryPath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
            {
                return false;
            }

            var resolvedRoot = Path.GetFullPath(extractionRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolvedDestination = Path.GetFullPath(Path.Combine(resolvedRoot, normalized));

            if (!IsWithinDirectory(resolvedDestination, resolvedRoot)
                || string.Equals(resolvedDestination, resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            destination = resolvedDestination;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeContextPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static ArchiveDetection DetectArchive(string path)
    {
        try
        {
            return ArchiveFactory.GetArchiveInformation(path) is null
                ? ArchiveDetection.NotArchive
                : ArchiveDetection.Supported;
        }
        catch (Exception exception) when (IsControlledArchiveFailure(exception))
        {
            return ArchiveDetection.Unreadable;
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var resolvedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryPrefix = resolvedDirectory + Path.DirectorySeparatorChar;

        return resolvedPath.Equals(resolvedDirectory, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsControlledArchiveFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or ArchiveOperationException
            or InvalidFormatException
            or NotSupportedException
            or EndOfStreamException
            or System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException;
    }

    private sealed record ArchiveWorkItem(
        string ArchivePath,
        SourceId ArchiveSourceId,
        int NestingLevel,
        IReadOnlyList<ArchiveLineageItem> Lineage);

    private sealed record ArchiveExtractionLocation(
        string Root,
        ArchiveExtractionRetention Retention);

    private enum ArchiveDetection
    {
        NotArchive,
        Supported,
        Unreadable
    }
}

public sealed record ArchiveExtractionResult(
    bool Accepted,
    IReadOnlyList<LoadedSourceContract> Sources,
    IReadOnlyList<SourceIntakeIssue> Issues,
    string? ExtractionRoot,
    ArchiveExtractionRetention Retention,
    IpcFailure? Failure)
{
    internal static ArchiveExtractionResult Accept(
        IReadOnlyList<LoadedSourceContract> sources,
        IReadOnlyList<SourceIntakeIssue> issues,
        string extractionRoot,
        ArchiveExtractionRetention retention)
    {
        return new ArchiveExtractionResult(
            true,
            sources,
            issues,
            extractionRoot,
            retention,
            Failure: null);
    }

    internal static ArchiveExtractionResult Reject(
        string failureCode,
        string failureDescription,
        string? extractionRoot,
        ArchiveExtractionRetention retention,
        IReadOnlyList<SourceIntakeIssue> issues)
    {
        return new ArchiveExtractionResult(
            false,
            Array.Empty<LoadedSourceContract>(),
            issues,
            extractionRoot,
            retention,
            new IpcFailure(failureCode, failureDescription));
    }
}
