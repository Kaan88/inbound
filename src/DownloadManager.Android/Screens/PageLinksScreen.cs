using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Mobile.Links;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;

namespace Inbound.App.Screens;

internal sealed class PageLinksScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly PageLinks _model;
    private readonly Func<IReadOnlyList<PageLink>, Task<bool>> _download;
    private readonly Action _close;
    private readonly EditText _search;
    private readonly CheckBox _shown;
    private readonly TextView _summary;
    private readonly MaterialButton _submit;
    private readonly RecyclerView _list;
    private readonly Rows _rows;
    private readonly Dictionary<PageLinkKind, Chip> _chips = new();
    private bool _disposed, _busy, _rendering;

    public PageLinksScreen(MainActivity activity, PageLinks model, string pageHost, string destinationText,
        Func<IReadOnlyList<PageLink>, Task<bool>> download, Action close)
    {
        _activity = activity; _model = model; _download = download; _close = close;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.page_links, null, false)!;
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.page_links_top)!, top: true, bottom: false);
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.page_links_bottom)!, top: false, bottom: true);
        _search = Root.FindViewById<EditText>(Resource.Id.page_links_search)!;
        _shown = Root.FindViewById<CheckBox>(Resource.Id.page_links_select_shown)!;
        _summary = Root.FindViewById<TextView>(Resource.Id.page_links_summary)!;
        _submit = Root.FindViewById<MaterialButton>(Resource.Id.page_links_download)!;
        _list = Root.FindViewById<RecyclerView>(Resource.Id.page_links_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(activity));
        _rows = new Rows(this); _list.SetAdapter(_rows);
        var host = Uri.CheckHostName(pageHost) != UriHostNameType.Unknown ? pageHost : "This page";
        Root.FindViewById<TextView>(Resource.Id.page_links_host)!.Text = $"{host} · {model.TotalCount} links";
        Root.FindViewById<TextView>(Resource.Id.page_links_destination)!.Text = $"To {destinationText}";
        var chips = Root.FindViewById<LinearLayout>(Resource.Id.page_links_chips)!;
        foreach (var kind in Enum.GetValues<PageLinkKind>())
        {
            var chip = (Chip)LayoutInflater.From(activity)!.Inflate(Resource.Layout.chip_filter, chips, false)!;
            chip.Text = $"{kind} {model.Count(kind)}";
            chip.Click += (_, _) => { if (_busy || _disposed) return; _model.Kind = kind; Refresh(); };
            chips.AddView(chip); _chips.Add(kind, chip);
        }
        Root.FindViewById<View>(Resource.Id.page_links_close)!.Click += OnClose;
        _search.TextChanged += OnSearch;
        _shown.CheckedChange += OnSelectShown;
        _submit.Click += OnDownload;
        Refresh();
    }
    public View Root { get; }
    private void OnClose(object? sender, EventArgs e) { if (!_busy && !_disposed) _close(); }
    private void OnSearch(object? sender, TextChangedEventArgs e) { if (!_disposed && !_busy) { _model.Query = _search.Text ?? ""; Refresh(); } }
    private void OnSelectShown(object? sender, CompoundButton.CheckedChangeEventArgs e)
    { if (!_rendering && !_disposed && !_busy) { _model.SetVisibleSelected(e.IsChecked); Refresh(); } }
    private async void OnDownload(object? sender, EventArgs e)
    {
        if (_disposed || _busy || _model.SelectedCount == 0) return;
        _busy = true; Refresh();
        try
        {
            var completed = await _download(_model.Selected);
            if (!_disposed && !_activity.IsFinishing && !_activity.IsDestroyed && completed) _close();
        }
        catch (Exception) { if (!_disposed && !_activity.IsFinishing && !_activity.IsDestroyed) _activity.ShowMessage("Could not add the selected links. Try again."); }
        finally { _busy = false; if (!_disposed && !_activity.IsFinishing && !_activity.IsDestroyed) Refresh(); }
    }
    private void Refresh()
    {
        _rendering = true;
        var visible = _model.Visible; var selected = _model.Selected.Select(x => x.Url).ToHashSet(StringComparer.Ordinal);
        _shown.Enabled = !_busy && visible.Count > 0;
        _shown.Checked = visible.Count > 0 && visible.All(x => selected.Contains(x.Url));
        _shown.Text = $"Select shown ({visible.Count})";
        _search.Enabled = !_busy;
        foreach (var pair in _chips) { pair.Value.Checked = pair.Key == _model.Kind; pair.Value.Enabled = !_busy; }
        _summary.Text = $"{_model.SelectedCount} files";
        _submit.Text = $"Download {_model.SelectedCount}"; _submit.Enabled = !_busy && _model.SelectedCount > 0; _submit.Alpha = _submit.Enabled ? 1f : .38f;
        Root.FindViewById<View>(Resource.Id.page_links_close)!.Enabled = !_busy;
        _rows.Update(visible, selected); _rendering = false;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _search.TextChanged -= OnSearch; _shown.CheckedChange -= OnSelectShown; _submit.Click -= OnDownload;
        Root.FindViewById<View>(Resource.Id.page_links_close)!.Click -= OnClose;
        _list.SetAdapter(null); _rows.Detach(); _rows.Dispose();
    }
    private static string Label(PageLink link)
    {
        // Page-controlled labels can themselves contain an address or query token.
        var name = link.Name.Trim();
        if (name.Length == 0 || name.Contains("://", StringComparison.Ordinal) || name.Contains('?'))
        {
            var path = new Uri(link.Url).AbsolutePath.TrimEnd('/');
            name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        }
        return string.IsNullOrWhiteSpace(name) || name.Contains('?') || name.Contains("://", StringComparison.Ordinal) ? "File" : name;
    }
    private sealed class Rows : RecyclerView.Adapter
    {
        private readonly PageLinksScreen _owner;
        public Rows(PageLinksScreen owner) => _owner = owner;
        private IReadOnlyList<PageLink> _items = Array.Empty<PageLink>();
        private HashSet<string> _selected = new(StringComparer.Ordinal);
        private readonly HashSet<Holder> _holders = [];
        public void Update(IReadOnlyList<PageLink> items, HashSet<string> selected) { _items = items; _selected = selected; NotifyDataSetChanged(); }
        public override int ItemCount => _items.Count;
        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var holder = new Holder(LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.page_link_row, parent, false)!, this);
            _holders.Add(holder); return holder;
        }
        public void Detach() { foreach (var holder in _holders) holder.Detach(); _holders.Clear(); }
        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) => ((Holder)holder).Bind(_items[position], _selected.Contains(_items[position].Url), !_owner._busy);
        private sealed class Holder : RecyclerView.ViewHolder
        {
            private readonly Rows _rows; private readonly CheckBox _check; private string? _url;
            public Holder(View view, Rows rows) : base(view) { _rows = rows; _check = view.FindViewById<CheckBox>(Resource.Id.page_link_check)!; _check.Click += OnClick; }
            public void Bind(PageLink link, bool selected, bool enabled) { _url = link.Url; _check.Text = Label(link); _check.Checked = selected; _check.Enabled = enabled; }
            public void Detach() => _check.Click -= OnClick;
            private void OnClick(object? sender, EventArgs e) { var owner = _rows._owner; if (owner._disposed || owner._busy || _url is null) return; owner._model.Toggle(_url); owner.Refresh(); }
        }
    }
}
