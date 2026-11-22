using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Inbound.App.Host;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Inbound.App.Screens;

/// <summary>Sheets, dialogs and transfers of the folder view: the pane picker (r2), the file options (r3) and what they do.</summary>
internal sealed partial class RemoteFilesScreen
{
    private BottomSheetDialog? _sheet;

    private BottomSheetDialog NewSheet(out LinearLayout content)
    {
        _sheet?.Dismiss();
        var dialog = new AnimatedSheetDialog(_activity, .55f);
        var scroll = new ScrollView(_activity) { FillViewport = false };
        content = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        background.SetCornerRadii(new float[] { Dp(28), Dp(28), Dp(28), Dp(28), 0, 0, 0, 0 });
        content.Background = background;
        content.SetPadding(0, Dp(10), 0, Dp(24));
        var handle = new View(_activity);
        handle.SetBackgroundResource(Resource.Drawable.bg_handle);
        content.AddView(handle, new LinearLayout.LayoutParams(Dp(32), Dp(4)) { Gravity = GravityFlags.CenterHorizontal, TopMargin = Dp(4), BottomMargin = Dp(14) });
        scroll.AddView(content);
        dialog.SetContentView(scroll);
        _sheet = dialog;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_sheet, dialog)) _sheet = null; };
        return dialog;
    }

    private static void ShowSheet(BottomSheetDialog dialog)
    {
        dialog.Show();
        dialog.Behavior.SkipCollapsed = true;
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
    }

    private ImageView SheetIcon(int drawable, int tintAttr)
    {
        var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(drawable);
        icon.ImageTintList = ColorStateList.ValueOf(new Color(Attr(tintAttr)));
        return icon;
    }

    private LinearLayout SheetRow(LinearLayout parent, int drawable, string title, string? subtitle, Action click, bool danger = false, bool accent = false)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(24), 0, Dp(24), 0);
        row.Background = new TypedValueHolder(_activity).SelectableBackground();
        row.AddView(SheetIcon(drawable, danger ? Resource.Attribute.colorError : accent ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurface),
            new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(16) });
        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        texts.AddView(FolderText(title, 16, danger ? Resource.Attribute.colorError : Resource.Attribute.colorOnSurface));
        if (subtitle is not null)
        {
            var sub = FolderText(subtitle, 13, Resource.Attribute.colorOnSurfaceVariant);
            sub.SetSingleLine(true);
            sub.Ellipsize = TextUtils.TruncateAt.End;
            texts.AddView(sub);
        }
        row.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        row.ContentDescription = subtitle is null ? title : title + ", " + subtitle;
        // Clear _sheet now: its dismiss event only fires after the close animation, and click() may start work guarded by it.
        row.Click += (_, _) => { var open = _sheet; _sheet = null; open?.Dismiss(); click(); };
        parent.AddView(row, new LinearLayout.LayoutParams(-1, Dp(56)));
        return row;
    }

    private void SheetDivider(LinearLayout parent)
    {
        var line = new View(_activity);
        line.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorOutlineVariant)));
        parent.AddView(line, new LinearLayout.LayoutParams(-1, Dp(1)) { LeftMargin = Dp(24), RightMargin = Dp(24), TopMargin = Dp(8), BottomMargin = Dp(8) });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // r2: what this pane shows

    private static int LocationGlyph(string kind) => kind.ToLowerInvariant() switch
    {
        SiteKindCatalog.Sftp or SiteKindCatalog.Ftp => Resource.Drawable.ic_remote_server,
        SiteKindCatalog.WebDav or "http" => Resource.Drawable.ic_remote_globe,
        SiteKindCatalog.Drive or SiteKindCatalog.OneDrive or SiteKindCatalog.Dropbox => Resource.Drawable.ic_remote_cloud,
        _ => Resource.Drawable.ic_remote_box,
    };

    private View LocationTile(int drawable)
    {
        var tile = new FrameLayout(_activity);
        var disc = new GradientDrawable();
        disc.SetCornerRadius(Dp(20));
        disc.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHighest)));
        tile.Background = disc;
        var icon = SheetIcon(drawable, Resource.Attribute.colorOnSurface);
        icon.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        tile.AddView(icon, new FrameLayout.LayoutParams(-1, -1));
        return tile;
    }

    private void ShowSwitcher()
    {
        if (!SameHost() || _sheet is not null || _mutation) return;
        int index = _active;
        var pane = _panes[index];
        var other = _panes[1 - index];
        var dialog = NewSheet(out var content);
        var heading = FolderText("Show in this pane", 14, Resource.Attribute.colorOnSurfaceVariant);
        heading.SetPadding(Dp(24), Dp(6), Dp(24), Dp(10));
        content.AddView(heading);

        void Location(SiteDto? site)
        {
            bool current = pane.Site?.Id == site?.Id;
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(current ? 12 : 0), 0, Dp(current ? 12 : 0), 0);
            if (current)
            {
                var highlight = new GradientDrawable();
                highlight.SetCornerRadius(Dp(16));
                highlight.SetColor(new Color(Attr(Resource.Attribute.colorSecondaryContainer)));
                row.Background = highlight;
            }
            else row.Background = new TypedValueHolder(_activity).SelectableBackground();
            string name = site is null ? "This phone" : RemoteLocationText.DisplayName(site);
            bool inOther = site is null ? other.IsPhone : other.Site?.Id == site.Id;
            string subtitle = (site is null ? PhoneFreeText() : LocationDescription(site, withFree: false)) + (inOther ? " · in the other pane" : "");
            var tile = LocationTile(site is null ? Resource.Drawable.ic_remote_phone : LocationGlyph(site.Kind));
            row.AddView(tile, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { LeftMargin = Dp(current ? 0 : 24), RightMargin = Dp(16) });
            var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            texts.AddView(FolderText(name, 16, Resource.Attribute.colorOnSurface));
            var sub = FolderText(subtitle, 13, Resource.Attribute.colorOnSurfaceVariant);
            sub.SetSingleLine(true);
            sub.Ellipsize = TextUtils.TruncateAt.End;
            texts.AddView(sub);
            row.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
            if (current) row.AddView(SheetIcon(Resource.Drawable.ic_remote_check, Resource.Attribute.colorPrimary), new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(4) });
            else if (site is not null && NeedsCloudSignIn(site))
            {
                var pill = new MaterialButton(_activity, null, Resource.Attribute.materialButtonTonalStyle) { Text = "Sign in" };
                pill.SetAllCaps(false);
                pill.Click += (_, _) => { dialog.Dismiss(); ChoosePaneLocation(index, site); };
                row.AddView(pill, new LinearLayout.LayoutParams(-2, Dp(40)) { RightMargin = Dp(24) });
            }
            row.ContentDescription = name + ", " + subtitle + (current ? ", shown now" : "");
            row.Click += (_, _) =>
            {
                dialog.Dismiss();
                if (current) return;
                if (inOther) SwapPanesKeepingActive(index);
                else ChoosePaneLocation(index, site);
            };
            content.AddView(row, new LinearLayout.LayoutParams(-1, Dp(current ? 60 : 56)) { LeftMargin = Dp(current ? 12 : 0), RightMargin = Dp(current ? 12 : 0) });
        }

        Location(null);
        foreach (var site in _sites) Location(site);
        SheetDivider(content);
        SheetRow(content, Resource.Drawable.ic_remote_swap, "Swap panes", null, SwapPanes, accent: true);
        SheetRow(content, Resource.Drawable.ic_remote_plus, "Add site", null, () =>
        {
            if (!SameHost()) return;
            foreach (var each in _panes) { each.Request?.Cancel(); each.Counts?.Cancel(); each.Selected.Clear(); }
            _inFolder = false;
            _adding = true;
            _error = null;
            Render();
        }, accent: true);
        ShowSheet(dialog);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // r3: a file's options

    private string? OnPhoneText(Pane pane, SiteItemDto item)
    {
        if (pane.IsPhone) return "On this phone";
        var other = pane == _panes[0] ? _panes[1] : _panes[0];
        if (!other.IsPhone || !_phoneHost || RemoteTransfers.DownloadRelative(other.Path) is null) return null;
        return other.Rows.Any(row => !row.IsFolder && row.Name == item.Name) ? "On this phone" : "Not on this phone";
    }

    private void ShowFileSheet(Pane pane, SiteItemDto item)
    {
        if (!SameHost() || _sheet is not null || !SafeName(item.Name)) return;
        var other = pane == _panes[0] ? _panes[1] : _panes[0];
        var dialog = NewSheet(out var content);
        var head = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetPadding(Dp(24), Dp(4), Dp(24), Dp(16));
        var kind = FileKinds.Of(item.Name);
        var fileIcon = FileIcons.For(kind);
        var iconTile = LocationTile(kind switch
        {
            FileKind.Audio => Resource.Drawable.ic_remote_audio,
            FileKind.Document => Resource.Drawable.ic_remote_document,
            FileKind.Archive => Resource.Drawable.ic_remote_archive,
            FileKind.Image => Resource.Drawable.ic_remote_image,
            FileKind.Video => Resource.Drawable.ic_remote_video,
            _ => fileIcon.Drawable
        });
        if (((FrameLayout)iconTile).GetChildAt(0) is ImageView glyph)
            glyph.ImageTintList = ColorStateList.ValueOf(new Color(RemoteIconColor(_activity, kind, fileIcon)));
        ((GradientDrawable)iconTile.Background!).SetCornerRadius(Dp(11));
        head.AddView(iconTile, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(16) });
        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var name = FolderText(item.Name, 16, Resource.Attribute.colorOnSurface);
        name.SetSingleLine(true);
        name.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(name);
        var parts = new List<string> { item.Size >= 0 ? RemoteFileList.Size(item.Size) : "Size unknown" };
        if (item.Modified is { } modified) parts.Add(modified.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (OnPhoneText(pane, item) is { } where) parts.Add(where);
        var sub = FolderText(string.Join(" · ", parts), 13, Resource.Attribute.colorOnSurfaceVariant);
        sub.SetSingleLine(true);
        sub.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(sub);
        head.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        content.AddView(head);

        bool hadSelection = pane.Selected.Contains(item.Name);
        pane.Selected.Add(item.Name);
        var plan = PlanFor(pane, other);
        if (!hadSelection) pane.Selected.Remove(item.Name);
        bool phoneFile = pane.IsPhone || _phoneHost;
        if (plan.Enabled)
        {
            string caption = plan.Caption.StartsWith("To ", StringComparison.Ordinal) ? plan.Caption[3..] : plan.Caption;
            SheetRow(content, plan.Icon, plan.Button, caption, () => _ = StartTransferAsync(pane, other, [item]), accent: true);
        }
        if (phoneFile)
        {
            SheetRow(content, Resource.Drawable.ic_remote_open, "Open", pane.IsPhone ? "Opens in a phone app" : "Downloads first, then opens in a phone app",
                () => _ = OpenOrShareAsync(pane, item, share: false));
            SheetRow(content, Resource.Drawable.ic_remote_share, "Share", null, () => _ = OpenOrShareAsync(pane, item, share: true));
        }
        SheetRow(content, Resource.Drawable.ic_remote_check, "Select", null, () => { pane.Selected.Add(item.Name); UpdateFolderUi(); });
        if (CanChange(pane))
            SheetRow(content, Resource.Drawable.ic_remote_edit, "Rename", null, () => ShowRenameDialog(pane, item));
        SheetRow(content, Resource.Drawable.ic_remote_info, "Details", null, () => ShowDetails(pane, item));
        if (CanDeleteSelection(pane))
        {
            SheetDivider(content);
            SheetRow(content, Resource.Drawable.ic_remote_trash, "Delete from " + PaneName(pane), null, () => _ = ConfirmDeleteAsync(pane, [item]), danger: true);
        }
        ShowSheet(dialog);
    }

    private bool CanChange(Pane pane) => pane.IsPhone
        ? RemoteTransfers.DownloadRelative(pane.Path) is not null
        : SameHost() && _secureHostConnection && pane.Error is null;

    private void ShowDetails(Pane pane, SiteItemDto item)
    {
        if (_confirm is not null) return;
        string extension = System.IO.Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        var lines = new List<string> { item.IsFolder ? "Type: Folder" : "Type: " + (extension.Length > 0 ? extension + " file" : "File") };
        if (item.IsFolder) { if (item.ChildCount is int count) lines.Add($"Contains: {count:N0} {(count == 1 ? "item" : "items")}"); }
        else lines.Add("Size: " + (item.Size >= 0 ? Fmt.Bytes(item.Size) + $" ({item.Size:N0} bytes)" : "not reported"));
        if (item.Modified is { } modified) lines.Add("Modified: " + modified.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        lines.Add("Location: " + (pane.IsPhone ? pane.Path.Length == 0 ? "Phone" : "Phone/" + pane.Path : RemoteFileList.Destination(PaneName(pane), pane.Path)));
        if (OnPhoneText(pane, item) is { } where) lines.Add(where);
        _confirm = new MaterialAlertDialogBuilder(_activity).SetTitle(item.Name)!.SetMessage(string.Join("\n", lines))!
            .SetPositiveButton("Close", (Android.Content.IDialogInterfaceOnClickListener?)null)!.Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rename, new folder, delete

    private EditText NameField(string hint, string text)
    {
        var field = new EditText(_activity) { Hint = hint, Text = text, SaveEnabled = false };
        field.SetSingleLine(true);
        field.InputType = InputTypes.ClassText;
        field.ContentDescription = hint;
        field.SetSelectAllOnFocus(true);
        return field;
    }

    private void ShowNameDialog(string title, string positive, EditText field, Func<string, Task> accept)
    {
        if (_confirm is not null) return;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetPadding(Dp(20), Dp(8), Dp(20), 0);
        var problem = FolderText("", 14, Resource.Attribute.colorError);
        body.AddView(field);
        body.AddView(problem);
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle(title)!.SetView(body)!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton(positive, (Android.Content.IDialogInterfaceOnClickListener?)null)!.Create();
        _confirm = dialog;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
        dialog.ShowEvent += (_, _) =>
        {
            field.RequestFocus();
            dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
            {
                var typed = (field.Text ?? "").Trim();
                if (!SafeName(typed)) { problem.Text = "Enter a name without slashes."; return; }
                dialog.Dismiss();
                _ = accept(typed);
            };
        };
        dialog.Show();
    }

    private void ShowNewFolderDialog(Pane pane) =>
        ShowNameDialog("New folder", "Create", NameField("Folder name", ""), name => MakeFolderAsync(pane, name));

    private void ShowRenameDialog(Pane pane, SiteItemDto item) =>
        ShowNameDialog("Rename", "Rename", NameField("New name", item.Name), name => RenameItemAsync(pane, item, name));

    private async Task MakeFolderAsync(Pane pane, string name)
    {
        string message = "Folder created.";
        try
        {
            if (pane.IsPhone)
            {
                string? parent = ExistingPhoneDownloadFolder(RemoteTransfers.DownloadRelative(pane.Path) ?? "\0");
                if (parent is null) message = "That folder is no longer available.";
                else Directory.CreateDirectory(System.IO.Path.Combine(parent, name));
            }
            else
            {
                var result = await _client.MakeFolderAsync(new SiteMakeFolderRequest { Id = pane.Site!.Id, Path = pane.Path, Name = name }, _life.Token);
                if (!SameHost()) return;
                if (!result.Ok) message = result.Message ?? "The Host did not create the folder.";
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { message = "Could not create the folder."; }
        Toast.MakeText(_activity, message, ToastLength.Short)?.Show();
        if (SameHost()) await BrowsePaneAsync(pane, pane.Path, true);
    }

    private async Task RenameItemAsync(Pane pane, SiteItemDto item, string newName)
    {
        if (newName == item.Name) return;
        string message = "Renamed.";
        try
        {
            if (pane.IsPhone)
            {
                string? parent = ExistingPhoneDownloadFolder(RemoteTransfers.DownloadRelative(pane.Path) ?? "\0");
                string from = parent is null ? "" : System.IO.Path.Combine(parent, item.Name), to = parent is null ? "" : System.IO.Path.Combine(parent, newName);
                if (parent is null) message = "That folder is no longer available.";
                else if (File.Exists(to) || Directory.Exists(to)) message = "Something with that name is already there.";
                else if (item.IsFolder) Directory.Move(from, to);
                else File.Move(from, to);
            }
            else
            {
                var result = await _client.RenameAsync(new SiteRenameRequest { Id = pane.Site!.Id, Path = pane.Path, Name = item.Name, NewName = newName }, _life.Token);
                if (!SameHost()) return;
                if (!result.Ok) message = result.Message ?? "The Host did not rename it.";
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { message = "Android did not allow renaming this item."; }
        Toast.MakeText(_activity, message, ToastLength.Short)?.Show();
        if (SameHost()) await BrowsePaneAsync(pane, pane.Path, true);
    }

    private void ConfirmDeleteSelection(Pane pane) => _ = ConfirmDeleteAsync(pane, SelectedItems(pane));

    private async Task ConfirmDeleteAsync(Pane pane, IReadOnlyList<SiteItemDto> items)
    {
        if (!SameHost() || _confirm is not null || items.Count == 0 || items.Any(item => !SafeName(item.Name)) || !CanDeleteSelection(pane)) return;
        var names = items.Select(item => item.Name).Distinct(StringComparer.Ordinal).ToArray();
        string place = PaneName(pane) + (pane.IsPhone ? "" : " · " + pane.Path);
        // The folder the question is about, taken before any wait (audit RF-2): the pane may move meanwhile, and the delete
        // must go to this folder or nowhere. ViewEpoch, not Generation: a quiet refresh of the same folder keeps it.
        string? siteId = pane.Site?.Id, path = pane.Path;
        int epoch = pane.ViewEpoch;
        bool SameFolder() => pane.Site?.Id == siteId && pane.Path == path && pane.ViewEpoch == epoch;
        string detail = "";
        if (!pane.IsPhone && items.Any(item => item.IsFolder))
        {
            try
            {
                var count = await _client.CountAsync(new SiteChangeRequest
                {
                    Id = pane.Site!.Id, Path = pane.Path, Items = names.Select(n => new SiteChangeItemDto { Name = n, IsFolder = items.First(i => i.Name == n).IsFolder }).ToList()
                }, _life.Token);
                if (!SameHost() || _confirm is not null || !SameFolder()) return;
                if (count.Ok) detail = $"\n\nThat is {count.Files:N0} {(count.Files == 1 ? "file" : "files")}" + (count.SizeUnknown ? "." : " (" + Fmt.Bytes(count.Bytes) + ").");
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
            catch (Exception) { /* The question is still asked, without the counts. */ }
        }
        if (!SameFolder()) return;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle(names.Length == 1 ? $"Delete {names[0]}?" : $"Delete {names.Length} items?")!
            .SetMessage($"Permanently delete the selected {(names.Length == 1 ? "item" : "items")} from {place}?{detail}\n\nThis cannot be undone."
                + (pane.IsPhone ? "" : "\nCopies on this phone are not affected."))!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Delete", (_, _) =>
            {
                if (SameFolder() && CanDeleteSelection(pane)) _ = DeleteItemsAsync(pane, items);
                else Toast.MakeText(_activity, "The folder changed, so nothing was deleted.", ToastLength.Long)?.Show();
            })!
            .Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task DeleteItemsAsync(Pane pane, IReadOnlyList<SiteItemDto> items)
    {
        _mutation = true;
        UpdateFolderUi();
        string message;
        try
        {
            if (pane.IsPhone)
            {
                int failed = 0;
                string? parent = ExistingPhoneDownloadFolder(RemoteTransfers.DownloadRelative(pane.Path) ?? "\0");
                foreach (var item in items)
                {
                    try
                    {
                        string full = parent is null ? "" : System.IO.Path.Combine(parent, item.Name);
                        if (parent is null) failed++;
                        else if (item.IsFolder) Directory.Delete(full, true);
                        else if (!TryDeletePhoneFile(full, pane.PhoneUris.GetValueOrDefault(item.Name))) failed++;
                    }
                    catch (Exception) { failed++; }
                }
                message = failed == 0 ? "Deleted." : $"{failed} of {items.Count} could not be deleted.";
            }
            else
            {
                var request = new SiteChangeRequest
                {
                    Id = pane.Site!.Id, Path = pane.Path, Listing = pane.Listing,
                    Items = items.Select(item => new SiteChangeItemDto { Name = item.Name, IsFolder = item.IsFolder }).ToList()
                };
                bool filesOnly = items.All(item => !item.IsFolder) && !string.IsNullOrEmpty(pane.Listing);
                var result = filesOnly ? await _client.DeleteFilesAsync(request, _life.Token) : await _client.DeleteAsync(request, _life.Token);
                if (!SameHost()) return;
                message = result.Ok ? "Deleted." : result.Message ?? "The Host did not delete every selected item.";
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { message = "The Host's answer was lost. Check the refreshed list before trying again."; }
        finally { _mutation = false; }
        Toast.MakeText(_activity, message, ToastLength.Long)?.Show();
        if (SameHost()) await BrowsePaneAsync(pane, pane.Path, true);
    }

    private bool TryDeletePhoneFile(string path, Android.Net.Uri? uri)
    {
        try { if (File.Exists(path)) { File.Delete(path); return !File.Exists(path); } }
        catch (Exception) { /* Fall back to MediaStore below. */ }
        try { return uri is not null && _activity.ContentResolver?.Delete(uri, null, null) > 0; }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Copying between the panes

    private Task StartSelectionTransferAsync(Pane source, Pane target) => StartTransferAsync(source, target, SelectedItems(source));

    /// <summary>
    /// Asks only when a selected name is already in the target folder as this pane shows it (no extra request), once per
    /// batch. A clash made after the pane loaded is left to the Host's handling of the chosen conflict.
    /// </summary>
    private Task StartTransferAsync(Pane source, Pane target, IReadOnlyList<SiteItemDto> items)
    {
        if (!SameHost() || _mutation || _sheet is not null || items.Count is 0 or > 100 || items.Any(item => !SafeName(item.Name))) return Task.CompletedTask;
        var mode = RemoteTransfers.Mode(source.IsPhone, target.IsPhone);
        var listing = TargetListing(mode, target);
        var clashing = RemoteConflicts.Clashes(items, listing);
        // A listing that is only partly loaded cannot rule a clash out, so ask unless the rows seen already say it.
        if (clashing.Count == 0 && listing is not null && target.Items.Count < target.Total)
        {
            ShowConflictSheet($"{(items.Count == 1 ? items[0].Name : items.Count + " items")} may already exist in {TargetPlace(target)}",
                conflict => _ = StartTransferCoreAsync(source, target, items, conflict));
            return Task.CompletedTask;
        }
        if (clashing.Count == 0) return StartTransferCoreAsync(source, target, items, RemoteConflicts.Skip);
        ShowConflictSheet(RemoteConflicts.Title(clashing, TargetPlace(target)), conflict => _ = StartTransferCoreAsync(source, target, items, conflict));
        return Task.CompletedTask;
    }

    /// <summary>The target's loaded rows, or null when no pane lists the destination folder (then nothing is checked).</summary>
    private IReadOnlyList<SiteItemDto>? TargetListing(RemoteTransferMode mode, Pane target)
    {
        if (target.Loading || target.Error is not null || target.NeedsSignIn || target.KeyProblem) return null;
        return mode switch
        {
            RemoteTransferMode.Upload or RemoteTransferMode.LocationToLocation => target.Rows,
            RemoteTransferMode.Download when _phoneHost && target.IsPhone && RemoteTransfers.DownloadRelative(target.Path) is not null => target.Rows,
            _ => null,
        };
    }

    private string TargetPlace(Pane target)
    {
        if (target.IsPhone) return RemoteFileList.Destination("This phone", target.Path);
        string root = PaneRoot(target), path = RemoteCrumbs.Normalize(target.Path);
        string relative = root != "/" && path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
        return RemoteFileList.Destination(PaneName(target), relative);
    }

    private void ShowConflictSheet(string title, Action<string> choose)
    {
        var dialog = NewSheet(out var content);
        var heading = FolderText(title, 16, Resource.Attribute.colorOnSurface);
        heading.SetPadding(Dp(24), Dp(6), Dp(24), Dp(12));
        content.AddView(heading);
        SheetRow(content, Resource.Drawable.ic_remote_arrow, "Skip", "Leave the existing ones as they are", () => choose(RemoteConflicts.Skip));
        SheetRow(content, Resource.Drawable.ic_remote_transfer, "Replace", "Overwrite the existing ones", () => choose(RemoteConflicts.Replace));
        SheetRow(content, Resource.Drawable.ic_remote_plus, "Keep both", "Save the new ones under a different name", () => choose(RemoteConflicts.KeepBoth));
        SheetDivider(content);
        SheetRow(content, Resource.Drawable.ic_remote_caret, "Cancel", null, () => { });
        ShowSheet(dialog);
    }

    private async Task StartTransferCoreAsync(Pane source, Pane target, IReadOnlyList<SiteItemDto> items, string conflict)
    {
        if (!SameHost() || _mutation || items.Count is 0 or > 100 || items.Any(item => !SafeName(item.Name))) return;
        var mode = RemoteTransfers.Mode(source.IsPhone, target.IsPhone);
        if (mode is not (RemoteTransferMode.Download or RemoteTransferMode.Upload or RemoteTransferMode.LocationToLocation)) return;
        if (mode == RemoteTransferMode.LocationToLocation && !_remoteCopy()) return;
        _mutation = true;
        UpdateFolderUi();
        int batches = 0;
        string? failure = null;
        // Downloads to this phone that the activity watches, to say "<file> is on this phone" on whichever tab is open (x8, x33).
        var tracked = new List<(string Id, string Name)>();
        try
        {
            string phoneRoot = AndroidHost.SharedDownloadFolder;
            if (mode == RemoteTransferMode.LocationToLocation)
            {
                // r6: one batch, straight from one location to the other through the Host; nothing comes to this phone.
                var result = await _client.StartCopyAsync(source.Site!.Id, source.Path, items, target.Site!.Id, target.Path, _life.Token, conflict);
                if (!SameHost()) return;
                if (!result.Ok || result.Transfer is null) failure = result.Message ?? "The Host did not start this transfer.";
                else batches++;
            }
            else if (mode == RemoteTransferMode.Download)
            {
                string folder = _phoneHost ? System.IO.Path.Combine(phoneRoot, RemoteTransfers.DownloadRelative(target.Path) ?? "") : source.Site!.LocalFolder ?? "";
                foreach (var file in items.Where(item => !item.IsFolder))
                {
                    var result = await _client.StartTransferAsync(new SiteTransferStartRequest
                    {
                        SiteId = source.Site!.Id, Direction = "download", RemoteFolder = source.Path, LocalFolder = folder, SafeSingleDownload = conflict == RemoteConflicts.Skip,
                        Conflict = conflict, Names = [new SiteTransferNameDto { Name = file.Name, IsFolder = false }]
                    }, _life.Token);
                    if (!SameHost()) return;
                    if (!result.Ok || result.Transfer is null) { failure = result.Message ?? "The Host did not start this transfer."; break; }
                    batches++;
                    if (_phoneHost) tracked.Add((result.Transfer.Id, file.Name));
                }
                var folders = items.Where(item => item.IsFolder).ToList();
                if (failure is null && folders.Count > 0)
                {
                    var result = await _client.StartTransferAsync(new SiteTransferStartRequest
                    {
                        SiteId = source.Site!.Id, Direction = "download", RemoteFolder = source.Path, LocalFolder = folder, Conflict = conflict,
                        Names = folders.Select(item => new SiteTransferNameDto { Name = item.Name, IsFolder = true }).ToList()
                    }, _life.Token);
                    if (!SameHost()) return;
                    if (!result.Ok || result.Transfer is null) failure = result.Message ?? "The Host did not start this transfer.";
                    else
                    {
                        batches++;
                        if (_phoneHost) tracked.Add((result.Transfer.Id, folders.Count == 1 ? folders[0].Name : folders.Count + " folders"));
                    }
                }
            }
            else
            {
                var result = await _client.StartTransferAsync(new SiteTransferStartRequest
                {
                    SiteId = target.Site!.Id, Direction = "upload", RemoteFolder = target.Path,
                    LocalFolder = System.IO.Path.Combine(phoneRoot, RemoteTransfers.DownloadRelative(source.Path) ?? ""), Conflict = conflict,
                    Names = items.Select(item => new SiteTransferNameDto { Name = item.Name, IsFolder = item.IsFolder }).ToList()
                }, _life.Token);
                if (!SameHost()) return;
                if (!result.Ok || result.Transfer is null) failure = result.Message ?? "The Host did not start this transfer.";
                else batches++;
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { failure = "The Host's answer was lost. Check Transfers before trying again."; }
        finally { _mutation = false; }
        if (batches > 0) source.Selected.Clear();
        UpdateFolderUi();
        // x7: "2 files going to Download/Inbound/Music" with View transfers.
        string place = mode == RemoteTransferMode.Download
            ? (_phoneHost ? RemoteTransfers.PhoneLabel(target.Path) : _hostName + " in " + (source.Site?.LocalFolder ?? "its download folder")) : TargetPlace(target);
        string what = items.Count == 1 ? items[0].Name : items.All(item => !item.IsFolder) ? items.Count + " files" : items.Count + " items";
        string message = failure is null ? what + " going to " + place
            : batches > 0 ? $"{batches} started. {failure}" : failure;
        if (batches > 0) _activity.ShowMessage(message, "View transfers", () => OpenTransfers());
        else _activity.ShowMessage(message);
        if (_phoneHost) foreach (var (id, name) in tracked) _activity.TrackPhoneDownload(id, name, RemoteTransfers.DownloadRelative(target.Path) is { } rel ? "Download" + (rel.Length == 0 ? "" : "/" + rel) : "Download");
        if (batches > 0) _ = RefreshTransfersQuietAsync();
    }

    private async Task RefreshTransfersQuietAsync()
    {
        try
        {
            var result = await _client.ListTransfersAsync(_life.Token);
            if (!SameHost()) return;
            _transfers = result.Transfers;
            _transfersLoaded = true;
            int active = _transfers.Count(t => t.State is "queued" or "running" or "paused");
            // A transfer that just finished changes a folder on the other side; show it without moving the user.
            if (_inFolder && !_showTransfers && !_adding)
            {
                bool finished = active < _lastActiveTransfers;
                // The phone's Host adds finished files to Android's media index a moment later, and This phone lists from
                // that index, so phone panes are read again on the next few ticks too (Fable, 2026-11-22).
                // Set to 4: the finish tick reads every pane, then three more ticks read phone panes on the phone's own Host
                // (a paired Host doesn't index the phone).
                if (finished) _phoneSettleTicks = _phoneHost ? 4 : 0;
                else if (_phoneSettleTicks > 0) _phoneSettleTicks--;
                if (finished || _phoneSettleTicks > 0)
                    foreach (var pane in _panes)
                        if ((finished || pane.IsPhone) && !pane.Loading && pane.Selected.Count == 0 && pane.Error is null)
                            _ = BrowsePaneAsync(pane, pane.Path, true, keepView: true);
            }
            // The badge of the transfers button follows the count.
            if (active != _lastActiveTransfers && _inFolder && !_showTransfers && !_adding) UpdateFolderUi();
            _lastActiveTransfers = active;
        }
        catch (Exception) { /* A missing count must not disturb the folder. */ }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Open and Share

    private async Task OpenOrShareAsync(Pane pane, SiteItemDto item, bool share)
    {
        if (!SameHost()) return;
        Android.Net.Uri? uri = null;
        if (pane.IsPhone) uri = pane.PhoneUris.GetValueOrDefault(item.Name);
        else if (_phoneHost && pane.Site is { } site)
        {
            var target = pane == _panes[0] ? _panes[1] : _panes[0];
            string relative = target.IsPhone ? RemoteTransfers.DownloadRelative(target.Path) ?? "" : "";
            string folder = System.IO.Path.Combine(AndroidHost.SharedDownloadFolder, relative);
            Toast.MakeText(_activity, "Downloading " + item.Name + "…", ToastLength.Short)?.Show();
            try
            {
                var started = await _client.StartTransferAsync(new SiteTransferStartRequest
                {
                    SiteId = site.Id, Direction = "download", RemoteFolder = pane.Path, LocalFolder = folder, SafeSingleDownload = true,
                    Conflict = "skip", Names = [new SiteTransferNameDto { Name = item.Name }]
                }, _life.Token);
                if (!started.Ok || started.Transfer is null) { Toast.MakeText(_activity, started.Message ?? "The Host did not start the download.", ToastLength.Long)?.Show(); return; }
                var deadline = DateTime.UtcNow.AddMinutes(30);
                while (SameHost() && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), _life.Token);
                    var state = (await _client.GetTransferAsync(started.Transfer.Id, _life.Token)).Transfer;
                    if (state is null) return;
                    if (state.State == "done" || state.FilesSkipped > 0 && state.FilesRunning == 0 && state.State != "running") break;
                    if (state.State is "failed" or "canceled")
                    {
                        Toast.MakeText(_activity, state.Message ?? "The download did not finish.", ToastLength.Long)?.Show();
                        return;
                    }
                }
                if (!SameHost()) return;
                uri = FindPhoneDownload(item.Name, relative);
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
            catch (Exception) { Toast.MakeText(_activity, "Could not download " + item.Name + ".", ToastLength.Long)?.Show(); return; }
        }
        if (uri is null) { Toast.MakeText(_activity, "Android does not list " + item.Name + " yet. Try again in a moment.", ToastLength.Long)?.Show(); return; }
        try
        {
            var ext = System.IO.Path.GetExtension(item.Name).TrimStart('.').ToLowerInvariant();
            var mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) ?? "*/*";
            Intent intent;
            if (share)
            {
                intent = new Intent(Intent.ActionSend);
                intent.SetType(mime);
                intent.PutExtra(Intent.ExtraStream, uri);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                intent = Intent.CreateChooser(intent, "Share " + item.Name)!;
            }
            else
            {
                intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mime);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            }
            _activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException) { Toast.MakeText(_activity, "No app on this phone can open " + item.Name + ".", ToastLength.Long)?.Show(); }
    }

    private Android.Net.Uri? FindPhoneDownload(string name, string relative)
    {
        string path = "Download/" + (relative.Length == 0 ? "" : relative.Trim('/') + "/");
        using var cursor = _activity.ContentResolver?.Query(MediaStore.Downloads.ExternalContentUri!, ["_id"],
            MediaStore.IMediaColumns.DisplayName + "=? AND " + MediaStore.IMediaColumns.RelativePath + "=?", [name, path], null);
        return cursor is not null && cursor.MoveToFirst()
            ? Android.Content.ContentUris.WithAppendedId(MediaStore.Downloads.ExternalContentUri!, cursor.GetLong(0)) : null;
    }
}
