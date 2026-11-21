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

namespace Inbound.App.Screens;

/// <summary>The saved-location overview, styled after the approved C3 Sites reference.</summary>
internal sealed partial class RemoteFilesScreen
{
    private string _locationSearch = "";
    private bool _locationSearchOpen;
    private readonly List<(SiteDto Site, View Card)> _locationCards = [];
    private readonly Dictionary<string, TextView> _locationSubtitles = new(StringComparer.Ordinal);
    private TextView? _locationNoMatches;
    private TextView? _locationTransferText;
    private MaterialCardView? _locationTransferCard;

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
        var overflow = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle)
        {
            Text = "⋮",
            ContentDescription = "Remote Files options"
        };
        overflow.SetAllCaps(false);
        overflow.SetMinWidth(0);
        overflow.SetTextSize(Android.Util.ComplexUnitType.Sp, 24);
        overflow.SetPadding(Motion.Dp(_activity, 10), 0, Motion.Dp(_activity, 10), 0);
        overflow.Click += (_, _) => ShowLocationsMenu(overflow);
        if (_status?.Enabled == true)
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
            search.LongClick += (_, _) => ShowLocationsMenu(search);
            titleRow.AddView(search, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
        }
        else titleRow.AddView(overflow, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
        titleRow.LayoutParameters = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Motion.Dp(_activity, 12)
        };
        _content.AddView(titleRow);
        if (!_phoneHost) AddText("Host: " + _hostName, 14, Resource.Attribute.colorOnSurfaceVariant);

        if (_busy && !_listLoaded)
        {
            AddText("Loading Remote Files…", 16, Resource.Attribute.colorOnSurfaceVariant);
            return;
        }
        if (_error is not null && !_listLoaded)
        {
            AddText(_error, 16, Resource.Attribute.colorOnSurfaceVariant);
            AddButton("Try again", () => _ = LoadLocationsAsync());
            return;
        }
        if (!_listLoaded) return;

        if (_status is { Enabled: false })
        {
            AddText("Remote Files is off on this Host.", 15, Resource.Attribute.colorOnSurfaceVariant);
            if (!_busy) AddButton("Enable Remote Files", () => _ = SetEnabledAsync(true));
            return;
        }

        AddTransferSummary();
        if (_locationSearchOpen) AddLocationSearch();

        if (_error is not null)
            AddText(_error, 14, Resource.Attribute.colorError);

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
        add.Click += (_, _) =>
        {
            if (_busy || _closed) return;
            _adding = true;
            _error = null;
            Render();
        };
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
        card.SetCardBackgroundColor(new Color(_activity.GetColor(Resource.Color.host_strip_bg)));
        card.LayoutParameters = new LinearLayout.LayoutParams(-1, -2)
        {
            BottomMargin = Motion.Dp(_activity, 14)
        };
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int padding = Motion.Dp(_activity, 16);
        row.SetPadding(padding, Motion.Dp(_activity, 10), padding, Motion.Dp(_activity, 10));

        var icon = new ImageView(_activity);
        icon.SetImageResource(Resource.Drawable.ic_remote_transfer);
        icon.ImageTintList = ColorStateList.ValueOf(new Color(_activity.GetColor(Resource.Color.host_strip_fg)));
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 24), Motion.Dp(_activity, 24)));

        var text = Label("", 14, Resource.Attribute.colorOnTertiaryContainer);
        text.SetTextColor(new Color(_activity.GetColor(Resource.Color.host_strip_fg)));
        text.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        var textParams = new LinearLayout.LayoutParams(0, -2, 1)
        {
            LeftMargin = Motion.Dp(_activity, 14),
            RightMargin = Motion.Dp(_activity, 8)
        };
        row.AddView(text, textParams);
        var chevron = new ImageView(_activity);
        chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
        chevron.ImageTintList = ColorStateList.ValueOf(new Color(_activity.GetColor(Resource.Color.host_strip_fg)));
        chevron.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(chevron, new LinearLayout.LayoutParams(Motion.Dp(_activity, 20), Motion.Dp(_activity, 20)));
        card.AddView(row);
        card.Click += (_, _) => OpenTransfers();
        _content.AddView(card);
        _locationTransferText = text;
        _locationTransferCard = card;
        UpdateTransferSummary();
    }

    private void UpdateTransferSummary()
    {
        if (_locationTransferText is null || _locationTransferCard is null) return;
        var active = _transfers.Where(t => t.State is "queued" or "running" or "paused").ToArray();
        int uploads = active.Count(t => t.Direction == "upload");
        int downloads = active.Count(t => t.Direction == "download");
        long rate = active.Where(t => t.State == "running").Aggregate(0L, (total, t) =>
            total + Math.Min(long.MaxValue - total, Math.Max(0, t.Speed)));
        string summary = !_transfersLoaded
            ? "Transfer status unavailable"
            : active.Length == 0
            ? "No active transfers"
            : $"{active.Length} {(active.Length == 1 ? "transfer" : "transfers")} · {uploads} up, {downloads} down";
        if (_transfersLoaded && active.Any(t => t.State == "running"))
            summary += " · " + Fmt.Rate(rate);
        _locationTransferText.Text = summary;
        _locationTransferCard.ContentDescription = summary + ". Open transfers.";
        // The banner is for running transfers; with none (or no answer yet) it is not drawn (owner item 1).
        _locationTransferCard.Visibility = _transfersLoaded && active.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
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

        var tile = new FrameLayout(_activity);
        var disc = new Android.Graphics.Drawables.GradientDrawable();
        disc.SetCornerRadius(Motion.Dp(_activity, 20));
        disc.SetColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainerHighest)));
        tile.Background = disc;
        var icon = new ImageView(_activity);
        icon.SetImageResource(LocationGlyph(site.Kind));
        icon.ImageTintList = ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        icon.SetPadding(Motion.Dp(_activity, 10), Motion.Dp(_activity, 10), Motion.Dp(_activity, 10), Motion.Dp(_activity, 10));
        tile.AddView(icon, new FrameLayout.LayoutParams(-1, -1, GravityFlags.Center));
        row.AddView(tile, new LinearLayout.LayoutParams(Motion.Dp(_activity, 40), Motion.Dp(_activity, 40)));

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
        card.Click += (_, _) =>
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
        };
        card.LongClick += (_, _) => ShowLocationMenu(card, site);
        _locationCards.Add((site, card));
        _content.AddView(card);
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

    private void ShowLocationsMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        var items = menu.Menu!;
        items.Add(0, 1, 0, "Refresh");
        items.Add(0, 2, 1, _status?.Enabled == true ? "Disable Remote Files" : "Set up Remote Files");
        menu.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == 1)
            {
                _transfersLoaded = false;
                _ = LoadLocationsAsync();
            }
            else if (e.Item?.ItemId == 2)
            {
                if (_status?.Enabled == true) ConfirmDisable();
                else _ = SetEnabledAsync(true);
            }
        };
        menu.Show();
    }

    private void ShowLocationMenu(View anchor, SiteDto site)
    {
        var menu = new PopupMenu(_activity, anchor);
        menu.Menu!.Add(0, 1, 0, "Remove location");
        if (SiteKindCatalog.SignsIn(site.Kind) && _secureHostConnection) menu.Menu!.Add(0, 2, 1, "Own client ID");
        menu.MenuItemClick += (_, e) =>
        {
            if (e.Item?.ItemId == 1) ConfirmRemove(site);
            else if (e.Item?.ItemId == 2) ShowClientIdDialog(site);
        };
        menu.Show();
    }

    /// <summary>
    /// The desktop's "Your own client ID" for a location that signs in. A new ID signs the location out, as on the desktop:
    /// the old sign-in belongs to the other registration.
    /// </summary>
    private void ShowClientIdDialog(SiteDto site)
    {
        if (!SameHost() || _busy || _confirm is not null || !_secureHostConnection) return;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetPadding(Motion.Dp(_activity, 20), Motion.Dp(_activity, 8), Motion.Dp(_activity, 20), 0);
        body.AddView(Label(OwnClientIdNote + " A new client ID signs this location out; sign in again afterwards.", 14,
            Resource.Attribute.colorOnSurfaceVariant));
        var id = new EditText(_activity) { Hint = "Your own client ID (optional)", Text = site.ClientId ?? "", SaveEnabled = false };
        id.SetSingleLine(true);
        id.ContentDescription = "Your own client ID (optional)";
        body.AddView(id);
        var secret = _clientSecret = new EditText(_activity)
        {
            Hint = site.HasClientSecret ? "Client secret saved; type to replace" : "Its client secret (optional)",
            SaveEnabled = false,
        };
        secret.SetSingleLine(true);
        secret.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
        secret.ContentDescription = "Client secret (optional)";
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O) secret.ImportantForAutofill = ImportantForAutofill.No;
        body.AddView(secret);
        var problem = Label("", 14, Resource.Attribute.colorError);
        body.AddView(problem);
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Own client ID")!.SetView(body)!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Save", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .Create();
        _confirm = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_confirm, dialog)) _confirm = null;
            ClearPassword();
        };
        dialog.ShowEvent += (_, _) => dialog.GetButton((int)Android.Content.DialogButtonType.Positive)!.Click += (_, _) =>
        {
            var clientId = id.Text?.Trim() ?? "";
            var typed = secret.Text ?? "";
            if (ClientIdProblem(clientId, typed) is { } message) { problem.Text = message; return; }
            var request = new SiteSaveRequest
            {
                Id = site.Id, Name = site.Name, Kind = site.Kind, AuthMethod = "signIn",
                RemoteFolder = site.RemoteFolder, LocalFolder = site.LocalFolder,
                ClientId = clientId.Length == 0 ? null : clientId,
                // As on the desktop: empty keeps the stored secret while the ID stays; no ID removes it.
                ClientSecret = typed.Length > 0 ? typed : clientId.Length == 0 ? "" : null,
            };
            dialog.Dismiss();
            _ = SaveLocationAsync(request);
        };
        dialog.Show();
    }

    private string LocationDescription(SiteDto site)
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
                (site.Port > 0 && (site.Kind != SiteKindCatalog.Sftp || site.Port != 22) ? ":" + site.Port : "")
        };
        string detail = kind;
        if (!string.IsNullOrWhiteSpace(address)) detail += " · " + address;
        long? free = _freeBytes.TryGetValue(site.Id, out var known) ? known : site.FreeBytes;
        if (site.Kind == SiteKindCatalog.Sftp && free is >= 0) detail += " · " + DownloadManager.Mobile.Fmt.Bytes(free.Value) + " free";
        if (!site.Encrypted) detail += " · Not encrypted";
        return detail;
    }

    private static string HostOf(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        return uri.IsDefaultPort ? uri.Host : uri.Authority;
    }
}
