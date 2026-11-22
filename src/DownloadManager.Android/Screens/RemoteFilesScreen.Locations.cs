using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Android.Text;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>The saved-location overview (x11 to x17) and the dialogs and sheets that belong to it.</summary>
internal sealed partial class RemoteFilesScreen
{
    private string _locationSearch = "";
    private bool _locationSearchOpen;
    private readonly List<(SiteDto Site, View Card)> _locationCards = [];
    private readonly Dictionary<string, TextView> _locationSubtitles = new(StringComparer.Ordinal);
    private TextView? _locationNoMatches;
    private TextView? _locationTransferText;
    private MaterialCardView? _locationTransferCard;
    private ImageView? _locationTransferIcon, _locationTransferChevron;

    private string HostPhrase => _phoneHost ? "this phone" : _hostName;

    private void RenderLocations()
    {
        _locationCards.Clear();
        _locationSubtitles.Clear();
        _locationNoMatches = null;
        _locationTransferText = null;
        _locationTransferCard = null;

        var titleRow = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        titleRow.SetGravity(GravityFlags.CenterVertical);
        var title = Label("Remote Files", 32, Resource.Attribute.colorOnSurface);
        titleRow.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        bool listed = _listLoaded && _status?.Enabled == true;
        if (listed)
        {
            var search = new ImageButton(_activity) { Background = null, ContentDescription = "Search remote locations" };
            search.SetImageResource(Resource.Drawable.ic_search);
            search.ImageTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
            search.Click += (_, _) =>
            {
                _locationSearchOpen = !_locationSearchOpen;
                if (!_locationSearchOpen) _locationSearch = "";
                Render();
            };
            titleRow.AddView(search, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
        }
        var overflow = new ImageButton(_activity) { Background = null, ContentDescription = "Remote Files options" };
        overflow.SetImageResource(Resource.Drawable.ic_more);
        overflow.ImageTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        overflow.Click += (_, _) => ShowLocationsMenu(overflow);
        titleRow.AddView(overflow, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
        titleRow.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Motion.Dp(_activity, 12) };
        _content.AddView(titleRow);
        if (!_phoneHost)
        {
            var host = Label("On " + _hostName, 14, Resource.Attribute.colorOnSurfaceVariant);
            host.SetPadding(0, 0, 0, Motion.Dp(_activity, 8));
            _content.AddView(host);
        }

        if (_busy && !_listLoaded)
        {
            var spinner = new ProgressBar(_activity) { Indeterminate = true, ContentDescription = "Loading Remote Files" };
            _content.AddView(spinner, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48))
            { Gravity = GravityFlags.CenterHorizontal, TopMargin = Motion.Dp(_activity, 96) });
            return;
        }
        if (_error is not null && !_listLoaded)
        {
            StateBlock(Resource.Drawable.ic_remote_alert, Resource.Color.state_failed, LoadErrorText, "Try again", () => _ = LoadLocationsAsync(), filled: false);
            return;
        }
        if (!_listLoaded) return;

        if (_status is { Enabled: false })
        {
            StateBlock(Resource.Drawable.ic_remote_power, 0, _phoneHost ? "Remote Files is off" : "Remote Files is off on " + _hostName,
                _busy ? null : "Turn on Remote Files", () => _ = SetEnabledAsync(true), filled: true);
            return;
        }

        AddTransferSummary();
        if (_locationSearchOpen) AddLocationSearch();

        if (_error is not null)
            _content.AddView(Banner(BannerKind.Fail, _error, null, Resource.Drawable.ic_remote_alert));

        if (_sites.Count == 0)
        {
            AddText("No saved remote locations yet.", 16, Resource.Attribute.colorOnSurfaceVariant);
            AddText("Add a location to browse and transfer files.", 14, Resource.Attribute.colorOnSurfaceVariant);
        }
        else
        {
            foreach (var site in _sites)
                AddLocationCard(site);

            _locationNoMatches = Label("No locations match your search.", 15, Resource.Attribute.colorOnSurfaceVariant);
            _locationNoMatches.SetPadding(0, Motion.Dp(_activity, 12), 0, Motion.Dp(_activity, 12));
            _content.AddView(_locationNoMatches);
            FilterLocationCards();
        }

        // Keep the last card and empty state clear of the floating action button.
        _content.SetPadding(Motion.Dp(_activity, 16), Motion.Dp(_activity, 16),
            Motion.Dp(_activity, 16), Motion.Dp(_activity, 104));
        AddFloatingLocationButton();

