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
}

public sealed class LoadedSourceItem : ObservableObject
{
    private bool _isIncluded;

    public LoadedSourceItem(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SourceId = source.SourceId;
        Path = source.Path;
        _isIncluded = source.IsIncluded;
        Status = source.Status;
        Kind = source.Kind;

        var metadata = ReadFileMetadata(Path);
        SizeText = metadata.SizeText;
        ModifiedText = metadata.ModifiedText;
        CreatedText = metadata.CreatedText;
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

    public LoadedSourceStatus Status { get; }

    public LoadedSourceKind Kind { get; }

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public string StatusText => Status == LoadedSourceStatus.Ready ? "Ready" : Status.ToString();

    public string LevelText => "—";

    public string KindText => Kind == LoadedSourceKind.XmlFile ? "XML file" : "Archive";

    public string TypeBadgeText => Kind == LoadedSourceKind.XmlFile ? "XML" : "ARC";

    public string SourceText => "File system";

    public string ArchiveDepthText => Kind == LoadedSourceKind.Archive
        ? "Selected archive"
        : "Not archived";

    public string BreadcrumbText => DisplayName;

    public string SizeText { get; }

    public string ModifiedText { get; }

    public string CreatedText { get; }

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
        string SizeText,
        string ModifiedText,
        string CreatedText)
    {
        public static SourceFileMetadata Unavailable { get; } = new("—", "—", "—");
    }
}
