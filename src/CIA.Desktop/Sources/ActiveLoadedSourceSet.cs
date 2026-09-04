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
        return _items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
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
        Path = source.Path;
        _isIncluded = source.IsIncluded;
        _status = source.Status;
        Kind = source.Kind;
        RefreshMetadata();
    }

    public SourceId SourceId { get; }

    public string Path { get; }

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

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public string StatusText => Status switch
    {
        LoadedSourceStatus.Ready => "Ready",
        LoadedSourceStatus.Unavailable => "Unavailable",
        LoadedSourceStatus.Unsupported => "Unsupported",
        LoadedSourceStatus.FailedValidation => "Failed validation",
        _ => Status.ToString()
    };

    public string LevelText => "—";

    public string KindText => Kind == LoadedSourceKind.XmlFile ? "XML file" : "Archive";

    public string TypeBadgeText => Kind == LoadedSourceKind.XmlFile ? "XML" : "ARC";

    public string SourceText => "File system";

    public string ArchiveDepthText => Kind == LoadedSourceKind.Archive
        ? "Selected archive"
        : "Not archived";

    public string BreadcrumbText => DisplayName;

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

        if (source.SourceId != SourceId
            || !string.Equals(source.Path, Path, StringComparison.OrdinalIgnoreCase)
            || source.Kind != Kind)
        {
            throw new ArgumentException(
                "A source refresh must retain the loaded source identity, path and kind.",
                nameof(source));
        }

        Status = source.Status;
        StatusDetail = null;
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