        if (_busy)
            AddText("Updating…", 13, Resource.Attribute.colorOnSurfaceVariant);
    }

    private string LoadErrorText => "Could not load Remote Files from " + HostPhrase + ". Check the connection and try again.";

    /// <summary>A pane-style message (x14, x16): round icon, text and one pill.</summary>
    private void StateBlock(int icon, int tintColor, string text, string? button, Action? click, bool filled)
    {
        var block = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        block.SetGravity(GravityFlags.CenterHorizontal);
        block.SetPadding(Dp(24), Dp(80), Dp(24), 0);
        var disc = new FrameLayout(_activity);
        disc.Background = Rounded(Attr(Resource.Attribute.colorSurfaceContainerHighest), Dp(40));
        var glyph = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        glyph.SetImageResource(icon);
        glyph.ImageTintList = ColorStateList.ValueOf(new Color(tintColor == 0 ? Attr(Resource.Attribute.colorOnSurfaceVariant) : ResColor(tintColor)));
        disc.AddView(glyph, new FrameLayout.LayoutParams(Dp(32), Dp(32), GravityFlags.Center));
        block.AddView(disc, new LinearLayout.LayoutParams(Dp(80), Dp(80)) { Gravity = GravityFlags.CenterHorizontal });
        var message = FolderText(text, 18, Resource.Attribute.colorOnSurface);
        message.Gravity = GravityFlags.Center;
        message.SetPadding(0, Dp(20), 0, Dp(20));
        block.AddView(message, new LinearLayout.LayoutParams(-1, -2));
        if (button is not null && click is not null)
        {
            var pill = new MaterialButton(_activity, null, filled ? 0 : Resource.Attribute.materialButtonTonalStyle) { Text = button, CornerRadius = Dp(28) };
            if (filled) pill.SetIconResource(Resource.Drawable.ic_remote_power);
            pill.SetAllCaps(false);
            pill.Click += (_, _) => click();
            block.AddView(pill, new LinearLayout.LayoutParams(-2, Dp(filled ? 56 : 40)) { Gravity = GravityFlags.CenterHorizontal });
        }
        _content.AddView(block);
    }

    private void AddFloatingLocationButton()
    {
        var add = new MaterialButton(_activity)
        {
            Text = "Add location",
            ContentDescription = "Add remote location",
            CornerRadius = Motion.Dp(_activity, 18)
        };
        add.SetAllCaps(false);
        add.InsetTop = 0;
        add.InsetBottom = 0;
        add.SetMinimumHeight(Motion.Dp(_activity, 56));
        add.SetIconResource(Resource.Drawable.ic_add);
        add.BackgroundTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorPrimary)));
        add.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnPrimary)));
        add.Click += (_, _) => BeginAddLocation();
        var addParams = new FrameLayout.LayoutParams(-2, -2, GravityFlags.Bottom | GravityFlags.Right)
        {
            RightMargin = Motion.Dp(_activity, 20),
            BottomMargin = Motion.Dp(_activity, 20)
        };
        _surface.AddView(add, addParams);
    }

    private void AddLocationSearch()
    {
        var shell = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        shell.SetGravity(GravityFlags.CenterVertical);
        shell.SetBackgroundResource(Resource.Drawable.bg_pill_search);
        shell.SetPadding(Motion.Dp(_activity, 14), 0, Motion.Dp(_activity, 12), 0);
        shell.LayoutParameters = new LinearLayout.LayoutParams(-1, Motion.Dp(_activity, 52))
        {
            TopMargin = Motion.Dp(_activity, 8),
            BottomMargin = Motion.Dp(_activity, 14)
        };

        var icon = new ImageView(_activity);
        icon.SetImageResource(Resource.Drawable.ic_search);
        icon.ImageTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        shell.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 22), Motion.Dp(_activity, 22)));

        var input = new EditText(_activity)
        {
            Hint = "Search locations",
            Text = _locationSearch,
            SaveEnabled = false,
            Background = null,
            InputType = Android.Text.InputTypes.ClassText
        };
        input.SetSingleLine(true);
        input.SetPadding(Motion.Dp(_activity, 10), 0, 0, 0);
        input.SetTextSize(Android.Util.ComplexUnitType.Sp, 16);
        input.ContentDescription = "Search remote locations";
        input.TextChanged += (_, _) =>
        {
            _locationSearch = input.Text ?? "";
            FilterLocationCards();
        };
        shell.AddView(input, new LinearLayout.LayoutParams(0, -1, 1));
        _content.AddView(shell);
    }

    /// <summary>x11: the Transfers row is always there; amber while transfers run (c3), neutral otherwise.</summary>
    private void AddTransferSummary()
    {
        var card = new MaterialCardView(_activity)
        {
            Radius = Motion.Dp(_activity, 24),
            CardElevation = 0,
            StrokeWidth = 0,
            Clickable = true,
            Focusable = true
        };
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Motion.Dp(_activity, 14)
        };
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int padding = Motion.Dp(_activity, 16);
        row.SetPadding(padding, Motion.Dp(_activity, 10), padding, Motion.Dp(_activity, 10));

        var icon = new ImageView(_activity);
        icon.SetImageResource(Resource.Drawable.ic_remote_transfers);
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 24), Motion.Dp(_activity, 24)));

        var text = Label("", 16, Resource.Attribute.colorOnSurface);
        var textParams = new LinearLayout.LayoutParams(0, -2, 1)
        {
            LeftMargin = Motion.Dp(_activity, 14),
            RightMargin = Motion.Dp(_activity, 8)
        };
        row.AddView(text, textParams);
        var chevron = new ImageView(_activity);
        chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
        chevron.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(chevron, new LinearLayout.LayoutParams(Motion.Dp(_activity, 20), Motion.Dp(_activity, 20)));
        card.AddView(row);
        card.Click += (_, _) => OpenTransfers();
        _content.AddView(card);
        _locationTransferText = text;
        _locationTransferCard = card;
        _locationTransferIcon = icon;
        _locationTransferChevron = chevron;
        UpdateTransferSummary();
    }

    private void UpdateTransferSummary()
    {
        if (_locationTransferText is null || _locationTransferCard is null) return;
        var active = _transfers.Where(t => t.State is "queued" or "running" or "paused").ToArray();
        int uploads = active.Count(t => t.Direction == "upload");
        int downloads = active.Count(t => t.Direction == "download");
        int copies = active.Count(t => t.Direction == "copy");
        var directions = new List<string>();
        if (uploads > 0) directions.Add(uploads + " up");
        if (downloads > 0) directions.Add(downloads + " down");
        if (copies > 0) directions.Add(copies + (copies == 1 ? " copy" : " copies"));
        long rate = active.Where(t => t.State == "running").Aggregate(0L, (total, t) =>
            total + Math.Min(long.MaxValue - total, Math.Max(0, t.Speed)));
        bool running = _transfersLoaded && active.Length > 0;
        int finishedToday = _transfers.Count(t => t.State is "done" or "failed" or "canceled"
            && (t.FinishedAt ?? t.CreatedAt).ToLocalTime().Date == DateTime.Today);
        string summary;
        if (running)
        {
            summary = $"{active.Length} {(active.Length == 1 ? "transfer" : "transfers")} · {string.Join(", ", directions)}";
            if (active.Any(t => t.State == "running")) summary += " · " + Fmt.Rate(rate);
        }
        else if (!_transfersLoaded) summary = "Transfers";
        else summary = finishedToday > 0 ? $"Transfers · {finishedToday} finished today" : "Transfers";

        int background = running ? ResColor(Resource.Color.host_strip_bg) : Attr(Resource.Attribute.colorSurfaceContainer);
        int foreground = running ? ResColor(Resource.Color.host_strip_fg) : Attr(Resource.Attribute.colorOnSurface);
        _locationTransferCard.SetCardBackgroundColor(new Color(background));
        _locationTransferText.SetTextColor(new Color(foreground));
        _locationTransferText.SetTypeface(null, running ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        _locationTransferText.TextSize = running ? 14 : 16;
        _locationTransferIcon?.ImageTintList = ColorStateList.ValueOf(new Color(running ? foreground : ResColor(Resource.Color.state_done)));
        _locationTransferChevron?.ImageTintList = ColorStateList.ValueOf(new Color(running ? foreground : Attr(Resource.Attribute.colorOnSurfaceVariant)));
        _locationTransferText.Text = summary;
        _locationTransferCard.ContentDescription = summary + ". Open transfers.";
        _locationTransferCard.Visibility = ViewStates.Visible;
    }

    private void AddLocationCard(SiteDto site)
    {
        var card = new MaterialCardView(_activity)
        {
            Radius = Motion.Dp(_activity, 24),
            CardElevation = 0,
            StrokeWidth = 0,
            Clickable = true,
            Focusable = true
        };
        card.SetCardBackgroundColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Motion.Dp(_activity, 10)
        };
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int pad = Motion.Dp(_activity, 16);
        row.SetPadding(pad, Motion.Dp(_activity, 12), pad, Motion.Dp(_activity, 12));

        row.AddView(LocationTile(LocationGlyph(site.Kind)), new LinearLayout.LayoutParams(Motion.Dp(_activity, 40), Motion.Dp(_activity, 40)));

        var details = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        details.SetPadding(Motion.Dp(_activity, 14), 0, Motion.Dp(_activity, 4), 0);
        details.AddView(Label(RemoteLocationText.DisplayName(site), 17, Resource.Attribute.colorOnSurface));
        var subtitle = Label(LocationDescription(site), 14, Resource.Attribute.colorOnSurfaceVariant);
        _locationSubtitles[site.Id] = subtitle;
        details.AddView(subtitle);
        var detailsParams = new LinearLayout.LayoutParams(0, -2, 1);
        row.AddView(details, detailsParams);

        var arrow = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        arrow.SetImageResource(Resource.Drawable.ic_chevron_right);
        arrow.ImageTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        if (CreateCloudSignInAction(site) is { } signIn)
            row.AddView(signIn, new LinearLayout.LayoutParams(-2, Motion.Dp(_activity, 48)));
        else
            row.AddView(arrow, new LinearLayout.LayoutParams(Motion.Dp(_activity, 22), Motion.Dp(_activity, 22)));

        card.AddView(row);
        string actionDescription = " Open location.";
        if (NeedsCloudSignIn(site)) actionDescription = CanSignIn(site) ? " Sign in." : "";
        card.ContentDescription = RemoteLocationText.DisplayName(site) + ". " + LocationDescription(site) + "." + actionDescription;
        card.Click += (_, _) => OpenLocation(site);
        card.LongClick += (_, _) => ShowLocationSheet(site);
        _locationCards.Add((site, card));
        _content.AddView(card);
    }

    private void OpenLocation(SiteDto site)
    {
        if (!SameHost() || _busy) return;
        if (site.Kind == SiteKindCatalog.Sftp && (site.HostKeys.Count == 0 || _changedSftpKeys.Contains(site.Id)))
        {
            if (_secureHostConnection && _confirm is null)
                _ = ReviewSftpKeyAsync(site, () => { if (FreshSite(site.Id) is { } fresh && !_inFolder) OpenFolderView(fresh); });
            return;
        }
        if (NeedsCloudSignIn(site))
        {
            BeginCloudSignIn(site);
            return;
        }
        OpenFolderView(site);
    }

    /// <summary>
    /// Free space of a trusted SFTP location, read after the list shows (owner item 3): one small browse that asks for it, so the
    /// row can say "1.2 TB free" without making the list wait.
    /// </summary>
    private async Task FetchSftpFreeSpaceAsync(int generation)
    {
        foreach (var site in _sites.Where(site => site.Kind == SiteKindCatalog.Sftp && site.HostKeys.Count > 0
            && !_changedSftpKeys.Contains(site.Id) && !_freeBytes.ContainsKey(site.Id)).ToArray())
        {
            if (!SameHost() || generation != _generation && _inFolder) return;
            try
            {
                var result = await _client.BrowseAsync(new SiteBrowseRequest { Id = site.Id, Limit = 1, IncludeFreeSpace = true }, _life.Token);
                if (!SameHost()) return;
                if (result.Ok && result.FreeBytes is { } free)
                {
                    _freeBytes[site.Id] = free;
                    if (_locationSubtitles.TryGetValue(site.Id, out var subtitle) && !_inFolder) subtitle.Text = LocationDescription(site);
                }
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
            catch (Exception) { /* The row simply shows no free space. */ }
        }
    }

    private void FilterLocationCards()
    {
        var query = _locationSearch.Trim();
        int visible = 0;
        foreach (var (site, card) in _locationCards)
        {
            bool match = query.Length == 0
                || (RemoteLocationText.DisplayName(site) + " " + site.Name + " " + site.Kind + " " + LocationDescription(site))
                    .Contains(query, StringComparison.CurrentCultureIgnoreCase);
            card.Visibility = match ? ViewStates.Visible : ViewStates.Gone;
            if (match) visible++;
        }
        if (_locationNoMatches is not null)
            _locationNoMatches.Visibility = visible == 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>x11b and x14b.</summary>
    private void ShowLocationsMenu(View anchor)
    {
        bool enabled = _status?.Enabled == true;
        ShowIconMenu(anchor,
            (Resource.Drawable.ic_refresh, "Refresh", () => { _transfersLoaded = false; _ = LoadLocationsAsync(); }),
            (Resource.Drawable.ic_remote_transfers, "Transfers", OpenTransfers),
            (Resource.Drawable.ic_remote_power, enabled ? "Turn off Remote Files" : "Turn on Remote Files", () =>
            {
                if (enabled) ConfirmDisable(); else _ = SetEnabledAsync(true);
            }));
    }

    /// <summary>x12 and x12b: the long-press sheet of a location.</summary>
    private void ShowLocationSheet(SiteDto site)
    {
        if (!SameHost() || _busy) return;
        var dialog = NewSheet(out var column);
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(24), 0, Dp(24), Dp(12));
        header.AddView(LocationTile(LocationGlyph(site.Kind)), new LinearLayout.LayoutParams(Dp(44), Dp(44)));
        var names = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        names.SetPadding(Dp(14), 0, 0, 0);
        var nameText = FolderText(RemoteLocationText.DisplayName(site), 18, Resource.Attribute.colorOnSurface);
        nameText.SetTypeface(null, TypefaceStyle.Bold);
        names.AddView(nameText);
        names.AddView(FolderText(LocationDescription(site, withFree: false), 15, Resource.Attribute.colorOnSurfaceVariant));
        header.AddView(names, new LinearLayout.LayoutParams(0, -2, 1));
        column.AddView(header);
        SheetRow(column, Resource.Drawable.ic_remote_folder, "Open", null, () => OpenLocation(site));
        SheetRow(column, Resource.Drawable.ic_remote_edit, "Edit location", null, () => BeginEditLocation(site));
        if (SiteKindCatalog.SignsIn(site.Kind) && _secureHostConnection)
        {
            SheetRow(column, Resource.Drawable.ic_remote_lock, "Sign in again", null, () => BeginCloudSignIn(site, again: true));
            SheetRow(column, Resource.Drawable.ic_settings_site_key, "Own client ID", "Your own app registration", () => ShowClientIdDialog(site));
        }
        if (site.Kind == SiteKindCatalog.Sftp && _secureHostConnection)
            SheetRow(column, Resource.Drawable.ic_settings_site_key, "Review server key", null,
                () => _ = ReviewSftpKeyAsync(site, () => { _ = LoadLocationsAsync(); }));
        SheetDivider(column);
        SheetRow(column, Resource.Drawable.ic_remote_trash, "Remove location", null, () => ConfirmRemove(site), danger: true);
        ShowSheet(dialog);
    }

    /// <summary>
    /// x30: the desktop's "Your own client ID" for a location that signs in. A new ID signs the location out, as on the desktop:
    /// the old sign-in belongs to the other registration.
    /// </summary>
    private void ShowClientIdDialog(SiteDto site)
    {
        if (!SameHost() || _busy || _confirm is not null || !_secureHostConnection) return;
        var dialog = NewSheet(out var column);
        SheetTitle(column, "Own client ID");
        var note = FolderText(OwnClientIdNote + " A new client ID signs this location out; sign in again afterwards.", 14, Resource.Attribute.colorOnSurfaceVariant);
        AddInset(column, note);
        var (idLayout, idEdit) = OutlinedField("Your own client ID (optional)", site.ClientId ?? "", InputTypes.ClassText);
        var (secretLayout, secretEdit) = OutlinedField("Its client secret (optional)", "", InputTypes.ClassText, password: true,
            placeholder: site.HasClientSecret ? "Client secret saved; type to replace" : null);
        _clientSecret = secretEdit;
        idEdit.TextChanged += (_, _) => SetFieldError(idLayout, null);
        AddInset(column, idLayout);
        AddInset(column, secretLayout);
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O) secretEdit.ImportantForAutofill = ImportantForAutofill.No;
        var save = SheetButtons(column, "Save", Resource.Drawable.ic_remote_check, null);
        // Validation must keep the sheet open, so Save is wired here and the shared click handler is replaced.
        save.SetOnClickListener(new ClickAction(() =>
        {
            var clientId = idEdit.Text?.Trim() ?? "";
            var typed = secretEdit.Text ?? "";
            if (ClientIdProblem(clientId, typed) is { } message) { SetFieldError(idLayout, message); return; }
            var request = new SiteSaveRequest
            {
                Id = site.Id, Name = site.Name, Kind = site.Kind, AuthMethod = "signIn",
                RemoteFolder = site.RemoteFolder, LocalFolder = site.LocalFolder,
                ClientId = clientId.Length == 0 ? null : clientId,
                // As on the desktop: empty keeps the stored secret while the ID stays; no ID removes it.
                ClientSecret = typed.Length > 0 ? typed : clientId.Length == 0 ? "" : null,
            };
            var open = _sheet; _sheet = null; open?.Dismiss();
            ClearPassword();
            _ = SaveLocationAsync(request);
        }));
        dialog.DismissEvent += (_, _) => ClearPassword();
        ShowSheet(dialog);
    }

    // x13
    private void ConfirmRemove(SiteDto site, bool fromForm = false)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        int generation = _generation;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Remove location?")!
            .SetMessage($"Remove {site.Name} from {HostPhrase}? Files in {site.Name} are not deleted.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Remove", (_, _) =>
            {
                if (!SameHost() || generation != _generation) return;
                if (fromForm)
                {
                    ClearPassword();
                    _adding = false; _addStep = 0; _editSite = null; _savedId = null; _fv.Clear();
                }
                _ = RemoveLocationAsync(site.Id);
            })!
            .Create();
        _confirm!.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task RemoveLocationAsync(string id)
    {
        if (_closed || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var result = await _client.RemoveAsync(id, _life.Token);
            if (_closed || generation != _generation) return;
            if (!result.Ok) { _error = "The Host could not remove this location."; return; }
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not confirm removal. Refresh to check the location list.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    /// <summary>Saves a change to an existing location (the own client ID sheet) and reloads the list.</summary>
    private async Task SaveLocationAsync(SiteSaveRequest request)
    {
        if (!SameHost() || _busy || !_secureHostConnection) { request.Password = null; request.ClientSecret = null; return; }
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var result = await _client.SaveAsync(request, _life.Token);
            if (_closed || generation != _generation) return;
            if (!result.Ok)
            {
                _error = result.Message ?? "The Host could not save this location. Check the details and try again.";
                return;
            }
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not confirm the save. Refresh the location list before trying again.";
        }
        finally
        {
            request.Password = null;
            request.ClientSecret = null;
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    // x15
    private void ConfirmDisable()
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Turn off Remote Files?")!
            .SetMessage((_phoneHost ? "On this phone." : "On " + _hostName + ".") + " Running transfers pause. Your locations stay saved.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Turn off", (_, _) => _ = SetEnabledAsync(false))!
            .Show();
        if (_confirm is { } dialog) dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        try
        {
            var status = await _client.SetEnabledAsync(enabled, _life.Token);
            if (_closed || generation != _generation) return;
            if (status.Enabled != enabled)
            {
                _error = "The Host did not change Remote Files. Refresh to check its state.";
                return;
            }
            _busy = false;
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && generation == _generation)
                _error = "Could not change Remote Files. Refresh to check the Host's state.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
    }

    private string LocationDescription(SiteDto site, bool withFree = true)
    {
        if (SiteKindCatalog.SignsIn(site.Kind))
            return site.SigningIn ? "Waiting for sign-in" : _expiredCloudSites.Contains(site.Id) ? "Sign-in has run out"
                : site.SignedIn ? "Signed in" : "Sign-in required";
        var kind = site.Kind == SiteKindCatalog.WebDav ? "WebDAV" : site.Kind.ToUpperInvariant();
        string address = site.Kind.ToLowerInvariant() switch
        {
            "webdav" => HostOf(site.Url),
            "s3" or "b2" or "swift" => !string.IsNullOrWhiteSpace(site.Region) ? site.Region! : HostOf(site.Endpoint),
            "drive" or "onedrive" or "dropbox" or "box" or "pcloud" => site.SignedIn ? "Signed in" : "Sign-in required",
            _ => string.IsNullOrWhiteSpace(site.Host) ? "" : site.Host +
                (site.Port > 0 && (site.Kind != SiteKindCatalog.Sftp || site.Port != 22) && (site.Kind != SiteKindCatalog.Ftp || site.Port != 21) ? ":" + site.Port : "")
        };
        string detail = kind;
        if (!string.IsNullOrWhiteSpace(address)) detail += " · " + address;
        long? free = _freeBytes.TryGetValue(site.Id, out var known) ? known : site.FreeBytes;
        if (withFree && site.Kind == SiteKindCatalog.Sftp && free is >= 0) detail += " · " + DownloadManager.Mobile.Fmt.Bytes(free.Value) + " free";
        if (!site.Encrypted) detail += " · Not encrypted";
        return detail;
    }

    private static string HostOf(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        return uri.IsDefaultPort ? uri.Host : uri.Authority;
    }
}
