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
    private readonly ObservableCollection<SourceSetDefinition> _sourceSets = [];
    private readonly ReadOnlyObservableCollection<SourceSetDefinition> _readOnlySourceSets;
    private int _nextDefaultSetNumber = 1;

    public ActiveLoadedSourceSet()
    {
        _readOnlyItems = new ReadOnlyObservableCollection<LoadedSourceItem>(_items);
        _readOnlySourceSets = new ReadOnlyObservableCollection<SourceSetDefinition>(_sourceSets);
    }

    public ReadOnlyObservableCollection<LoadedSourceItem> Items => _readOnlyItems;

    public ReadOnlyObservableCollection<SourceSetDefinition> SourceSets => _readOnlySourceSets;

    public SourceSetDefinition? ActiveSourceSet { get; private set; }

    public IReadOnlyList<LoadedSourceContract> CreateIncludedReadySnapshot()
    {
        return _items
            .Where(source => source.IsIncluded && source.Status == LoadedSourceStatus.Ready)
            .Select(source => new LoadedSourceContract(
                source.SourceId,
                source.SourceSetId,
                source.Path,
                source.IsIncluded,
                source.Status,
                source.Kind)
            {
                ArchiveProvenance = source.ArchiveProvenance
            })
            .ToArray();
    }

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

    internal bool Contains(SourceSetId sourceSetId)
    {
        return _sourceSets.Any(sourceSet => sourceSet.SourceSetId == sourceSetId);
    }

    internal SourceSetDefinition CreateSourceSet(SourceSetId sourceSetId, string name)
    {
        if (sourceSetId.Value == Guid.Empty)
        {
            throw new ArgumentException("A Source Set requires a non-empty identity.", nameof(sourceSetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Contains(sourceSetId))
        {
            throw new ArgumentException("The Source Set identity is already active.", nameof(sourceSetId));
        }

        var sourceSet = new SourceSetDefinition(sourceSetId, name.Trim());
        _sourceSets.Add(sourceSet);
        if (string.Equals(
                sourceSet.Name,
                $"Set {_nextDefaultSetNumber}",
                StringComparison.OrdinalIgnoreCase))
        {
            _nextDefaultSetNumber++;
        }

        ActiveSourceSet = sourceSet;
        return sourceSet;
    }

    internal string GetNextDefaultSourceSetName()
    {
        var candidateNumber = _nextDefaultSetNumber;
        while (_sourceSets.Any(sourceSet =>
            string.Equals(
                sourceSet.Name,
                $"Set {candidateNumber}",
                StringComparison.OrdinalIgnoreCase)))
        {
            candidateNumber++;
        }

        return $"Set {candidateNumber}";
    }

    internal bool Activate(SourceSetId sourceSetId)
    {
        var sourceSet = _sourceSets.FirstOrDefault(candidate =>
            candidate.SourceSetId == sourceSetId);
        if (sourceSet is null || ReferenceEquals(sourceSet, ActiveSourceSet))
        {
            return sourceSet is not null;
        }

        ActiveSourceSet = sourceSet;
        return true;
    }

    internal bool Rename(SourceSetId sourceSetId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sourceSet = _sourceSets.FirstOrDefault(candidate =>
            candidate.SourceSetId == sourceSetId);
        if (sourceSet is null)
        {
            return false;
        }

        var normalizedName = name.Trim();
        sourceSet.Rename(normalizedName);
        foreach (var item in _items.Where(item => item.SourceSetId == sourceSetId))
        {
            item.SetSourceSet(sourceSetId, normalizedName);
        }

        return true;
    }

    internal void AddRange(IEnumerable<LoadedSourceContract> sources)
    {
        foreach (var source in sources)
        {
            var sourceSet = _sourceSets.FirstOrDefault(candidate =>
                candidate.SourceSetId == source.SourceSetId)
                ?? throw new InvalidOperationException(
                    "A loaded source must belong to an active Source Set.");
            _items.Add(new LoadedSourceItem(source, sourceSet.Name));
        }
    }

    internal void Reassign(IEnumerable<LoadedSourceItem> sources, SourceSetDefinition sourceSet)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceSet);

        if (!Contains(sourceSet.SourceSetId))
        {
            throw new ArgumentException("The destination Source Set is not active.", nameof(sourceSet));
        }

        foreach (var source in sources.Distinct())
        {
            source.SetSourceSet(sourceSet.SourceSetId, sourceSet.Name);
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

public sealed class SourceSetDefinition : ObservableObject
{
    private string _name;

    internal SourceSetDefinition(SourceSetId sourceSetId, string name)
    {
        SourceSetId = sourceSetId;
        _name = name;
    }

    public SourceSetId SourceSetId { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    internal void Rename(string name)
    {
        Name = name;
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

    private SourceSetId _sourceSetId;
    private string _sourceSetName;

    internal LoadedSourceItem(LoadedSourceContract source, string sourceSetName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);

        SourceId = source.SourceId;
        _sourceSetId = source.SourceSetId;
        _sourceSetName = sourceSetName;
        _path = source.Path;
        _isIncluded = source.IsIncluded;
        _status = source.Status;
        Kind = source.Kind;
        ArchiveProvenance = source.ArchiveProvenance;
        RefreshMetadata();
    }

    public SourceId SourceId { get; }

    public SourceSetId SourceSetId
    {
        get => _sourceSetId;
        private set => SetProperty(ref _sourceSetId, value);
    }

    public string SourceSetName
    {
        get => _sourceSetName;
        private set => SetProperty(ref _sourceSetName, value);
    }

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

        if (source.SourceId != SourceId
            || source.SourceSetId != SourceSetId
            || source.Kind != Kind)
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

    internal void SetSourceSet(SourceSetId sourceSetId, string sourceSetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSetName);
        SourceSetId = sourceSetId;
        SourceSetName = sourceSetName;
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
