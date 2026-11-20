using Android.Database;
using Android.Provider;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Runtime;
using Inbound.App.Host;
using Color = Android.Graphics.Color;

namespace Inbound.App.Screens;

internal sealed partial class RemoteFilesScreen
{
    private readonly HashSet<string> _selectedRemoteFiles = new(StringComparer.Ordinal);
    private bool _showPhoneFiles;
    private LinearLayout? _folderHeader;
    private LinearLayout? _folderRows;
    private LinearLayout? _folderFooter;
    private string? _selectionSiteId;
    private string? _selectionPath;
    private ScrollView? _folderRowsScroll;

    private sealed record LocalFileEntry(string Name, bool IsFolder, long Size, long ModifiedSeconds, Android.Net.Uri? Uri = null);
    private sealed record LocalFilesSnapshot(IReadOnlyList<LocalFileEntry> Items, string? Error);

    /// <summary>
    /// Reads files only from Android's scoped Downloads MediaStore collection.
    /// On the phone Host, also enumerate directory names under the allowed root:
    /// MediaStore cannot enumerate empty folders that are valid download destinations.
    /// </summary>
    private LocalFilesSnapshot ReadLocalFiles(string relativePath)
    {
        if (!SafeRelativeFolder(relativePath))
            return new LocalFilesSnapshot(Array.Empty<LocalFileEntry>(), "This folder path is not available.");

        var directFiles = new Dictionary<string, LocalFileEntry>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var parent = relativePath.Trim('/');
        var prefix = parent.Length == 0 ? "" : parent + "/";
        var projection = new[]
        {
            MediaStore.IMediaColumns.DisplayName,
            MediaStore.IMediaColumns.RelativePath,
            MediaStore.IMediaColumns.Size,
            MediaStore.IMediaColumns.DateModified,
            "_id"
        };

        try
        {
            using ICursor? cursor = _activity.ContentResolver?.Query(
                MediaStore.Downloads.ExternalContentUri, projection, null, null,
                MediaStore.IMediaColumns.RelativePath + ", " + MediaStore.IMediaColumns.DisplayName);
            if (cursor is null) return new LocalFilesSnapshot(Array.Empty<LocalFileEntry>(), "Android did not return the Downloads list.");

            int nameColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
            int pathColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath);
            int sizeColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.Size);
            int modifiedColumn = cursor.GetColumnIndex(MediaStore.IMediaColumns.DateModified);
            int idColumn = cursor.GetColumnIndex("_id");
            if (nameColumn < 0 || pathColumn < 0)
                return new LocalFilesSnapshot(Array.Empty<LocalFileEntry>(), "Android did not provide Downloads folder details.");

            while (cursor.MoveToNext())
            {
                var displayName = cursor.GetString(nameColumn) ?? "";
                var mediaPath = cursor.GetString(pathColumn) ?? "";
                if (!SafeSegment(displayName) || !TryDownloadsRelativePath(mediaPath, out var directoryPath))
                    continue;
                var itemPath = directoryPath.Length == 0 ? displayName : directoryPath + "/" + displayName;
                if (!itemPath.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var remainder = itemPath[prefix.Length..];
                if (remainder.Length == 0) continue;
                int slash = remainder.IndexOf('/');
                if (slash >= 0)
                {
                    var childFolder = remainder[..slash];
                    if (SafeSegment(childFolder)) folders.Add(childFolder);
                    continue;
                }

                long size = sizeColumn >= 0 && !cursor.IsNull(sizeColumn) ? cursor.GetLong(sizeColumn) : -1;
                long modified = modifiedColumn >= 0 && !cursor.IsNull(modifiedColumn) ? cursor.GetLong(modifiedColumn) : 0;
                var uri = idColumn >= 0 && !cursor.IsNull(idColumn)
                    ? Android.Content.ContentUris.WithAppendedId(MediaStore.Downloads.ExternalContentUri!, cursor.GetLong(idColumn)) : null;
                directFiles[displayName] = new LocalFileEntry(displayName, false, Math.Max(-1, size), modified, uri);
            }
        }
        catch (Exception)
        {
            return new LocalFilesSnapshot(Array.Empty<LocalFileEntry>(), "Could not read the phone's scoped Downloads list.");
        }

