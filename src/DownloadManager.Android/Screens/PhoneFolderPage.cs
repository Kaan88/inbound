using System.ComponentModel;
using Android.Database;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Inbound.App.Host;

namespace Inbound.App.Screens;

/// <summary>
/// Mockup x10b: where Show in folder lands from Downloads. The app's own single-pane folder page (Phone › Download › Inbound) over the
/// Downloads tab, which stays selected. The file is outlined for about 3 s and keeps a tonal Open pill; while its download still runs, its
/// row shows the progress instead. Back (the arrow or the back gesture) returns to the Downloads list.
/// </summary>
internal sealed class PhoneFolderPage : IDisposable
{
    private const int MarkMilliseconds = 3000;

    private sealed record Row(SiteItemDto Item, bool Running);

    private readonly MainActivity _activity;
    private readonly JobRow? _running;
    private string _file;
    private string _markFolder;
    private readonly List<SiteItemDto>? _canned;
    private readonly Action _close;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly LinearLayout _header;
    private readonly RecyclerView _list;
    private readonly FolderAdapter _adapter;
    private readonly TextView _message;
    private List<Row> _rows = [];
    private string _path;
    private string? _mark;
    private string? _pill;
    private bool _scrolled;
    private bool _disposed;
    private int _generation;

    public LinearLayout Root { get; }

    /// <param name="phonePath">"Download/Inbound": the folder shown first.</param>
    /// <param name="file">The file to outline, or null for the folder alone.</param>
    /// <param name="running">The download of that file when it has not finished yet: its row shows its progress.</param>
    /// <param name="canned">Test build only: rows to show instead of reading the phone.</param>
    public PhoneFolderPage(MainActivity activity, string phonePath, string? file, JobRow? running, Action close, List<SiteItemDto>? canned = null)
    {
        _activity = activity;
        _path = _markFolder = phonePath;
        _file = file ?? running?.FileName ?? "";
        _running = running is { Group: not RowGroup.Finished and not RowGroup.Failed } ? running : null;
        _close = close;
        _canned = canned;
        Root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        Root.SetBackgroundColor(new Color(Motion.Attr(activity, Android.Resource.Attribute.ColorBackground)));
        Root.Clickable = true; // nothing underneath hears a touch
        _header = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        Root.AddView(_header, new LinearLayout.LayoutParams(-1, -2));
        _message = Text("", 14, Resource.Attribute.colorOnSurfaceVariant);
        _message.SetPadding(Dp(24), Dp(24), Dp(24), 0);
        _message.Visibility = ViewStates.Gone;
        Root.AddView(_message, new LinearLayout.LayoutParams(-1, -2));
        _list = new RecyclerView(activity);
        _list.SetLayoutManager(new LinearLayoutManager(activity));
        _list.SetClipToPadding(false);
        _list.SetPadding(0, Dp(4), 0, Dp(24));
        _adapter = new FolderAdapter(this);
        _list.SetAdapter(_adapter);
        Root.AddView(_list, new LinearLayout.LayoutParams(-1, 0, 1));
        InsetsListener.Pad(Root, top: true, bottom: false);

        if (file is not null || running is not null)
        {
            _mark = _file;
            if (_running is null) _pill = _file;
            // The outline fades after about 3 s; the Open pill stays until the person leaves the folder (x9).
            if (_running is null) StartMarkTimer();
        }
        if (_running is not null) _running.PropertyChanged += OnRunningChanged;
        Load(_path);
    }

    /// <summary>The file this page is about: the running download's name as the Host knows it now (it can change), else the one fixed at the end.</summary>
    private string CurrentFile => Running && _running is { } job ? (job.FileName.Length > 0 ? job.FileName : job.Name) : _file;

    private string _appliedFile = "";

    private int Dp(float dp) => Motion.Dp(_activity, dp);

    private TextView Text(string text, float size, int color)
    {
        var view = new TextView(_activity) { Text = text, TextSize = size };
        view.SetTextColor(new Color(Motion.Attr(_activity, color)));
        return view;
    }

