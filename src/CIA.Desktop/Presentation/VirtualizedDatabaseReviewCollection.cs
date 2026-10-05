using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using CIA.Contracts.Database;

namespace CIA.Desktop.Presentation;

internal sealed class VirtualizedDatabaseReviewCollection :
    IList<DatabaseReviewRowPresentation>,
    IReadOnlyList<DatabaseReviewRowPresentation>,
    IList,
    INotifyCollectionChanged,
    INotifyPropertyChanged
{
    internal const int MaximumCachedPages = 4;

    private readonly Dictionary<int, CachedPage> _pages = [];
    private readonly object _syncRoot = new();
    private Func<DatabaseReviewRow, DatabaseReviewRowPresentation> _projector;
    private long _accessSequence;
    private int _count;

    public VirtualizedDatabaseReviewCollection(
        Func<DatabaseReviewRow, DatabaseReviewRowPresentation> projector)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _count;

    public bool IsReadOnly => true;

    bool IList.IsFixedSize => true;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => _syncRoot;

    public DatabaseReviewRowPresentation this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var pageStart = GetPageStart(index + 1);
            if (_pages.TryGetValue(pageStart, out var page))
            {
                page.LastAccess = ++_accessSequence;
                var pageIndex = index - (pageStart - 1);
                if (pageIndex < page.Rows.Count)
                {
                    return _projector(page.Rows[pageIndex]);
                }
            }

            return DatabaseReviewRowPresentation.CreateLoading(index + 1);
        }
        set => throw new NotSupportedException();
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    internal int CachedPageCount => _pages.Count;

    internal IReadOnlyList<DatabaseReviewRowPresentation> LoadedRows => _pages.Values
        .SelectMany(page => page.Rows)
        .OrderBy(row => row.Ordinal)
        .Select(_projector)
        .ToArray();

    internal void Reset(
        int totalRowCount,
        int pageStart,
        IReadOnlyList<DatabaseReviewRow> rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalRowCount);
        _pages.Clear();
        _count = totalRowCount;
        _accessSequence = 0;
        if (rows.Count > 0)
        {
            _pages.Add(pageStart, new CachedPage(rows.ToArray(), ++_accessSequence));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    internal void ClearReview() => Reset(0, 1, []);

    internal bool IsPageLoaded(int pageStart) => _pages.ContainsKey(pageStart);

    internal void SetPage(
        int pageStart,
        IReadOnlyList<DatabaseReviewRow> rows,
        int totalRowCount)
    {
        if (totalRowCount != Count)
        {
            throw new InvalidOperationException(
                "A Database review page changed the active result row count.");
        }

        _pages[pageStart] = new CachedPage(rows.ToArray(), ++_accessSequence);
        while (_pages.Count > MaximumCachedPages)
        {
            var oldest = _pages
                .Where(page => page.Key != pageStart)
                .MinBy(page => page.Value.LastAccess);
            _pages.Remove(oldest.Key);
        }

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    internal void Reproject(
        Func<DatabaseReviewRow, DatabaseReviewRowPresentation> projector)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    internal void InvalidateOrdinals(IEnumerable<int> ordinals)
    {
        foreach (var pageStart in ordinals.Select(GetPageStart).Distinct())
        {
            _pages.Remove(pageStart);
        }

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    internal static int GetPageStart(int ordinal) =>
        ((ordinal - 1) / DatabaseReviewLimits.MaximumRowsPerPage
            * DatabaseReviewLimits.MaximumRowsPerPage) + 1;

    public IEnumerator<DatabaseReviewRowPresentation> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(DatabaseReviewRowPresentation item) =>
        item is null ? -1 : item.Ordinal - 1;

    int IList.IndexOf(object? value) =>
        value is DatabaseReviewRowPresentation item ? IndexOf(item) : -1;

    public bool Contains(DatabaseReviewRowPresentation item) =>
        item is not null && item.Ordinal >= 1 && item.Ordinal <= Count;

    bool IList.Contains(object? value) =>
        value is DatabaseReviewRowPresentation item && Contains(item);

    public void CopyTo(DatabaseReviewRowPresentation[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var index = 0; index < Count; index++)
        {
            array[arrayIndex + index] = this[index];
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var itemIndex = 0; itemIndex < Count; itemIndex++)
        {
            array.SetValue(this[itemIndex], index + itemIndex);
        }
    }

    public void Add(DatabaseReviewRowPresentation item) => throw new NotSupportedException();

    int IList.Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    public void Insert(int index, DatabaseReviewRowPresentation item) =>
        throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    public bool Remove(DatabaseReviewRowPresentation item) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    private sealed class CachedPage(
        IReadOnlyList<DatabaseReviewRow> rows,
        long lastAccess)
    {
        public IReadOnlyList<DatabaseReviewRow> Rows { get; } = rows;

        public long LastAccess { get; set; } = lastAccess;
    }
}
