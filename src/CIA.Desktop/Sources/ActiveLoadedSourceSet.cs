using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CIA.Contracts.Sources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CIA.Desktop.Sources;

public sealed class ActiveLoadedSourceSet
{
    private readonly ObservableCollection<LoadedSourceItem> _items = [];
    private readonly ReadOnlyObservableCollection<LoadedSourceItem> _readOnlyItems;

    public ActiveLoadedSourceSet()
    {
        _readOnlyItems = new ReadOnlyObservableCollection<LoadedSourceItem>(_items);
    }

    public ReadOnlyObservableCollection<LoadedSourceItem> Items => _readOnlyItems;

    internal bool Contains(string path)
    {
        return _items.Any(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                item.ArchiveProvenance?.OriginalArchivePath,
                path,
                StringComparison.OrdinalIgnoreCase));
    }

    internal bool Contains(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _items.Any(item => item.HasSameLogicalSource(source));
    }

    internal bool Contains(LoadedSourceItem source)
    {
        return _items.Contains(source);
    }

    internal void AddRange(IEnumerable<LoadedSourceContract> sources)
    {
        foreach (var source in sources)
        {
            _items.Add(new LoadedSourceItem(source));
        }
    }

    internal void RemoveRange(IEnumerable<LoadedSourceItem> sources)
    {
        foreach (var source in sources.Distinct().ToArray())
        {
            _items.Remove(source);
        }
    }
}

public sealed class LoadedSourceItem : ObservableObject
{
    private string _path;
    private bool _isIncluded;
    private LoadedSourceStatus _status;
    private string? _statusDetail;
    private long? _sizeBytes;
    private string _sizeText = "—";
    private string _modifiedText = "—";
    private string _createdText = "—";

    public LoadedSourceItem(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SourceId = source.SourceId;
        _path = source.Path;
        _isIncluded = source.IsIncluded;
        _status = source.Status;
        Kind = source.Kind;
        ArchiveProvenance = source.ArchiveProvenance;
        RefreshMetadata();
    }

    public SourceId SourceId { get; }

    public string Path
    {
        get => _path;
        private set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        internal set
        {
            if (SetProperty(ref _isIncluded, value))
            {
                OnPropertyChanged(nameof(IncludedText));
            }
        }
    }

    public string IncludedText => IsIncluded ? "Yes" : "No";

    public LoadedSourceStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public LoadedSourceKind Kind { get; }

    public ArchiveSourceProvenance? ArchiveProvenance { get; private set; }

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public string StatusText => Status switch
    {
        LoadedSourceStatus.Ready => "Ready",
        LoadedSourceStatus.Unavailable => "Unavailable",
        LoadedSourceStatus.Unsupported => "Unsupported",
        LoadedSourceStatus.FailedValidation => "Failed validation",
        _ => Status.ToString()
    };

    public string LevelText => ArchiveProvenance?.ArchiveNestingLevel.ToString(
        CultureInfo.InvariantCulture) ?? "—";

    public string KindText => Kind == LoadedSourceKind.XmlFile ? "XML file" : "Archive";

    public string TypeBadgeText => Kind == LoadedSourceKind.XmlFile ? "XML" : "ARC";

    public string SourceText => ArchiveProvenance is null
        ? "File system"
        : $"Archive: {System.IO.Path.GetFileName(ArchiveProvenance.OriginalArchivePath)}";

    public string ArchiveDepthText => ArchiveProvenance is null
        ? "Not archived"
        : $"Level {ArchiveProvenance.ArchiveNestingLevel} of {ArchiveProvenance.MaximumArchiveNestingDepth.Value}";

    public string BreadcrumbText => ArchiveProvenance is null
        ? DisplayName
        : string.Join(
            "  ›  ",
            ArchiveProvenance.ArchiveLineage
                .Select(item => System.IO.Path.GetFileName(item.Path))
                .Concat(ArchiveProvenance.ArchiveMemberPath.Split(
                    ['/', '\\'],
                    StringSplitOptions.RemoveEmptyEntries)));

    public long? SizeBytes
    {
        get => _sizeBytes;
        private set => SetProperty(ref _sizeBytes, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    public string ModifiedText
    {
        get => _modifiedText;
        private set => SetProperty(ref _modifiedText, value);
    }

    public string CreatedText
    {
        get => _createdText;
        private set => SetProperty(ref _createdText, value);
    }

    internal void ApplyRefresh(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.SourceId != SourceId || source.Kind != Kind)
        {
            throw new ArgumentException(
                "A source refresh must retain the loaded source identity and kind.",
                nameof(source));
        }

        Path = source.Path;
        ArchiveProvenance = source.ArchiveProvenance;
        Status = source.Status;
        StatusDetail = null;
        NotifyArchiveContextChanged();
        RefreshMetadata();
    }

    internal void ApplyRefreshFailure(
        LoadedSourceStatus status,
        string failureDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        Status = status;
        StatusDetail = failureDescription;
        RefreshMetadata();
    }

    private void RefreshMetadata()
    {
        var metadata = ReadFileMetadata(Path);
        SizeBytes = metadata.SizeBytes;
        SizeText = metadata.SizeText;
        ModifiedText = metadata.ModifiedText;
        CreatedText = metadata.CreatedText;
    }

    internal bool HasSameLogicalSource(LoadedSourceContract source)
    {
        if (ArchiveProvenance is null || source.ArchiveProvenance is null)
        {
            return string.Equals(Path, source.Path, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
                ArchiveProvenance.OriginalArchivePath,
                source.ArchiveProvenance.OriginalArchivePath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                ArchiveProvenance.ArchiveMemberPath,
                source.ArchiveProvenance.ArchiveMemberPath,
                StringComparison.OrdinalIgnoreCase)
            && ArchiveProvenance.ArchiveLineage
                .Skip(1)
                .Select(item => item.Path)
                .SequenceEqual(
                    source.ArchiveProvenance.ArchiveLineage.Skip(1).Select(item => item.Path),
                    StringComparer.OrdinalIgnoreCase);
    }

    private void NotifyArchiveContextChanged()
    {
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(ArchiveDepthText));
        OnPropertyChanged(nameof(BreadcrumbText));
    }

    private static SourceFileMetadata ReadFileMetadata(string path)
    {
        try
        {
            var file = new FileInfo(path);

            if (!file.Exists)
            {
                return SourceFileMetadata.Unavailable;
            }

            return new SourceFileMetadata(
                file.Length,
                FormatFileSize(file.Length),
                file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                file.CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return SourceFileMetadata.Unavailable;
        }
    }

    private static string FormatFileSize(long length)
    {
        const double bytesPerMegabyte = 1024d * 1024d;
        return $"{length / bytesPerMegabyte:0.00} MB";
    }

    private sealed record SourceFileMetadata(
        long? SizeBytes,
        string SizeText,
        string ModifiedText,
        string CreatedText)
    {
        public static SourceFileMetadata Unavailable { get; } = new(null, "—", "—", "—");
    }
}