    private View IconButton(int drawable, string description, Action click)
    {
        var button = new ImageButton(_activity) { ContentDescription = description };
        button.SetImageResource(drawable);
        button.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        button.SetScaleType(ImageView.ScaleType.CenterInside);
        button.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        button.Background = new TypedValueHolder(_activity).SelectableBackground();
        button.Click += (_, _) => click();
        return button;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Header: app bar and breadcrumbs

    private void BuildHeader()
    {
        _header.RemoveAllViews();
        var bar = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(4), Dp(4), Dp(4), 0);
        bar.AddView(IconButton(Resource.Drawable.ic_arrow_back, "Back to Downloads", _close), new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        var segments = _path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var title = Text(segments.Length == 0 ? "Phone" : segments[^1], 22, Resource.Attribute.colorOnSurface);
        title.SetSingleLine(true);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        title.SetPadding(Dp(12), 0, 0, 0);
        ViewCompatHeading(title);
        bar.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        View? more = null;
        more = IconButton(Resource.Drawable.ic_more, "More options", () => ShowMenu(more!));
        bar.AddView(more, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        _header.AddView(bar);
        _header.AddView(Crumbs());
    }

    private static void ViewCompatHeading(View view) => AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(view, true);

    private void ShowMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        menu.Menu!.Add(0, 1, 0, "Show in the Files app");
        menu.MenuItemClick += (_, _) =>
        {
            if (!Outside.ShowDownloadFolder(_activity)) _activity.ShowMessage("No app on this phone can show the folder.");
        };
        menu.Show();
    }

    private View Crumbs()
    {
        var crumbs = RemoteCrumbs.BuildPhone(_path);
        var scroll = new HorizontalScrollView(_activity) { HorizontalScrollBarEnabled = false };
        var strip = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        strip.SetGravity(GravityFlags.CenterVertical);
        strip.SetPadding(Dp(16), Dp(10), Dp(16), Dp(6));
        for (int i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            bool current = i == crumbs.Count - 1;
            var chip = Text(crumb.Label, 14, Resource.Attribute.colorOnSurface);
            // The mockups' --chiptext: on-surface in light, #cfcfcf in dark (on-surface at 85 %) (Fable x10b).
            if (!current && (_activity.Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes)
                chip.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)) { A = 217 });
            chip.SetSingleLine(true);
            chip.Gravity = GravityFlags.CenterVertical;
            chip.SetPadding(Dp(12), 0, Dp(12), 0);
            var background = new GradientDrawable();
            background.SetCornerRadius(Dp(8));
            background.SetColor(new Color(Motion.Attr(_activity, current ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurface)));
            if (!current) background.SetStroke(Dp(1), new Color(Motion.Attr(_activity, Resource.Attribute.colorOutlineVariant)));
            chip.Background = background;
            chip.ContentDescription = current ? crumb.Label + ", this folder" : "Open " + crumb.Label;
            if (!current) chip.Click += (_, _) => Load(crumb.Path);
            strip.AddView(chip, new LinearLayout.LayoutParams(-2, Dp(32)));
            if (i < crumbs.Count - 1)
            {
                var chevron = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
                chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
                chevron.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
                strip.AddView(chevron, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { LeftMargin = Dp(3), RightMargin = Dp(3) });
            }
        }
        scroll.AddView(strip);
        scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Right));
        return scroll;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Loading

    private void Load(string path)
    {
        if (_disposed) return;
        _path = path;
        int generation = ++_generation;
        BuildHeader();
        var context = _activity;
        var canned = _canned;
        _ = Task.Run(() =>
        {
            var result = canned is not null ? (Items: canned, Error: (string?)null) : ReadFolder(context, path);
            _activity.RunOnUiThread(() => Apply(generation, result.Items, result.Error));
        });
    }

    private void Apply(int generation, List<SiteItemDto> items, string? error)
    {
        if (_disposed || generation != _generation) return;
        var rows = new List<Row>();
        string file = CurrentFile;
        _appliedFile = file;
        bool here = _path == _markFolder && file.Length > 0;
        var folders = items.Where(i => i.IsFolder).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var files = items.Where(i => !i.IsFolder).OrderByDescending(i => i.Modified ?? DateTimeOffset.MinValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        rows.AddRange(folders.Select(f => new Row(f, false)));
        if (here && Running)
        {
            // The download's own file is not there until it is renamed from its part file: the row stands in with its progress.
            // Its part file (<name>.<id>.part) is not shown either: only this row, with the progress.
            files.RemoveAll(f => f.Name == file || (f.Name.StartsWith(file + ".", StringComparison.Ordinal) && f.Name.EndsWith(".part", StringComparison.Ordinal)));
            rows.Add(new Row(new SiteItemDto { Name = file, Size = _running!.Job.TotalBytes ?? -1 }, true));
        }
        else if (here && !Running && _pill == file && files.All(f => f.Name != file) && _lastFinished is { } done && ExistsOnPhone(file))
        {
            // Just finished and not in the phone's media index yet; only when the file is really there.
            files.Insert(0, new SiteItemDto { Name = file, Size = done.Size, Modified = DateTimeOffset.Now });
        }
        rows.AddRange(files.Select(f => new Row(f, false)));
        _rows = rows;
        _message.Visibility = error is not null || rows.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        _message.Text = error ?? "This folder is empty.";
        _adapter.NotifyDataSetChanged();
        if (!_scrolled && here)
        {
            int index = _rows.FindIndex(r => r.Item.Name == file);
            if (index >= 0) { _scrolled = true; _list.ScrollToPosition(index); }
        }
    }

    private (long Size, string Name)? _lastFinished;

    private bool ExistsOnPhone(string name)
    {
        try
        {
            var relative = RemoteTransfers.DownloadRelative(_markFolder) ?? "";
            return SafeRelative(relative) && SafeSegment(name) && File.Exists(System.IO.Path.Combine(AndroidHost.SharedDownloadFolder, relative, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The row of this page's download was removed (deleted from another place): its progress row goes.</summary>
    public void OnRowRemoved(JobRow row)
    {
        if (_disposed || !ReferenceEquals(row, _running) || _runningDone) return;
        _running!.PropertyChanged -= OnRunningChanged;
        _runningDone = true;
        _mark = _pill = null;
        Load(_path);
    }

#if INBOUND_TEST_BUILD
    /// <summary>Captures only: the outline fades after 3 s, so the fixture puts it back.</summary>
    internal void FixtureRemark()
    {
        if (_disposed || _pill is null || _mark is not null) return;
        _mark = _pill;
        _adapter.NotifyDataSetChanged();
    }
#endif

    private void StartMarkTimer()
    {
        _ui.PostDelayed(() =>
        {
            if (_disposed) return;
            _mark = null; // the outline fades; the Open pill stays
            _adapter.NotifyDataSetChanged();
        }, MarkMilliseconds);
    }

    private void OnRunningChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _running is null) return;
        if (_running.Group == RowGroup.Failed)
        {
            _running.PropertyChanged -= OnRunningChanged;
            _runningDone = true;
            _mark = _pill = null;
            Load(_path);
            return;
        }
        if (_running.Group == RowGroup.Finished)
        {
            _running.PropertyChanged -= OnRunningChanged;
            // The final name and folder (the Host may have renamed it): read before the page lists again.
            string final = _running.Job.FinalFilePath ?? "";
            string name = System.IO.Path.GetFileName(final);
            _file = name.Length > 0 ? name : CurrentFile;
            if (final.Length > 0 && System.IO.Path.GetDirectoryName(final) is { } directory && MainActivity.PhoneFolderOf(directory) is { } folder)
                _path = _markFolder = folder;
            _lastFinished = (_running.Job.TotalBytes ?? _running.Job.ReceivedBytes, _file);
            _pill = _mark = _file;
            _runningDone = true;
            _scrolled = false;
            StartMarkTimer();
            Load(_path);
            // The phone's media index may need a moment to list the file.
            _ui.PostDelayed(() => { if (!_disposed) Load(_path); }, 2000);
            return;
        }
        if (CurrentFile != _appliedFile) { Load(_path); return; } // the Host's name arrived
        int index = _rows.FindIndex(r => r.Running);
        if (index >= 0) _adapter.NotifyItemChanged(index);
    }

    private bool _runningDone;

    private bool Running => _running is not null && !_runningDone;

    // ---------------------------------------------------------------------------------------------------------------
    // The phone's Download folder (scoped storage): files from MediaStore, folders from the file system.

    private static List<SiteItemDto> NoItems => [];

    private static (List<SiteItemDto> Items, string? Error) ReadFolder(MainActivity activity, string phonePath)
    {
        if (phonePath.Length == 0) return ([new SiteItemDto { Name = "Download", IsFolder = true }], null);
        if (RemoteTransfers.DownloadRelative(phonePath) is not { } relative || !SafeRelative(relative))
            return (NoItems, "This folder is not available.");

        var files = new Dictionary<string, SiteItemDto>(StringComparer.Ordinal);
        var folders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string parent = relative.Trim('/');
        string prefix = parent.Length == 0 ? "" : parent + "/";
        var projection = new[]
        {
            MediaStore.IMediaColumns.DisplayName, MediaStore.IMediaColumns.RelativePath,
            MediaStore.IMediaColumns.Size, MediaStore.IMediaColumns.DateModified
        };
        try
        {
            using ICursor? cursor = activity.ContentResolver?.Query(MediaStore.Downloads.ExternalContentUri, projection, null, null,
                MediaStore.IMediaColumns.RelativePath + ", " + MediaStore.IMediaColumns.DisplayName);
            if (cursor is null) return (NoItems, "Android did not return the Downloads list.");
            int nameColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
            int pathColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath);
            int sizeColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.Size);
            int modifiedColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DateModified);
            if (nameColumn < 0 || pathColumn < 0) return (NoItems, "Android did not provide Downloads folder details.");
            while (cursor.MoveToNext())
            {
                var displayName = cursor.GetString(nameColumn) ?? "";
                if (!SafeSegment(displayName) || !TryRelative(cursor.GetString(pathColumn) ?? "", out var directory)) continue;
                var itemPath = directory.Length == 0 ? displayName : directory + "/" + displayName;
                if (!itemPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var remainder = itemPath[prefix.Length..];
                if (remainder.Length == 0) continue;
                int slash = remainder.IndexOf('/');
                if (slash >= 0)
                {
                    var child = remainder[..slash];
                    if (!SafeSegment(child)) continue;
                    if (!folders.TryGetValue(child, out var inside)) folders[child] = inside = new HashSet<string>(StringComparer.Ordinal);
                    var next = remainder[(slash + 1)..];
                    int deeper = next.IndexOf('/');
                    inside.Add(deeper < 0 ? next : next[..deeper]);
                    continue;
                }
                long size = sizeColumn >= 0 && !cursor.IsNull(sizeColumn) ? cursor.GetLong(sizeColumn) : -1;
                long modified = modifiedColumn >= 0 && !cursor.IsNull(modifiedColumn) ? cursor.GetLong(modifiedColumn) : 0;
                files[displayName] = new SiteItemDto
                {
                    Name = displayName, Size = Math.Max(-1, size),
                    Modified = modified > 0 ? DateTimeOffset.FromUnixTimeSeconds(modified) : null,
                };
            }
        }
        catch (Exception)
        {
            return (NoItems, "Could not read the phone's Downloads list.");
        }

        try
        {
            // The same resolved-path check as the Remote Files tab: a link out of the Download folder is not followed.
            if (RemoteFilesScreen.ExistingPhoneDownloadFolder(parent) is { } full)
                foreach (var directory in new DirectoryInfo(full).EnumerateDirectories())
                    if (SafeSegment(directory.Name) && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        if (!folders.TryGetValue(directory.Name, out var inside)) folders[directory.Name] = inside = new HashSet<string>(StringComparer.Ordinal);
                        try { foreach (var entry in Directory.EnumerateFileSystemEntries(directory.FullName)) inside.Add(System.IO.Path.GetFileName(entry)); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Scoped storage may hide directory metadata; the MediaStore entries stay.
        }

        return ([.. folders.Select(pair => new SiteItemDto { Name = pair.Key, IsFolder = true, ChildCount = pair.Value.Count }), .. files.Values], null);
    }

    private static bool SafeSegment(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0');

    private static bool SafeRelative(string path) =>
        !System.IO.Path.IsPathRooted(path) && !path.Contains('\\') && path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);

    private static bool TryRelative(string mediaPath, out string relative)
    {
        relative = "";
        var normalized = mediaPath.Replace('\\', '/').Trim('/');
        const string root = "Download";
        if (!normalized.Equals(root, StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        relative = normalized.Length == root.Length ? "" : normalized[(root.Length + 1)..];
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rows

    private static string DetailOf(SiteItemDto item)
    {
        if (item.IsFolder) return RemoteFileList.Detail(item, DateTimeOffset.Now);
        var parts = new List<string> { item.Size >= 0 ? RemoteFileList.Size(item.Size) : "Size unknown" };
        if (item.Modified is { } modified)
            parts.Add(DateTimeOffset.Now - modified < TimeSpan.FromMinutes(1) ? "Just now"
                : modified.ToLocalTime().Date == DateTimeOffset.Now.ToLocalTime().Date.AddDays(-1) ? "Yesterday" // x10b writes the day alone
                : Fmt.When(modified, DateTimeOffset.Now));
        return string.Join(" · ", parts);
    }

    private void OpenFile(string name)
    {
        var relative = RemoteTransfers.DownloadRelative(_path) ?? "";
        var path = System.IO.Path.Combine(AndroidHost.SharedDownloadFolder, relative, name);
        if (!Outside.OpenFile(_activity, path)) _activity.ShowMessage("The file could not be opened. It may have been moved or deleted.");
    }

    private void OnRowTapped(Row row)
    {
        if (row.Item.IsFolder) Load(_path.Length == 0 ? row.Item.Name : _path + "/" + row.Item.Name);
        else if (!row.Running) OpenFile(row.Item.Name);
    }

    public void Dispose()
    {
        _disposed = true;
        _ui.RemoveCallbacksAndMessages(null);
        if (_running is not null) _running.PropertyChanged -= OnRunningChanged;
    }

    private sealed class FolderAdapter(PhoneFolderPage page) : RecyclerView.Adapter
    {
        public override int ItemCount => page._rows.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) => new RowHolder(page);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) => ((RowHolder)holder).Bind(page._rows[position]);
    }

    private sealed class RowHolder : RecyclerView.ViewHolder
    {
        private readonly PhoneFolderPage _p;
        private readonly LinearLayout _root;
        private readonly ImageView _icon;
        private readonly TextView _name, _detail;
        private readonly MaterialButton _open;
        private readonly LinearProgressIndicator _bar;
        private Row? _row;

        public RowHolder(PhoneFolderPage page) : this(page, new LinearLayout(page._activity)) { }

        private RowHolder(PhoneFolderPage p, LinearLayout root) : base(root)
        {
            _p = p;
            _root = root;
            root.Orientation = Orientation.Vertical;
            var layout = new RecyclerView.LayoutParams(-1, -2);
            layout.SetMargins(p.Dp(16), 0, p.Dp(16), 0);
            root.LayoutParameters = layout;
            root.SetMinimumHeight(p.Dp(62));
            root.SetPadding(p.Dp(8), 0, p.Dp(8), 0);
            root.Clickable = true;
            root.Focusable = true;
            var line = new LinearLayout(p._activity) { Orientation = Orientation.Horizontal };
            line.SetGravity(GravityFlags.CenterVertical);
            _icon = new ImageView(p._activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            _icon.SetScaleType(ImageView.ScaleType.CenterInside);
            _icon.SetPadding(p.Dp(8), p.Dp(8), p.Dp(8), p.Dp(8));
            line.AddView(_icon, new LinearLayout.LayoutParams(p.Dp(40), p.Dp(40)));
            var labels = new LinearLayout(p._activity) { Orientation = Orientation.Vertical };
            labels.SetPadding(p.Dp(16), p.Dp(11), p.Dp(8), p.Dp(11));
            _name = p.Text("", 15, Resource.Attribute.colorOnSurface);
            _name.SetSingleLine(true);
            _name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _detail = p.Text("", 12.5f, Resource.Attribute.colorOnSurfaceVariant);
            _detail.SetSingleLine(true);
            _detail.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            labels.AddView(_name);
            labels.AddView(_detail);
            line.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1));
            _open = new MaterialButton(p._activity) { Text = "Open", CornerRadius = p.Dp(18), Visibility = ViewStates.Gone };
            _open.SetAllCaps(false);
            _open.TextSize = 15;
            _open.InsetTop = _open.InsetBottom = 0;
            _open.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(p._activity, Resource.Attribute.colorSecondaryContainer)));
            _open.SetTextColor(new Color(Motion.Attr(p._activity, Resource.Attribute.colorOnSecondaryContainer)));
            _open.SetIconResource(Resource.Drawable.ic_remote_open);
            _open.IconTint = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(p._activity, Resource.Attribute.colorOnSecondaryContainer)));
            _open.IconPadding = p.Dp(8);
            _open.SetPadding(p.Dp(16), 0, p.Dp(18), 0);
            _open.Click += (_, _) => { if (_row is { } r && !r.Item.IsFolder) _p.OpenFile(r.Item.Name); };
            line.AddView(_open, new LinearLayout.LayoutParams(-2, p.Dp(36)));
            root.AddView(line, new LinearLayout.LayoutParams(-1, -2));
            _bar = new LinearProgressIndicator(p._activity) { Max = 1000, TrackThickness = p.Dp(6), TrackCornerRadius = p.Dp(3), TrackStopIndicatorSize = 0, IndicatorTrackGapSize = 0 /* (mockup low a) */, Visibility = ViewStates.Gone, ImportantForAccessibility = ImportantForAccessibility.No };
            _bar.TrackColor = Motion.Attr(p._activity, Resource.Attribute.colorSurfaceContainerHighest);
            _bar.SetIndicatorColor(Motion.Attr(p._activity, Resource.Attribute.colorPrimary));
            root.AddView(_bar, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = p.Dp(64), RightMargin = p.Dp(12), BottomMargin = p.Dp(12) });
            root.Click += (_, _) => { if (_row is { } r) _p.OnRowTapped(r); };
        }

        public void Bind(Row row)
        {
            _row = row;
            var item = row.Item;
            var kind = FileKinds.Of(item.Name);
            var fileIcon = FileIcons.For(kind);
            _icon.SetImageResource(item.IsFolder ? Resource.Drawable.ic_remote_folder : kind switch
            {
                FileKind.Audio => Resource.Drawable.ic_remote_audio,
                FileKind.Document => Resource.Drawable.ic_remote_document,
                FileKind.Archive => Resource.Drawable.ic_remote_archive,
                FileKind.Image => Resource.Drawable.ic_remote_image,
                FileKind.Video => Resource.Drawable.ic_remote_video,
                _ => fileIcon.Drawable,
            });
            _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(item.IsFolder
                ? Motion.Attr(_p._activity, Resource.Attribute.colorPrimary) : RemoteFilesScreen.RemoteIconColor(_p._activity, kind, fileIcon)));
            var tile = new GradientDrawable();
            tile.SetCornerRadius(_p.Dp(11));
            tile.SetColor(new Color(Motion.Attr(_p._activity, Resource.Attribute.colorSurfaceContainerHighest)));
            _icon.Background = tile;
            _name.Text = item.Name;
            bool inMarkFolder = _p._path == _p._markFolder;
            bool outlined = inMarkFolder && !item.IsFolder && (row.Running || _p._mark == item.Name);
            bool pill = inMarkFolder && !item.IsFolder && !row.Running && _p._pill == item.Name;
            string detail = row.Running && _p._running is { } job ? job.Subtitle : DetailOf(item);
            _detail.Text = detail;
            _open.Visibility = pill ? ViewStates.Visible : ViewStates.Gone;
            _open.ContentDescription = "Open " + item.Name;
            if (row.Running && _p._running is { } running)
            {
                _bar.Visibility = ViewStates.Visible;
                if (running.Indeterminate) { if (!_bar.Indeterminate) _bar.Indeterminate = true; }
                else
                {
                    if (_bar.Indeterminate) _bar.Indeterminate = false;
                    _bar.SetProgressCompat((int)(running.Progress * 1000), false);
                }
            }
            else _bar.Visibility = ViewStates.Gone;
            if (outlined)
            {
                var outline = new GradientDrawable();
                outline.SetCornerRadius(_p.Dp(16));
                outline.SetColor(Color.Transparent);
                outline.SetStroke(_p.Dp(2), new Color(Motion.Attr(_p._activity, Resource.Attribute.colorPrimary)));
                _root.Background = outline;
            }
            else _root.Background = new TypedValueHolder(_p._activity).SelectableBackground();
            _root.ContentDescription = item.Name + ", " + detail + (item.IsFolder ? ". Open folder" : row.Running ? "" : ". Open file");
        }
    }
}
