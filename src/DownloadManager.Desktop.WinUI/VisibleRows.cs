using System.Collections.ObjectModel;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The rows on the list, with a set kept in step so "is this row listed" is constant time (PERF.16): the window asks it for
/// every job event. Rows are never listed twice.
/// </summary>
internal sealed class VisibleRows : ObservableCollection<JobViewModel>
{
    private readonly HashSet<JobViewModel> _set = new(ReferenceEqualityComparer.Instance);

    public VisibleRows() { }

    public VisibleRows(IEnumerable<JobViewModel> rows) : base(rows)
    {
        foreach (var row in this) _set.Add(row);
    }

    /// <summary>Constant time; hides the linear <see cref="Collection{T}.Contains"/>.</summary>
    public new bool Contains(JobViewModel row) => _set.Contains(row);

    protected override void InsertItem(int index, JobViewModel item)
    {
        base.InsertItem(index, item);
        _set.Add(item);
    }

    protected override void SetItem(int index, JobViewModel item)
    {
        var old = this[index];
        base.SetItem(index, item);
        _set.Remove(old);
        _set.Add(item);
    }

    protected override void RemoveItem(int index)
    {
        var old = this[index];
        base.RemoveItem(index);
        _set.Remove(old);
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        _set.Clear();
    }
}
