using System.Collections.ObjectModel;
using CIA.Contracts.Sources;

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

    internal void AddRange(IEnumerable<LoadedSourceContract> sources)
    {
        foreach (var source in sources)
        {
            _items.Add(new LoadedSourceItem(source));
        }
    }
}

public sealed class LoadedSourceItem
{
    public LoadedSourceItem(LoadedSourceContract source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Path = source.Path;
        IsIncluded = source.IsIncluded;
        Status = source.Status;
        Kind = source.Kind;
    }

    public string Path { get; }

    public bool IsIncluded { get; }

    public LoadedSourceStatus Status { get; }

    public LoadedSourceKind Kind { get; }

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public string StatusText => Status == LoadedSourceStatus.Ready ? "Ready" : Status.ToString();

    public string LevelText => Kind == LoadedSourceKind.Archive ? "1" : "—";

    public string KindText => Kind == LoadedSourceKind.XmlFile ? "XML file" : "Archive";
}
