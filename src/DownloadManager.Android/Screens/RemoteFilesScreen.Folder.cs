using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Inbound.App.Screens;

/// <summary>The folder view of the Remote Files redesign: app bar or selection bar, the two tabs, breadcrumbs, rows, bottom bar.</summary>
internal sealed partial class RemoteFilesScreen
{
    private LinearLayout? _folderHeader, _folderFooter, _folderMessage;
    private RecyclerView? _folderList;
    private FolderAdapter? _folderAdapter;
    private LinearProgressIndicator? _folderProgress;
    private string? _listIdentity;

    private int Dp(int value) => Motion.Dp(_activity, value);
    private int Attr(int attribute) => Motion.Attr(_activity, attribute);

    private void RenderFolder()
    {
        EnsureFolderChrome();
        UpdateFolderUi();
    }

    private void EnsureFolderChrome()
    {
        if (_surface.ChildCount == 1 && _folderHeader?.Parent is ViewGroup current && ReferenceEquals(current.Parent, _surface)) return;

        // _surface is the stable root. The legacy _content stays in _scroll so ResetChrome can restore every other screen.
        _surface.RemoveAllViews();
        var shell = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        shell.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorSurface)));
        _folderHeader = new LinearLayout(_activity) { Orientation = Orientation.Vertical };

        var body = new FrameLayout(_activity);
        _folderList = new RecyclerView(_activity);
        _folderList.SetLayoutManager(new LinearLayoutManager(_activity));
        _folderAdapter = new FolderAdapter(this);
        _folderList.SetAdapter(_folderAdapter);
        _folderList.SetClipToPadding(false);
        _folderList.SetPadding(0, Dp(2), 0, Dp(16));
        body.AddView(_folderList, new FrameLayout.LayoutParams(-1, -1));
        _folderMessage = new LinearLayout(_activity) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _folderMessage.SetPadding(Dp(24), Dp(16), Dp(24), 0);
        body.AddView(_folderMessage, new FrameLayout.LayoutParams(-1, -2));

        _folderFooter = new LinearLayout(_activity) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _folderFooter.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        _folderFooter.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

        shell.AddView(_folderHeader, new LinearLayout.LayoutParams(-1, -2));
        shell.AddView(body, new LinearLayout.LayoutParams(-1, 0, 1));
        shell.AddView(_folderFooter, new LinearLayout.LayoutParams(-1, -2));
        _surface.AddView(shell, new FrameLayout.LayoutParams(-1, -1));
        _listIdentity = null;
    }

    private void UpdateFolderUi()
    {
        if (Android.OS.Looper.MyLooper() != Android.OS.Looper.MainLooper) { _activity.RunOnUiThread(UpdateFolderUi); return; }
        if (_closed || !_inFolder || _showTransfers || _adding || _folderHeader?.Parent is null) return;
        var pane = Active;
        BuildHeader(pane);
        BuildRows(pane);
        BuildFooter(pane);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Header

    private TextView FolderText(string text, float size, int color)
    {
        var view = new TextView(_activity) { Text = text, TextSize = size };
        view.SetTextColor(new Color(Attr(color)));
        return view;
    }

    private View IconButton(int drawable, string description, Action click, int tintAttr = 0)
    {
        var button = new ImageButton(_activity) { ContentDescription = description };
        button.SetImageResource(drawable);
        button.ImageTintList = ColorStateList.ValueOf(new Color(Attr(tintAttr == 0 ? Resource.Attribute.colorOnSurface : tintAttr)));
        button.SetScaleType(ImageView.ScaleType.CenterInside);
        button.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        button.Background = new TypedValueHolder(_activity).SelectableBackground();
        button.Click += (_, _) => click();
        return button;
    }

    private void BuildHeader(Pane pane)
    {
        _folderHeader!.RemoveAllViews();
        bool selecting = pane.Selected.Count > 0;
        var bar = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(4), 0, Dp(4), 0);
        if (selecting) bar.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        if (selecting)
        {
            bar.AddView(IconButton(Resource.Drawable.ic_close, "Clear selection", () => { pane.Selected.Clear(); UpdateFolderUi(); }),
                new LinearLayout.LayoutParams(Dp(48), Dp(48)));
            var count = FolderText(pane.Selected.Count + " selected", 20, Resource.Attribute.colorOnSurface);
            count.SetPadding(Dp(12), 0, 0, 0);
            bar.AddView(count, new LinearLayout.LayoutParams(0, -2, 1));
            bar.AddView(IconButton(Resource.Drawable.ic_remote_selall, "Select all", () => SelectAll(pane)), new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        }
        else
        {
            bar.AddView(IconButton(Resource.Drawable.ic_arrow_back, "Back", () => HandleBack()), new LinearLayout.LayoutParams(Dp(48), Dp(48)));
            var title = FolderText(PaneName(_panes[0]), 22, Resource.Attribute.colorOnSurface);
            title.SetSingleLine(true);
            title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            title.SetPadding(Dp(12), 0, 0, 0);
            title.Selected = true;
            bar.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        }
        View more = IconButton(Resource.Drawable.ic_more, "More options", () => { });
        more.Click += (_, _) => ShowFolderMenu(more);
        bar.AddView(more, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        _folderHeader.AddView(bar, new LinearLayout.LayoutParams(-1, Dp(72)));

        var tabs = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        tabs.AddView(CreateTab(0), new LinearLayout.LayoutParams(0, Dp(46), 1));
        tabs.AddView(CreateTab(1), new LinearLayout.LayoutParams(0, Dp(46), 1));
        _folderHeader.AddView(tabs);

        _folderHeader.AddView(CreateCrumbs(pane));
        var progress = new LinearProgressIndicator(_activity) { Indeterminate = true, TrackThickness = Dp(3) };
        progress.Visibility = pane.Loading ? ViewStates.Visible : ViewStates.Invisible;
        progress.ContentDescription = "Loading this folder";
        _folderProgress = progress;
        _folderHeader.AddView(progress, new LinearLayout.LayoutParams(-1, Dp(3)));
    }

    private View CreateTab(int index)
    {
        var pane = _panes[index];
        bool on = index == _active;
        var tab = new FrameLayout(_activity) { Clickable = true, Focusable = true };
        tab.Background = new TypedValueHolder(_activity).SelectableBackground();
        var label = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        label.SetGravity(GravityFlags.Center);
        var text = FolderText(PaneName(pane), 15, on ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurfaceVariant);
        text.SetSingleLine(true);
        text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        label.AddView(text, new LinearLayout.LayoutParams(-2, -2));
        if (on)
        {
            var caret = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            caret.SetImageResource(Resource.Drawable.ic_remote_caret);
            caret.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorPrimary)));
            label.AddView(caret, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { LeftMargin = Dp(2) });
        }
        tab.AddView(label, new FrameLayout.LayoutParams(-2, -2, GravityFlags.Center) { LeftMargin = Dp(8), RightMargin = Dp(8) });
        if (on)
        {
            var line = new GradientDrawable();
            line.SetColor(new Color(Attr(Resource.Attribute.colorPrimary)));
            line.SetCornerRadii(new float[] { Dp(3), Dp(3), Dp(3), Dp(3), 0, 0, 0, 0 });
            var underline = new View(_activity) { Background = line };
            tab.AddView(underline, new FrameLayout.LayoutParams(Dp(48), Dp(3), GravityFlags.Bottom | GravityFlags.CenterHorizontal));
        }
        tab.ContentDescription = PaneName(pane) + (on ? ", selected. Choose what this pane shows" : ". Show this pane");
        tab.Click += (_, _) =>
        {
            if (index == _active) ShowSwitcher();
            else { _active = index; UpdateFolderUi(); }
        };
        return tab;
    }

    private View CreateCrumbs(Pane pane)
    {
        var crumbs = pane.IsPhone ? RemoteCrumbs.BuildPhone(pane.Path)
            : RemoteCrumbs.Build(RemoteCrumbs.RootLabel(pane.Site!), PaneRoot(pane), pane.Path);
        var scroll = new HorizontalScrollView(_activity) { HorizontalScrollBarEnabled = false };
        var strip = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        strip.SetGravity(GravityFlags.CenterVertical);
        strip.SetPadding(Dp(16), Dp(10), Dp(16), Dp(6));
        for (int i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            bool current = i == crumbs.Count - 1;
            var chip = FolderText(crumb.Label, 14, current ? Resource.Attribute.colorOnSurface : Resource.Attribute.colorOnSurfaceVariant);
            chip.SetSingleLine(true);
            chip.Gravity = GravityFlags.CenterVertical;
            chip.SetPadding(Dp(12), 0, Dp(12), 0);
            var background = new GradientDrawable();
            background.SetCornerRadius(Dp(8));
            background.SetColor(new Color(Attr(current ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurface)));
            if (!current) background.SetStroke(Dp(1), new Color(Attr(Resource.Attribute.colorOutlineVariant)));
            chip.Background = background;
            chip.ContentDescription = current ? crumb.Label + ", this folder" : "Open " + crumb.Label;
            if (!current) chip.Click += (_, _) => { if (!_mutation) _ = BrowsePaneAsync(pane, crumb.Path, false); };
            strip.AddView(chip, new LinearLayout.LayoutParams(-2, Dp(32)));
            if (i < crumbs.Count - 1)
            {
                var chevron = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
                chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
                chevron.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
                strip.AddView(chevron, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { LeftMargin = Dp(3), RightMargin = Dp(3) });
            }
        }
        scroll.AddView(strip);
        scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Right));
        return scroll;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rows

    private void BuildRows(Pane pane)
    {
        string identity = _active + "|" + (pane.Site?.Id ?? "phone") + "|" + pane.Path + "|" + pane.Generation + "|" + pane.Sort;
        bool reset = identity != _listIdentity;
        _listIdentity = identity;
        _folderAdapter!.NotifyDataSetChanged();
        if (reset) _folderList!.ScrollToPosition(0);

        _folderMessage!.RemoveAllViews();
        string? message = pane.Error;
        if (message is null && !pane.Loading && pane.Rows.Count == 0) message = "This folder is empty.";
        if (message is null) { _folderMessage.Visibility = ViewStates.Gone; return; }
        _folderMessage.Visibility = ViewStates.Visible;
        _folderMessage.AddView(FolderText(message, 14, Resource.Attribute.colorOnSurfaceVariant));
        if (pane.Error is null) return;
        if (pane.NeedsSignIn && pane.Site is { } signIn)
        {
            if (CanSignIn(signIn)) AddMessageAction("Sign in", () => { _pendingChoice = (_panes.ToList().IndexOf(pane), signIn.Id); BeginCloudSignIn(signIn); });
        }
        else if (pane.KeyProblem && pane.Site is { } sftp)
        {
            if (_secureHostConnection) AddMessageAction("Review server key", () =>
                _ = ReviewSftpKeyAsync(FreshSite(sftp.Id) ?? sftp, () => _ = BrowsePaneAsync(pane, null, true)));
        }
        else if (!pane.Loading) AddMessageAction("Try again", () => _ = BrowsePaneAsync(pane, pane.Path, true));
    }

    private void AddMessageAction(string text, Action action)
    {
        var button = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = text };
        button.SetAllCaps(false);
        button.Click += (_, _) => action();
        _folderMessage!.AddView(button, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(12) });
    }

    private sealed class FolderAdapter(RemoteFilesScreen screen) : RecyclerView.Adapter
    {
        public override int ItemCount => screen.Active.Rows.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) => new RowHolder(screen);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
            ((RowHolder)holder).Bind(screen.Active.Rows[position]);
    }

    private sealed class RowHolder : RecyclerView.ViewHolder
    {
        private readonly RemoteFilesScreen _s;
        private readonly LinearLayout _root;
        private readonly ImageView _icon;
        private readonly TextView _name, _detail;
        private SiteItemDto? _item;

        public RowHolder(RemoteFilesScreen screen) : this(screen, new LinearLayout(screen._activity)) { }

        private RowHolder(RemoteFilesScreen screen, LinearLayout root) : base(root)
        {
            _s = screen;
            _root = root;
            root.Orientation = Orientation.Horizontal;
            root.SetGravity(GravityFlags.CenterVertical);
            root.SetPadding(screen.Dp(8), 0, screen.Dp(8), 0);
            root.Clickable = true;
            root.Focusable = true;
            var layout = new RecyclerView.LayoutParams(-1, screen.Dp(62));
            layout.SetMargins(screen.Dp(16), 0, screen.Dp(16), 0);
            root.LayoutParameters = layout;
            _icon = new ImageView(screen._activity);
            _icon.SetScaleType(ImageView.ScaleType.CenterInside);
            _icon.SetPadding(screen.Dp(8), screen.Dp(8), screen.Dp(8), screen.Dp(8));
            _icon.Clickable = true;
            root.AddView(_icon, new LinearLayout.LayoutParams(screen.Dp(40), screen.Dp(40)));
            var labels = new LinearLayout(screen._activity) { Orientation = Orientation.Vertical };
            labels.SetPadding(screen.Dp(16), 0, screen.Dp(8), 0);
            _name = screen.FolderText("", 15, Resource.Attribute.colorOnSurface);
            _name.SetSingleLine(true);
            _name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            _detail = screen.FolderText("", 12.5f, Resource.Attribute.colorOnSurfaceVariant);
            _detail.SetSingleLine(true);
            _detail.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            labels.AddView(_name);
            labels.AddView(_detail);
            root.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1));
            root.Click += (_, _) => { if (_item is { } item) screen.OnRowTapped(item); };
            root.LongClick += (_, _) =>
            {
                if (_item is { } item && SafeName(item.Name) && !screen.Active.Selected.Contains(item.Name)) screen.ToggleSelected(screen.Active, item);
            };
            _icon.Click += (_, _) => { if (_item is { } item) screen.ToggleSelected(screen.Active, item); };
        }

        public void Bind(SiteItemDto item)
        {
            _item = item;
            var pane = _s.Active;
            bool selected = pane.Selected.Contains(item.Name);
            var kind = FileKinds.Of(item.Name);
            var fileIcon = FileIcons.For(kind);
            int drawable = selected ? Resource.Drawable.ic_remote_check : item.IsFolder ? Resource.Drawable.ic_remote_folder : kind switch
            {
                FileKind.Audio => Resource.Drawable.ic_remote_audio,
                FileKind.Document => Resource.Drawable.ic_remote_document,
                FileKind.Archive => Resource.Drawable.ic_remote_archive,
                FileKind.Image => Resource.Drawable.ic_remote_image,
                FileKind.Video => Resource.Drawable.ic_remote_video,
                _ => fileIcon.Drawable
            };
            _icon.SetImageResource(drawable);
            int tint = selected ? _s.Attr(Resource.Attribute.colorOnPrimary)
                : item.IsFolder ? _s.Attr(Resource.Attribute.colorPrimary) : FileIcons.Color(_s._activity, fileIcon);
            _icon.ImageTintList = ColorStateList.ValueOf(new Color(tint));
            var tile = new GradientDrawable();
            tile.SetCornerRadius(_s.Dp(selected ? 20 : 11));
            tile.SetColor(new Color(_s.Attr(selected ? Resource.Attribute.colorPrimary : Resource.Attribute.colorSurfaceContainerHighest)));
            _icon.Background = tile;
            _name.Text = item.Name;
            string detail = RemoteFileList.Detail(item, DateTimeOffset.Now);
            _detail.Text = detail;
            if (selected)
            {
                var background = new GradientDrawable();
                background.SetCornerRadius(_s.Dp(16));
                background.SetColor(new Color(_s.Attr(Resource.Attribute.colorSecondaryContainer)));
                _root.Background = background;
            }
            else _root.Background = new TypedValueHolder(_s._activity).SelectableBackground();
            _root.ContentDescription = (selected ? "Selected, " : "") + item.Name + ", " + detail + (item.IsFolder ? ". Open folder" : ". File options");
            _icon.ContentDescription = (selected ? "Deselect " : "Select ") + item.Name;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Bottom bar

    private readonly record struct TransferPlan(bool Enabled, string Button, int Icon, string Caption);

    private bool CanDeleteSelection(Pane pane) =>
        pane.IsPhone ? RemoteTransfers.DownloadRelative(pane.Path) is not null : SameHost() && _secureHostConnection && _deleteFiles();

    /// <summary>What the bottom bar's main button does: the selection in the active pane goes to the other pane's folder.</summary>
    private TransferPlan PlanFor(Pane source, Pane target)
    {
        var selected = SelectedItems(source);
        bool hasFiles = selected.Any(item => !item.IsFolder);
        string targetName = PaneName(target);
        switch (RemoteTransfers.Mode(source.IsPhone, target.IsPhone))
        {
            case RemoteTransferMode.Download:
                if (!_phoneHost)
                {
                    string? folder = source.Site?.LocalFolder;
                    bool ok = !string.IsNullOrWhiteSpace(folder) && SameHost();
                    return new(ok && (!hasFiles || _safeDownloads()), "To " + _hostName, Resource.Drawable.ic_remote_download,
                        !ok ? "This Host has no download folder for the location" : hasFiles && !_safeDownloads()
                            ? "Update this Host to download files without replacing existing files." : "To " + _hostName + ": " + folder);
                }
                if (RemoteTransfers.DownloadRelative(target.Path) is null)
                    return new(false, "To this phone", Resource.Drawable.ic_remote_download, "Open a folder in Download on this phone first");
                return new(SameHost() && !_mutation && (!hasFiles || _safeDownloads()), "To this phone", Resource.Drawable.ic_remote_download,
                    hasFiles && !_safeDownloads() ? "Update this Host to download files without replacing existing files."
                        : "To " + RemoteTransfers.PhoneLabel(target.Path));
            case RemoteTransferMode.Upload:
                if (!_phoneHost)
                    return new(false, "To " + targetName, Resource.Drawable.ic_remote_up, "Uploads from this phone need this phone as the Host");
                if (RemoteTransfers.DownloadRelative(source.Path) is null)
                    return new(false, "To " + targetName, Resource.Drawable.ic_remote_up, "Open a folder in Download first");
                return new(SameHost() && !_mutation && target.Error is null && !target.Loading, "To " + targetName, Resource.Drawable.ic_remote_up,
                    "To " + RemoteFileList.Destination(targetName, RemoteCrumbs.Normalize(target.Path)));
            case RemoteTransferMode.LocationToLocation:
                // r6: the Host's rclone relays the copy from one location to the other; an older Host has no such transfer.
                if (!_remoteCopy())
                    return new(false, "To " + targetName, Resource.Drawable.ic_remote_arrow,
                        "Copying between two locations isn't available yet");
                return new(SameHost() && !_mutation && target.Error is null && !target.Loading && !target.NeedsSignIn && !target.KeyProblem,
                    "To " + targetName, Resource.Drawable.ic_remote_arrow,
                    "To " + RemoteFileList.Destination(targetName, RemoteCrumbs.Normalize(target.Path)));
            default:
                return new(false, "To " + targetName, Resource.Drawable.ic_remote_download, "Pick a location in the other tab to copy to");
        }
    }

    private void BuildFooter(Pane pane)
    {
        _folderFooter!.RemoveAllViews();
        if (pane.Selected.Count == 0) { _folderFooter.Visibility = ViewStates.Gone; return; }
        _folderFooter.Visibility = ViewStates.Visible;
        var plan = PlanFor(pane, Other);
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var summary = FolderText(RemoteFileList.Summary(SelectedItems(pane)), 16, Resource.Attribute.colorOnSurface);
        summary.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        info.AddView(summary);
        var caption = FolderText(plan.Caption, 13, Resource.Attribute.colorOnSurfaceVariant);
        caption.SetSingleLine(true);
        caption.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        info.AddView(caption);
        row.AddView(info, new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(8) });
        if (CanDeleteSelection(pane))
            row.AddView(IconButton(Resource.Drawable.ic_remote_trash, "Delete selected items", () => ConfirmDeleteSelection(pane)),
                new LinearLayout.LayoutParams(Dp(48), Dp(48)) { RightMargin = Dp(8) });
        var main = new MaterialButton(_activity) { Text = plan.Button, Enabled = plan.Enabled, CornerRadius = Dp(28) };
        main.SetAllCaps(false);
        main.TextSize = 15;
        main.InsetTop = 0;
        main.InsetBottom = 0;
        main.SetPadding(Dp(18), 0, Dp(20), 0);
        main.SetIconResource(plan.Icon);
        main.IconGravity = MaterialButton.IconGravityTextStart;
        main.IconPadding = Dp(10);
        main.Click += (_, _) => { if (plan.Enabled) _ = StartSelectionTransferAsync(pane, Other); };
        row.AddView(main, new LinearLayout.LayoutParams(-2, Dp(56)));
        _folderFooter.AddView(row);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Menu (r5)

    private void ShowFolderMenu(View anchor)
    {
        var pane = Active;
        var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var cardBackground = new GradientDrawable();
        cardBackground.SetCornerRadius(Dp(8));
        cardBackground.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHigh)));
        card.Background = cardBackground;
        card.SetPadding(0, Dp(8), 0, Dp(8));
        var popup = new PopupWindow(card, Dp(230), ViewGroup.LayoutParams.WrapContent, true) { OutsideTouchable = true, Elevation = Dp(8) };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));

        void Item(int icon, string text, Action action, string? end = null, bool sub = false, bool on = false)
        {
            var item = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            item.SetGravity(GravityFlags.CenterVertical);
            item.Background = new TypedValueHolder(_activity).SelectableBackground();
            item.SetPadding(sub ? Dp(52) : Dp(16), 0, Dp(16), 0);
            if (icon != 0 && !sub)
            {
                var image = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
                image.SetImageResource(icon);
                image.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurface)));
                item.AddView(image, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(14) });
            }
            var label = FolderText(text, 15, on ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurface);
            item.AddView(label, new LinearLayout.LayoutParams(0, -2, 1));
            if (on)
            {
                var check = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
                check.SetImageResource(Resource.Drawable.ic_remote_check);
                check.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorPrimary)));
                item.AddView(check, new LinearLayout.LayoutParams(Dp(20), Dp(20)));
            }
            else if (end is not null) item.AddView(FolderText(end, 13, Resource.Attribute.colorOnSurfaceVariant));
            item.ContentDescription = text + (end is null ? "" : ", " + end) + (on ? ", selected" : "");
            item.Click += (_, _) => { popup.Dismiss(); action(); };
            card.AddView(item, new LinearLayout.LayoutParams(-1, Dp(sub ? 42 : 48)));
        }

        Item(Resource.Drawable.ic_refresh, "Refresh", () => _ = BrowsePaneAsync(pane, pane.Path, true));
        Item(Resource.Drawable.ic_remote_sort, "Sort by", () => { });
        foreach (var (sort, label) in new[] { (RemoteSort.Name, "Name"), (RemoteSort.Newest, "Newest first"), (RemoteSort.Largest, "Largest first") })
            Item(0, label, () => { pane.Sort = sort; Resort(pane); UpdateFolderUi(); }, sub: true, on: pane.Sort == sort);
        if (CanMakeFolder(pane)) Item(Resource.Drawable.ic_remote_newfolder, "New folder", () => ShowNewFolderDialog(pane));
        Item(Resource.Drawable.ic_remote_selall, "Select all", () => SelectAll(pane));
        int active = _transfers.Count(t => t.State is "queued" or "running" or "paused");
        Item(Resource.Drawable.ic_remote_transfers, "Transfers", OpenTransfers, active > 0 ? active.ToString() : null);
        popup.ShowAsDropDown(anchor, 0, -Dp(8), GravityFlags.End);
    }

    private bool CanMakeFolder(Pane pane) => pane.IsPhone
        ? RemoteTransfers.DownloadRelative(pane.Path) is not null
        : SameHost() && _secureHostConnection && pane.Error is null && pane.Site!.Capabilities.Contains(SiteCapabilities.NewFolder);
}