        if (_phoneHost) AddPhoneDownloadDirectories(parent, folders);
        var entries = folders.Select(name => new LocalFileEntry(name, true, -1, 0))
            .Concat(directFiles.Values)
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new LocalFilesSnapshot(entries, null);
    }

    private static string? ExistingPhoneDownloadFolder(string relativePath)
    {
        if (!SafeRelativeFolder(relativePath)) return null;
        string root = AndroidHost.SharedDownloadFolder;
        var roots = new PathRoots([root]);
        string? resolved = roots.Allowed(Path.Combine(root, relativePath));
        return resolved is not null && Directory.Exists(resolved) ? resolved : null;
    }

    private static void AddPhoneDownloadDirectories(string parent, HashSet<string> folders)
    {
        try
        {
            if (ExistingPhoneDownloadFolder(parent) is not { } allowed) return;
            foreach (var directory in new DirectoryInfo(allowed).EnumerateDirectories())
                if (SafeSegment(directory.Name) && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                    folders.Add(directory.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Scoped-storage differences may hide directory metadata. Keep MediaStore entries.
        }
    }

    /// <summary>Replacement folder body, intended to be wired in place of the legacy renderer.</summary>
    private void RenderFolderC4()
    {
        EnsureFolderChrome();
        if (!_showPhoneFiles && (_selectionSiteId != _site?.Id || _selectionPath != _path))
        {
            _selectedRemoteFiles.Clear();
            _selectionSiteId = _site?.Id;
            _selectionPath = _path;
        }
        _folderRows!.RemoveAllViews();
        UpdateFolderHeader();
        UpdateFolderFooter();

        if (_showPhoneFiles)
        {
            RenderPhoneFiles();
            return;
        }

        if (_site is null)
        {
            AddFolderMessage("Choose a remote location to browse its files.");
            return;
        }

        if (_busy) AddFolderMessage("Loading…");
        if (_error is not null)
        {
            AddFolderMessage(_error);
            if (!_busy) AddFolderAction("Try again", () => _ = BrowseAsync(_path, false, true));
        }

        foreach (var crumb in Breadcrumbs())
            _folderRows.AddView(crumb);

        foreach (var item in _items)
        {
            if (!SafeName(item.Name)) continue;
            bool selected = !item.IsFolder && _selectedRemoteFiles.Contains(item.Name);
            _folderRows.AddView(CreateRemoteRow(item, selected));
        }

        if (!_busy && _error is null && _items.Count == 0)
            AddFolderMessage("This folder is empty.");
        if (!_busy && _items.Count < _total && !string.IsNullOrEmpty(_listing))
            AddFolderAction("Load more", () => _ = BrowseAsync(_path, true, false));
        if (!_safeDownloads())
            AddFolderMessage("Update this Host to download files without replacing existing files.");
    }

    // Keep the existing Render dispatch target while replacing its legacy body.
    private void RenderFolder() => RenderFolderC4();

    private void EnsureFolderChrome()
    {
        if (_surface.ChildCount == 1 && _folderHeader?.Parent is ViewGroup currentShell
            && ReferenceEquals(currentShell.Parent, _surface))
            return;

        // _surface is the stable root. Keep the app bar and transfer footer in its
        // vertical shell; keep the legacy _content in _scroll so ResetChrome can
        // restore all other Remote Files screens without reparenting it.
        _surface.RemoveAllViews();
        var shell = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        shell.SetBackgroundColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurface)));
        _folderHeader = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _folderHeader.SetPadding(Motion.Dp(_activity, 16), Motion.Dp(_activity, 3), Motion.Dp(_activity, 10), 0);

        _folderRowsScroll = new ScrollView(_activity) { FillViewport = false };
        _folderRows = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _folderRows.SetPadding(Motion.Dp(_activity, 16), Motion.Dp(_activity, 6), Motion.Dp(_activity, 16), Motion.Dp(_activity, 16));
        _folderRowsScroll.AddView(_folderRows);

        _folderFooter = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _folderFooter.SetPadding(Motion.Dp(_activity, 16), Motion.Dp(_activity, 14), Motion.Dp(_activity, 16), Motion.Dp(_activity, 13));
        _folderFooter.SetBackgroundColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));

        shell.AddView(_folderHeader, new LinearLayout.LayoutParams(-1, -2));
        shell.AddView(_folderRowsScroll, new LinearLayout.LayoutParams(-1, 0, 1));
        shell.AddView(_folderFooter, new LinearLayout.LayoutParams(-1, -2));
        _surface.AddView(shell, new FrameLayout.LayoutParams(-1, -1));
    }

    private void UpdateFolderHeader()
    {
        _folderHeader!.RemoveAllViews();
        var top = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        var back = new ImageButton(_activity) { Background = null, ContentDescription = "Back" };
        back.SetImageResource(Resource.Drawable.ic_arrow_back);
        back.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        back.Click += (_, _) => HandleBack();
        var backParams = new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 52))
            { LeftMargin = -Motion.Dp(_activity, 12), RightMargin = Motion.Dp(_activity, 5) };
        top.AddView(back, backParams);

        string title = _site?.Name ?? "Remote Files";
        var titleView = FolderText(title, 22, Resource.Attribute.colorOnSurface);
        titleView.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleView.SetSingleLine(true);
        top.AddView(titleView, new LinearLayout.LayoutParams(0, -2, 1));

        var overflow = FolderText("⋮", 28, Resource.Attribute.colorOnSurface);
        overflow.Gravity = GravityFlags.Center;
        overflow.SetMinWidth(Motion.Dp(_activity, 44));
        overflow.ContentDescription = "More options";
        overflow.Click += (_, _) => ShowFolderMenu(overflow);
        top.AddView(overflow, new LinearLayout.LayoutParams(Motion.Dp(_activity, 44), Motion.Dp(_activity, 52)));
        _folderHeader.AddView(top);
        _folderHeader.AddView(CreateFolderTabs());
    }

    private View CreateFolderTabs()
    {
        var tabs = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        tabs.SetPadding(0, Motion.Dp(_activity, 9), Motion.Dp(_activity, 6), Motion.Dp(_activity, 6));
        tabs.AddView(CreateTab("Site", !_showPhoneFiles, () => SetPhoneTab(false)), new LinearLayout.LayoutParams(0, Motion.Dp(_activity, 48), 1));
        tabs.AddView(CreateTab("This phone", _showPhoneFiles, () => SetPhoneTab(true)), new LinearLayout.LayoutParams(0, Motion.Dp(_activity, 48), 1));
        return tabs;
    }

    private View CreateTab(string text, bool selected, Action action)
    {
        var tab = FolderText(text, 16, selected ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurfaceVariant);
        tab.Gravity = GravityFlags.Center;
        tab.Click += (_, _) => action();
        if (selected)
        {
            var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            column.AddView(tab, new LinearLayout.LayoutParams(-1, 0, 1));
            var underline = new View(_activity);
            var line = new Android.Graphics.Drawables.GradientDrawable();
            line.SetColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorPrimary)));
            line.SetCornerRadius(Motion.Dp(_activity, 2));
            underline.Background = line;
            var holder = new FrameLayout(_activity);
            holder.AddView(underline, new FrameLayout.LayoutParams(Motion.Dp(_activity, 52), Motion.Dp(_activity, 3), GravityFlags.Bottom | GravityFlags.CenterHorizontal));
            column.AddView(holder, new LinearLayout.LayoutParams(-1, Motion.Dp(_activity, 4)));
            column.Click += (_, _) => action();
            return column;
        }
        return tab;
    }

    private IEnumerable<View> Breadcrumbs()
    {
        if (string.IsNullOrWhiteSpace(_path)) yield break;
        var segments = _path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) yield break;
        var strip = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        strip.SetPadding(0, Motion.Dp(_activity, 2), 0, Motion.Dp(_activity, 8));
        strip.Orientation = Orientation.Horizontal;
        strip.SetGravity(GravityFlags.CenterVertical);
        var crumbs = new HorizontalScrollView(_activity) { HorizontalScrollBarEnabled = false };
        crumbs.AddView(strip);
        for (int i = 0; i < segments.Length; i++)
        {
            string ancestor = "/" + string.Join("/", segments.Take(i + 1));
            int index = _parents.FindIndex(path => string.Equals(path.TrimEnd('/'), ancestor, StringComparison.Ordinal));
            var chip = FolderText(segments[i], 14,
                i == segments.Length - 1 ? Resource.Attribute.colorOnSurface : Resource.Attribute.colorOnSurfaceVariant);
            chip.SetPadding(Motion.Dp(_activity, 12), Motion.Dp(_activity, 7), Motion.Dp(_activity, 12), Motion.Dp(_activity, 7));
            var chipBackground = new Android.Graphics.Drawables.GradientDrawable();
            chipBackground.SetCornerRadius(Motion.Dp(_activity, 8));
            bool current = i == segments.Length - 1;
            chipBackground.SetColor(new Color(Motion.Attr(_activity,
                current ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurface)));
            if (!current)
                chipBackground.SetStroke(Motion.Dp(_activity, 1), new Color(Motion.Attr(_activity, Resource.Attribute.colorOutlineVariant)));
            chip.Background = chipBackground;
            if (!current)
                chip.Click += (_, _) =>
                {
                    if (index >= 0) BrowseBreadcrumb(index);
                    else if (SameHost() && !_mutation)
                    {
                        CancelCurrent();
                        _parents.Clear();
                        _ = BrowseAsync(ancestor, false, false);
                    }
                };
            strip.AddView(chip);
            if (i < segments.Length - 1)
            {
                var separator = FolderText("›", 20, Resource.Attribute.colorOnSurfaceVariant);
                separator.Gravity = GravityFlags.Center;
                separator.SetPadding(Motion.Dp(_activity, 9), 0, Motion.Dp(_activity, 9), 0);
                strip.AddView(separator);
            }
        }
        yield return crumbs;
    }

    private View CreateRemoteRow(SiteItemDto item, bool selected)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int pad = Motion.Dp(_activity, 8);
        row.SetPadding(pad, Motion.Dp(_activity, 8), pad, Motion.Dp(_activity, 8));
        row.SetMinimumHeight(Motion.Dp(_activity, 62));
        var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        var kind = FileKinds.Of(item.Name);
        var fileIcon = FileIcons.For(kind);
        int outline = kind switch
        {
            FileKind.Audio => Resource.Drawable.ic_remote_audio,
            FileKind.Document => Resource.Drawable.ic_remote_document,
            FileKind.Archive => Resource.Drawable.ic_remote_archive,
            _ => fileIcon.Drawable
        };
        icon.SetImageResource(selected ? Resource.Drawable.ic_check : item.IsFolder ? Resource.Drawable.ic_remote_folder : outline);
        var tint = selected ? Motion.Attr(_activity, Resource.Attribute.colorOnPrimary)
            : item.IsFolder ? Motion.Attr(_activity, Resource.Attribute.colorPrimary) : FileIcons.Color(_activity, fileIcon);
        icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(tint));
        var tile = new Android.Graphics.Drawables.GradientDrawable();
        tile.SetCornerRadius(Motion.Dp(_activity, selected ? 22 : 12));
        tile.SetColor(new Color(Motion.Attr(_activity, selected ? Resource.Attribute.colorPrimary : Resource.Attribute.colorSurfaceContainerHighest)));
        icon.Background = tile;
        int iconPadding = Motion.Dp(_activity, 10);
        icon.SetPadding(iconPadding, iconPadding, iconPadding, iconPadding);
        row.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 40), Motion.Dp(_activity, 40)));

        var labels = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        labels.SetPadding(Motion.Dp(_activity, 15), 0, Motion.Dp(_activity, 8), 0);
        var name = FolderText(item.Name, 16, Resource.Attribute.colorOnSurface);
        name.SetSingleLine(true);
        name.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        labels.AddView(name);
        var detail = item.IsFolder
            ? item.ChildCount is int count ? $"Folder · {count:N0} {(count == 1 ? "item" : "items")}" : "Folder"
            : item.Size >= 0 ? Fmt.Bytes(item.Size) : "Size unknown";
        if (!item.IsFolder && item.Modified is { } modified) detail += " · " + modified.ToLocalTime().ToString("d MMM");
        labels.AddView(FolderText(detail, 13, Resource.Attribute.colorOnSurfaceVariant));
        row.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1));

        if (item.IsFolder)
        {
            row.ContentDescription = item.Name + ", folder. Open";
            row.Click += (_, _) =>
            {
                if (_busy || _path is null) return;
                _selectedRemoteFiles.Clear();
                _parents.Add(_path);
                _ = BrowseAsync(_path.TrimEnd('/') + "/" + item.Name, false, false);
            };
        }
        bool selectable = !item.IsFolder && (CanSelectRemoteFiles() || CanDeleteRemoteFiles());
        var card = new Google.Android.Material.Card.MaterialCardView(_activity)
        {
            Radius = Motion.Dp(_activity, 16),
            CardElevation = 0,
            StrokeWidth = 0,
            Clickable = selectable,
            Focusable = selectable
        };
        card.SetCardBackgroundColor(new Color(Motion.Attr(_activity,
            selected ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurface)));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Motion.Dp(_activity, 2) };
        card.AddView(row);
        if (selectable)
        {
            card.ContentDescription = (selected ? "Selected " : "Select ") + item.Name + ", " + detail;
            card.Click += (_, _) =>
            {
                if (!CanSelectRemoteFiles() && !CanDeleteRemoteFiles()) return;
                if (selected) _selectedRemoteFiles.Remove(item.Name);
                else if (_selectedRemoteFiles.Count < 100) _selectedRemoteFiles.Add(item.Name);
                RenderFolderC4();
            };
        }
        return card;
    }

    private void UpdateFolderFooter()
    {
        _folderFooter!.RemoveAllViews();
        if (_showPhoneFiles)
        {
            RenderPhoneUploadFooter();
            return;
        }
        var actions = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.CenterVertical);
        var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        info.AddView(FolderText(SelectionSummary(), 16, Resource.Attribute.colorOnSurface));
        string? destination = DownloadDestination;
        if (_phoneHost && destination is not null)
            destination = _phoneDestination is { Length: > 0 } subfolder ? "Downloads/" + subfolder : "Downloads";
        var target = FolderText(destination is not null ? "To " + destination : "No destination set", 12,
            Resource.Attribute.colorOnSurfaceVariant);
        target.SetSingleLine(true);
        target.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        info.AddView(target);
        actions.AddView(info, new LinearLayout.LayoutParams(0, -2, 1));
        if (!_showPhoneFiles)
        {
            var canDelete = CanDeleteRemoteFiles();
            var delete = new ImageButton(_activity)
            {
                ContentDescription = "Delete selected remote files",
                Enabled = canDelete && _selectedRemoteFiles.Count > 0,
                Visibility = canDelete ? ViewStates.Visible : ViewStates.Gone,
                Background = null
            };
            delete.SetImageResource(Resource.Drawable.ic_remote_trash);
            int deleteInset = Motion.Dp(_activity, 16);
            delete.SetPadding(deleteInset, deleteInset, deleteInset, deleteInset);
            delete.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(
                _activity, delete.Enabled ? Resource.Attribute.colorOnSurface : Resource.Attribute.colorOnSurfaceVariant)));
            delete.Click += (_, _) =>
            {
                if (!CanDeleteRemoteFiles() || _selectedRemoteFiles.Count == 0) return;
                var names = _items.Where(item => !item.IsFolder && _selectedRemoteFiles.Contains(item.Name))
                    .Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length > 0) ConfirmDeleteFiles(names);
            };
            actions.AddView(delete, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
        }
        var transfer = new Google.Android.Material.Button.MaterialButton(_activity, null, Resource.Attribute.materialButtonStyle)
        {
            Text = "To " + (_phoneHost ? "this phone" : _hostName),
            Enabled = CanSelectRemoteFiles() && _selectedRemoteFiles.Count > 0 && !_busy
                && _safeDownloads() && _site is not null && _path is not null
        };
        transfer.SetAllCaps(false);
        transfer.TextSize = 14;
        transfer.InsetTop = 0;
        transfer.InsetBottom = 0;
        transfer.SetPadding(Motion.Dp(_activity, 18), 0, Motion.Dp(_activity, 18), 0);
        transfer.SetIconResource(Resource.Drawable.ic_remote_download);
        transfer.Click += (_, _) =>
        {
            if (!transfer.Enabled || _site is null || _path is null) return;
            var names = _items.Where(item => !item.IsFolder && _selectedRemoteFiles.Contains(item.Name))
                .Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
            long knownBytes = _items.Where(item => !item.IsFolder && names.Contains(item.Name, StringComparer.Ordinal) && item.Size >= 0)
                .Sum(item => item.Size);
            ConfirmDownloads(names, knownBytes);
        };
        actions.AddView(transfer, new LinearLayout.LayoutParams(-2, Motion.Dp(_activity, 56)));
        _folderFooter.AddView(actions);
    }

    private string SelectionSummary()
    {
        var selected = _items.Where(item => !item.IsFolder && _selectedRemoteFiles.Contains(item.Name)).ToArray();
        long bytes = selected.Where(item => item.Size >= 0).Sum(item => item.Size);
        bool unknown = selected.Any(item => item.Size < 0);
        string total = unknown && bytes == 0 ? "size unknown" : Fmt.Bytes(bytes) + (unknown ? " + unknown" : "");
        return selected.Length + (selected.Length == 1 ? " file" : " files") + " · " + total;
    }

    private void RenderPhoneFiles()
    {
        var localPath = _phonePath ?? "";
        AddFolderMessage(localPath.Length == 0 ? "Downloads on this phone" : "Downloads/" + localPath);
        var snapshot = ReadLocalFiles(localPath);
        if (snapshot.Error is not null) AddFolderMessage(snapshot.Error);
        if (_phoneHost && snapshot.Error is null)
            AddFolderAction("Use this download folder", () =>
            {
                if (!SameHost() || _mutation) return;
                if (ExistingPhoneDownloadFolder(localPath) is null)
                {
                    AddFolderMessage("That folder is no longer available.");
                    return;
                }
                _phoneDestination = localPath;
                SetPhoneTab(false);
            });
        else if (!_phoneHost)
            AddFolderMessage("Files sent from the Site are saved on the selected Host, not on this phone.");
        foreach (var item in snapshot.Items)
        {
            var detail = item.IsFolder ? "Folder · on this phone"
                : item.Size >= 0 ? Fmt.Bytes(item.Size) + " · on this phone" : "Size unknown · on this phone";
            _folderRows!.AddView(CreateLocalRow(item, detail));
        }
        if (snapshot.Items.Count == 0 && snapshot.Error is null)
            AddFolderMessage("No items are visible in Android's scoped Downloads list.");
    }

    private string? _phonePath;

    private void OpenPhoneFolder(string? path)
    {
        _phonePath = path;
        _phoneUploadSource = null;
        RenderFolderC4();
    }

    private View CreateLocalRow(LocalFileEntry item, string detail)
    {
        var card = new Google.Android.Material.Card.MaterialCardView(_activity)
        {
            Radius = Motion.Dp(_activity, 18), CardElevation = 0, StrokeWidth = 0,
            Clickable = item.IsFolder, Focusable = item.IsFolder
        };
        bool selected = !item.IsFolder && CanUseUploadProbe() && IsPhoneUploadSelected(item);
        card.SetCardBackgroundColor(new Color(Motion.Attr(_activity,
            selected ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurfaceContainer)));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Motion.Dp(_activity, 6) };
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Motion.Dp(_activity, 12), Motion.Dp(_activity, 10), Motion.Dp(_activity, 12), Motion.Dp(_activity, 10));
        var iconParams = new LinearLayout.LayoutParams(Motion.Dp(_activity, 44), Motion.Dp(_activity, 44));
        if (selected)
        {
            var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            icon.SetImageResource(Resource.Drawable.ic_check);
            icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnPrimary)));
            var tile = new Android.Graphics.Drawables.GradientDrawable();
            tile.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
            tile.SetColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorPrimary)));
            icon.Background = tile;
            int padding = Motion.Dp(_activity, 11);
            icon.SetPadding(padding, padding, padding, padding);
            row.AddView(icon, iconParams);
        }
        else row.AddView(FolderText(item.IsFolder ? "▱" : FileGlyph(item.Name), 22, Resource.Attribute.colorPrimary), iconParams);
        var labels = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        labels.SetPadding(Motion.Dp(_activity, 12), 0, 0, 0);
        labels.AddView(FolderText(item.Name, 16, Resource.Attribute.colorOnSurface));
        labels.AddView(FolderText(detail, 14, Resource.Attribute.colorOnSurfaceVariant));
        row.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1));
        card.AddView(row);
        if (item.IsFolder)
        {
            card.ContentDescription = item.Name + ", local folder. Open";
            card.Click += (_, _) =>
            {
                if (!SafeSegment(item.Name)) return;
                OpenPhoneFolder(string.IsNullOrEmpty(_phonePath) ? item.Name : _phonePath.TrimEnd('/') + "/" + item.Name);
            };
        }
        if (!item.IsFolder) BindPhoneUploadRow(card, item);
        return card;
    }

    private void SetPhoneTab(bool phone)
    {
        if (_showPhoneFiles == phone || _mutation) return;
        _showPhoneFiles = phone;
        _phonePath = null;
        _phoneUploadSource = null;
        if (phone) _selectedRemoteFiles.Clear();
        else if (_site is not null && _path is not null) _selectedRemoteFiles.Clear();
        RenderFolderC4();
    }

    private void ShowFolderMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        var items = menu.Menu!;
        items.Add("Refresh folder")!.SetOnMenuItemClickListener(new MenuClickListener(() =>
        {
            if (!_showPhoneFiles) _ = BrowseAsync(_path, false, true);
            else RenderFolderC4();
        }));
        items.Add("Transfers")!.SetOnMenuItemClickListener(new MenuClickListener(OpenTransfers));
        menu.Show();
    }

    private void AddFolderMessage(string text)
    {
        var label = FolderText(text, 14, Resource.Attribute.colorOnSurfaceVariant);
        label.SetPadding(0, Motion.Dp(_activity, 8), 0, Motion.Dp(_activity, 8));
        _folderRows!.AddView(label);
    }

    private void AddFolderAction(string text, Action action)
    {
        var button = new Google.Android.Material.Button.MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = text
        };
        button.SetAllCaps(false);
        button.Click += (_, _) => action();
        _folderRows!.AddView(button);
    }

    private TextView FolderText(string text, float size, int color)
    {
        var view = new TextView(_activity) { Text = text, TextSize = size };
        view.SetTextColor(new Color(Motion.Attr(_activity, color)));
        return view;
    }

    private static string FileGlyph(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp3" or ".flac" or ".wav" or ".m4a" => "♫",
        ".pdf" or ".txt" or ".doc" or ".docx" => "▤",
        ".zip" or ".7z" or ".rar" => "▣",
        _ => "▧"
    };

    private static bool SafeRelativeFolder(string path) =>
        !Path.IsPathRooted(path) && !path.Contains('\\')
        && path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);

    private static bool SafeSegment(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0');

    private static bool TryDownloadsRelativePath(string mediaPath, out string relative)
    {
        relative = "";
        var normalized = mediaPath.Replace('\\', '/').Trim('/');
        const string root = "Download";
        if (!normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        relative = normalized.Length == root.Length ? "" : normalized[(root.Length + 1)..];
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries).All(SafeSegment);
    }

    private sealed class MenuClickListener(Action click) : Java.Lang.Object, IMenuItemOnMenuItemClickListener
    {
        public bool OnMenuItemClick(Android.Views.IMenuItem? item)
        {
            click();
            return true;
        }
    }
}
