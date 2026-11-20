using Android.Text;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
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
    private readonly View _searchToggle;
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
        _searchToggle = Root.FindViewById<View>(Resource.Id.page_links_search_toggle)!;
        _searchToggle.Click += ToggleSearch;
        _shown = Root.FindViewById<CheckBox>(Resource.Id.page_links_select_shown)!;
        _summary = Root.FindViewById<TextView>(Resource.Id.page_links_summary)!;
        _submit = Root.FindViewById<MaterialButton>(Resource.Id.page_links_download)!;
        _list = Root.FindViewById<RecyclerView>(Resource.Id.page_links_list)!;
        _list.SetLayoutManager(new LinearLayoutManager(activity));
        _rows = new Rows(this); _list.SetAdapter(_rows);
        var host = Uri.CheckHostName(pageHost) != UriHostNameType.Unknown ? pageHost : "This page";
        Root.FindViewById<TextView>(Resource.Id.page_links_host)!.Text = $"{host} · {model.TotalCount} links";
        Root.FindViewById<TextView>(Resource.Id.page_links_destination)!.Text = $"To {destinationText}";
        var chipScroll = Root.FindViewById<HorizontalScrollView>(Resource.Id.page_links_chip_scroll)!;
        var chips = Root.FindViewById<LinearLayout>(Resource.Id.page_links_chips)!;
        foreach (var kind in Enum.GetValues<PageLinkKind>())
        {
            var chip = (Chip)LayoutInflater.From(activity)!.Inflate(Resource.Layout.chip_filter, chips, false)!;
            var count = model.Count(kind);
            chip.Text = $"{kind} {count}";
            chip.Visibility = kind == PageLinkKind.All || count > 0 ? ViewStates.Visible : ViewStates.Gone;
            chip.SetChipIconResource(KindIcon(kind));
            chip.ChipIconVisible = true;
            chip.Click += (_, _) =>
            {
                if (_busy || _disposed) return;
                _model.Kind = kind; Refresh();
                // All remains reachable by scrolling back; the selected category is brought to the leading edge.
                chipScroll.Post(() =>
                {
                    if (_disposed) return;
                    var offset = chipScroll.LayoutDirection == LayoutDirection.Rtl ? chip.Right - chipScroll.Width : chip.Left;
                    chipScroll.SmoothScrollTo(offset, 0);
                });
            };
            var layout = new LinearLayout.LayoutParams(-2, -2) { MarginEnd = Motion.Dp(activity, 8) };
            chips.AddView(chip, layout); _chips.Add(kind, chip);
        }
        Root.FindViewById<View>(Resource.Id.page_links_close)!.Click += OnClose;
        _search.TextChanged += OnSearch;
        _shown.CheckedChange += OnSelectShown;
        _submit.Click += OnDownload;
        Refresh();
    }
    private static int KindIcon(PageLinkKind kind) => kind switch
    {
        PageLinkKind.Archives => Resource.Drawable.ic_remote_archive,
        PageLinkKind.Audio => Resource.Drawable.ic_remote_audio,
        PageLinkKind.Video => Resource.Drawable.ic_page_link_video,
        PageLinkKind.Images => Resource.Drawable.ic_page_link_image,
        PageLinkKind.Documents => Resource.Drawable.ic_remote_document,
        PageLinkKind.Other => Resource.Drawable.ic_remote_document,
        _ => Resource.Drawable.ic_page_link_all,
    };
    private static int FileIcon(DownloadManager.Mobile.FileKind kind) => kind switch
    {
        DownloadManager.Mobile.FileKind.Archive => Resource.Drawable.ic_remote_archive,
        DownloadManager.Mobile.FileKind.Audio => Resource.Drawable.ic_remote_audio,
        DownloadManager.Mobile.FileKind.Video => Resource.Drawable.ic_page_link_video,
        DownloadManager.Mobile.FileKind.Image => Resource.Drawable.ic_page_link_image,
        _ => Resource.Drawable.ic_remote_document,
    };
    public View Root { get; }
    private void ToggleSearch(object? sender, EventArgs e)
    {
        if (_disposed || _busy) return;
        var keyboard = _activity.GetSystemService(Context.InputMethodService) as InputMethodManager;
        if (_search.Visibility == ViewStates.Visible)
        {
            keyboard?.HideSoftInputFromWindow(_search.WindowToken, 0);
            _search.Text = "";
            _search.ClearFocus();
            _search.Visibility = ViewStates.Gone;
            _searchToggle.ContentDescription = "Search links";
            return;
        }
        _search.Visibility = ViewStates.Visible;
        _searchToggle.ContentDescription = "Close link search";
        if (_search.RequestFocus()) _search.Post(() =>
        {
            if (!_disposed && _search.HasFocus) keyboard?.ShowSoftInput(_search, 0);
        });
    }
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
        var groupSelected = _model.Kind != PageLinkKind.All && string.IsNullOrWhiteSpace(_model.Query);
        _shown.Text = groupSelected ? $"All {visible.Count} {_model.Kind.ToString().ToLowerInvariant()}" : $"Select shown ({visible.Count})";
        var groupSize = Root.FindViewById<TextView>(Resource.Id.page_links_group_size)!;
        groupSize.Text = groupSelected && _shown.Checked && _model.VisibleBytes is long groupBytes
            ? DownloadManager.Mobile.Fmt.Bytes(groupBytes) : "";
        _search.Enabled = !_busy;
        foreach (var pair in _chips) { pair.Value.Checked = pair.Key == _model.Kind; pair.Value.Enabled = !_busy; }
        _summary.Text = $"{_model.SelectedCount} {(_model.SelectedCount == 1 ? "file" : "files")}";
        var selectedBytes = _model.SelectedBytes;
        if (selectedBytes is long bytes) _summary.Text += " \u00b7 " + DownloadManager.Mobile.Fmt.Bytes(bytes);
        _summary.ContentDescription = selectedBytes.HasValue ? _summary.Text + ", size reported by the page" : _summary.Text;
        _submit.Text = $"Download {_model.SelectedCount}"; _submit.Enabled = !_busy && _model.SelectedCount > 0; _submit.Alpha = _submit.Enabled ? 1f : .38f;
        Root.FindViewById<View>(Resource.Id.page_links_close)!.Enabled = !_busy;
        _rows.Update(_model.DisplayRows, selected); _rendering = false;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _searchToggle.Click -= ToggleSearch;
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
            public Holder(View view, Rows rows) : base(view)
            {
                _rows = rows;
                _check = view.FindViewById<CheckBox>(Resource.Id.page_link_check)!;
                _check.Click += OnClick;
            }
            public void Bind(PageLink link, bool selected, bool enabled)
            {
                _url = link.Url;
                var label = Label(link);
                if (link.SizeBytes is long size)
                {
                    var text = new SpannableString(label + "\n" + DownloadManager.Mobile.Fmt.Bytes(size));
                    text.SetSpan(new Android.Text.Style.RelativeSizeSpan(.8f), label.Length + 1, text.Length(), SpanTypes.ExclusiveExclusive);
                    text.SetSpan(new Android.Text.Style.ForegroundColorSpan(new Android.Graphics.Color(
                        Motion.Attr(ItemView.Context!, Resource.Attribute.colorOnSurfaceVariant))), label.Length + 1, text.Length(), SpanTypes.ExclusiveExclusive);
                    _check.TextFormatted = text;
                    _check.ContentDescription = text + ", size reported by the page";
                }
                else
                {
                    _check.Text = label;
                    _check.ContentDescription = null;
                }
                _check.Checked = selected;
                _check.Enabled = enabled;
                var kind = DownloadManager.Mobile.FileKinds.Of(new Uri(link.Url).AbsolutePath);
                var icon = FileIcons.For(kind);
                var drawable = ItemView.Context!.GetDrawable(FileIcon(kind))?.Mutate();
                drawable?.SetTint(FileIcons.Color(ItemView.Context!, icon));
                var iconSize = Motion.Dp(ItemView.Context!, 24);
                drawable?.SetBounds(0, 0, iconSize, iconSize);
                _check.SetCompoundDrawablesRelative(drawable, null, null, null);
            }
            public void Detach() => _check.Click -= OnClick;
            private void OnClick(object? sender, EventArgs e) { var owner = _rows._owner; if (owner._disposed || owner._busy || _url is null) return; owner._model.Toggle(_url); owner.Refresh(); }
        }
    }
}
