using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Inbound.App.Screens;

/// <summary>The Transfers screen of mockups x1 to x6: app bar, filter chips, one card per transfer, details sheet (x2, x2b), cancel confirm (x3).</summary>
internal sealed partial class RemoteFilesScreen
{
    private string _transferFilter = "all";
    // Item paths per transfer id, from sites.transfer.get: they name a card ("session-0503.flac", "Wedding") and fill its Files list.
    private readonly Dictionary<string, List<string>> _transferPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _transferBusy = new(StringComparer.Ordinal);
    private bool _transferLoading, _transferQuiet, _transferErrorRetry, _transferReloadPending, _transferReloadQuiet = true;
    private int _transfersGen;
    private string? _transferErrorTitle, _transferErrorText;
    private DateTimeOffset _transfersAt;
    private LinearLayout? _tfShell, _tfBanner, _tfChips, _tfList;
    private HorizontalScrollView? _tfChipScroll;
    private SwipeRefreshLayout? _tfSwipe;

    private void OpenTransfers()
    {
        if (!SameHost() || _busy) return;
        _showTransfers = true;
        _transfersGen++;
        _transferLoading = false;
        _transferErrorTitle = _transferErrorText = null;
        _ = LoadTransfersAsync();
    }

    private void CloseTransfers()
    {
        CancelCurrent();
        _transfersGen++;
        _transferLoading = false;
        _showTransfers = false;
        _error = null;
        _confirm?.Dismiss();
        _sheet?.Dismiss();
        Render();
    }

    private async Task LoadTransfersAsync(bool quiet = false)
    {
        if (!SameHost()) return;
#if INBOUND_TEST_BUILD
        if (FixtureMode is not null) return;
#endif
        if (_transferLoading)
        {
            // A look is in flight: ask for one more when it ends, so an action or "Try again" is never dropped.
            _transferReloadPending = true;
            _transferReloadQuiet &= quiet;
            return;
        }
        int generation = ++_transfersGen;
        _transferLoading = true;
        _transferQuiet = quiet;
        UpdateTransfersUi();
        try
        {
            var result = await _client.ListTransfersAsync(_life.Token);
            if (!SameHost() || generation != _transfersGen) return;
            _transfers = result.Transfers;
            _transfersLoaded = true;
            _transfersAt = DateTimeOffset.Now;
            _transferErrorTitle = _transferErrorText = null;
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (SameHost() && generation == _transfersGen)
            {
                _transferErrorTitle = "Could not refresh transfers";
                _transferErrorText = _transfersLoaded
                    ? "Their current state is unknown. Shown as of " + _transfersAt.ToString("HH:mm") + "."
                    : "Check the connection and try again.";
                _transferErrorRetry = true;
            }
        }
        finally
        {
            if (!_closed && generation == _transfersGen) { _transferLoading = false; UpdateTransfersUi(); }
        }
        if (_transferReloadPending && !_closed && !_transferLoading)
        {
            bool again = _transferReloadQuiet;
            _transferReloadPending = false;
            _transferReloadQuiet = true;
            _ = LoadTransfersAsync(again);
        }
        _ = FetchTransferPathsAsync(generation); // not awaited: the 3 s loop must not wait for the names
    }

    /// <summary>Names for the cards: one sites.transfer.get per transfer not asked about yet (its files do not change).</summary>
    private bool _fetchingPaths;

    private async Task FetchTransferPathsAsync(int generation)
    {
        if (_fetchingPaths) return;
        _fetchingPaths = true;
        try
        {
            foreach (var transfer in _transfers.Where(t => !_transferPaths.ContainsKey(t.Id)).Take(20).ToList())
            {
                var result = await _client.GetTransferAsync(transfer.Id, _life.Token);
                if (!SameHost() || !_showTransfers || _closed) return;
                // An answer without items is remembered too (an empty list), so it is not asked again every 3 s.
                _transferPaths[transfer.Id] = result.Ok && result.Items is { Count: > 0 } items ? [.. items.Select(item => item.Path).Take(2000)] : [];
                UpdateTransfersUi();
            }
        }
        catch (Exception) { /* A card without a name keeps "N files". */ }
        finally { _fetchingPaths = false; }
    }

    private string TransferTitle(SiteTransferDto t) => RemoteTransferText.Title(t, _transferPaths.GetValueOrDefault(t.Id));

    // ---------------------------------------------------------------------------------------------------------------
    // Screen

    private void RenderTransfers()
    {
        EnsureTransfersChrome();
        UpdateTransfersUi();
    }

    private void EnsureTransfersChrome()
    {
        if (_surface.ChildCount == 1 && _tfShell?.Parent == _surface) return;
        _surface.RemoveAllViews();
        _bannerSig = _chipsSig = null;
        _cards.Clear();
        _tfShell = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _tfShell.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorSurface)));

        var bar = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(4), 0, Dp(4), 0);
        bar.AddView(IconButton(Resource.Drawable.ic_arrow_back, "Back", () => HandleBack()), new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        var titles = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        titles.SetPadding(Dp(12), 0, 0, 0);
        titles.AddView(FolderText("Transfers", 22, Resource.Attribute.colorOnSurface));
        if (!_phoneHost) titles.AddView(FolderText("On " + _hostName, 14, Resource.Attribute.colorOnSurfaceVariant));
        bar.AddView(titles, new LinearLayout.LayoutParams(0, -2, 1));
        View more = IconButton(Resource.Drawable.ic_more, "More options", () => { });
        more.Click += (_, _) => ShowTransfersMenu(more);
        bar.AddView(more, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        _tfShell.AddView(bar, new LinearLayout.LayoutParams(-1, Dp(_phoneHost ? 72 : 76)));

        _tfBanner = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _tfShell.AddView(_tfBanner, new LinearLayout.LayoutParams(-1, -2));

        _tfChips = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        _tfChips.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
        _tfChipScroll = new HorizontalScrollView(_activity) { HorizontalScrollBarEnabled = false };
        _tfChipScroll.AddView(_tfChips);
        _tfShell.AddView(_tfChipScroll, new LinearLayout.LayoutParams(-1, -2));

        var scroll = new ScrollView(_activity) { FillViewport = true };
        _tfList = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _tfList.SetPadding(Dp(16), Dp(4), Dp(16), Dp(24));
        scroll.AddView(_tfList, new ViewGroup.LayoutParams(-1, -2));
        _tfSwipe = new SwipeRefreshLayout(_activity);
        _tfSwipe.SetColorSchemeColors(Attr(Resource.Attribute.colorPrimary));
        _tfSwipe.SetProgressBackgroundColorSchemeColor(Attr(Resource.Attribute.colorSurfaceContainer));
        _tfSwipe.Refresh += (_, _) =>
        {
            if (_transferLoading || !SameHost()) { _tfSwipe.Refreshing = _transferLoading && !_transferQuiet; return; }
            _ = LoadTransfersAsync();
        };
        _tfSwipe.AddView(scroll, new ViewGroup.LayoutParams(-1, -1));
        _tfShell.AddView(_tfSwipe, new LinearLayout.LayoutParams(-1, 0, 1));
        _surface.AddView(_tfShell, new FrameLayout.LayoutParams(-1, -1));
    }

    private void UpdateTransfersUi()
    {
        if (Android.OS.Looper.MyLooper() != Android.OS.Looper.MainLooper) { _activity.RunOnUiThread(UpdateTransfersUi); return; }
        if (_closed || !_showTransfers || _tfShell?.Parent is null) return;
        BuildTransfersBanner();
        BuildTransferChips();
        BuildTransferList();
        bool spinning = _transferLoading && !_transferQuiet;
        if (_tfSwipe!.Refreshing != spinning) _tfSwipe.Refreshing = spinning;
    }

    private string? _bannerSig;

    private void BuildTransfersBanner()
    {
        string sig = _transferErrorTitle + "|" + _transferErrorText + "|" + _transferErrorRetry;
        if (sig == _bannerSig) return;
        _bannerSig = sig;
        _tfBanner!.RemoveAllViews();
        if (_transferErrorTitle is null) { _tfBanner.Visibility = ViewStates.Gone; return; }
        _tfBanner.Visibility = ViewStates.Visible;
        var card = TransferBanner(_transferErrorTitle, _transferErrorText, warning: false);
        if (_transferErrorRetry)
        {
            var retry = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle) { Text = "Try again" };
            retry.SetAllCaps(false);
            retry.SetTextColor(new Color(Attr(Resource.Attribute.colorPrimary)));
            retry.Click += (_, _) => { _transferErrorTitle = null; UpdateTransfersUi(); _ = LoadTransfersAsync(); };
            ((LinearLayout)card.GetChildAt(1)!).AddView(retry, new LinearLayout.LayoutParams(-2, Dp(40)) { TopMargin = Dp(4), LeftMargin = Dp(-12) });
        }
        _tfBanner.AddView(card, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = Dp(16), RightMargin = Dp(16), TopMargin = Dp(4), BottomMargin = Dp(4) });
    }

    /// <summary>A red (or amber) banner: warning glyph, bold title, text. The text column is child 1.</summary>
    private LinearLayout TransferBanner(string title, string? text, bool warning, int iconRes = 0)
    {
        int tone = _activity.GetColor(warning ? Resource.Color.state_waiting : Resource.Color.state_failed);
        var card = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        card.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
        var background = new GradientDrawable();
        background.SetCornerRadius(Dp(16));
        background.SetColor(new Color(Color.GetRedComponent(tone), Color.GetGreenComponent(tone), Color.GetBlueComponent(tone), 48));
        card.Background = background;
        var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(iconRes == 0 ? Resource.Drawable.ic_remote_warning : iconRes);
        icon.ImageTintList = ColorStateList.ValueOf(new Color(tone));
        card.AddView(icon, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(14), TopMargin = Dp(2) });
        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var heading = FolderText(title, 16, Resource.Attribute.colorOnSurface);
        heading.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        texts.AddView(heading);
        if (text is not null) texts.AddView(FolderText(text, 14, Resource.Attribute.colorOnSurface));
        card.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        card.ContentDescription = title + (text is null ? "" : ". " + text);
        return card;
    }

    private static readonly (string Key, string Label)[] TransferFilters =
        [("all", "All"), ("active", "Active"), ("finished", "Finished"), ("failed", "Failed"), ("canceled", "Canceled")];

    private string? _chipsSig;

    private void BuildTransferChips()
    {
        bool any = _transfers.Count > 0;
        string sig = any ? _transferFilter + "|" + string.Join(",", TransferFilters.Select(f => _transfers.Count(t => RemoteTransferText.InFilter(t, f.Key)))) : "";
        if (sig == _chipsSig) return;
        _chipsSig = sig;
        _tfChips!.RemoveAllViews();
        _tfChipScroll!.Visibility = any ? ViewStates.Visible : ViewStates.Gone;
        if (!any) return;
        foreach (var (key, label) in TransferFilters)
        {
            int count = _transfers.Count(t => RemoteTransferText.InFilter(t, key));
            bool on = key == _transferFilter;
            var chip = FolderText(label + " " + count, 14, on ? Resource.Attribute.colorOnSecondaryContainer : Resource.Attribute.colorOnSurface);
            chip.Gravity = GravityFlags.Center;
            chip.SetSingleLine(true);
            chip.SetPadding(Dp(16), 0, Dp(16), 0);
            var shape = new GradientDrawable();
            shape.SetCornerRadius(Dp(8));
            shape.SetColor(new Color(Attr(on ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurface)));
            if (!on) shape.SetStroke(Dp(1), new Color(Attr(Resource.Attribute.colorOutlineVariant)));
            chip.Background = shape;
            chip.Clickable = true;
            chip.Focusable = true;
            chip.ContentDescription = label + ", " + count;
            chip.SetAccessibilityDelegate(new ChipDelegate(on));
            chip.Click += (_, _) => { _transferFilter = key; UpdateTransfersUi(); };
            _tfChips.AddView(chip, new LinearLayout.LayoutParams(-2, Dp(36)) { RightMargin = Dp(8) });
        }
    }

    /// <summary>A built card with the parts that change while it runs, so a refresh updates them in place (focus and ripple stay).</summary>
    private sealed record CardRef(View Card, string Sig, TextView Status, LinearProgressIndicator? Bar);

    private readonly Dictionary<string, CardRef> _cards = new(StringComparer.Ordinal);

    private string CardSignature(SiteTransferDto t)
    {
        var (source, destination) = RemoteTransferText.Route(t, _phoneHost);
        return string.Join("|", t.State, TransferTitle(t), source, destination, _transferBusy.Contains(t.Id), CardAction(t) is null,
            t.State is "running" or "paused" && t.Bytes > 0, CanShowTransferFolder(t), RemoteTransferText.NeedsAttention(t));
    }

    private void UpdateCard(CardRef card, SiteTransferDto t, DateTimeOffset now)
    {
        string status = RemoteTransferText.Status(t, now);
        if (card.Status.Text != status) card.Status.Text = status;
        if (card.Bar is { } bar) bar.Progress = (int)Math.Min(1000, t.BytesDone * 1000 / Math.Max(1, t.Bytes));
        var (source, destination) = RemoteTransferText.Route(t, _phoneHost);
        card.Card.ContentDescription = TransferTitle(t) + ", " + status + ", " + source + " to " + destination + ". Details";
    }

    private void BuildTransferList()
    {
        if (!_transfersLoaded || _transfers.Count == 0) { _cards.Clear(); _tfList!.RemoveAllViews(); }
        if (!_transfersLoaded)
        {
            if (_transferLoading)
            {
                var spinner = new CircularProgressIndicator(_activity) { Indeterminate = true };
                spinner.ContentDescription = "Loading transfers";
                _tfList!.AddView(spinner, new LinearLayout.LayoutParams(-2, -2) { Gravity = GravityFlags.CenterHorizontal, TopMargin = Dp(48) });
            }
            return;
        }
        if (_transfers.Count == 0)
        {
            _tfList!.AddView(TransfersEmpty(), new LinearLayout.LayoutParams(-1, 0, 1));
            return;
        }
        var shown = _transfers.Where(t => RemoteTransferText.InFilter(t, _transferFilter)).OrderByDescending(t => t.CreatedAt).ToList();
        if (shown.Count == 0)
        {
            _cards.Clear();
            _tfList!.RemoveAllViews();
            var none = FolderText("No transfers here.", 15, Resource.Attribute.colorOnSurfaceVariant);
            none.SetPadding(Dp(8), Dp(24), Dp(8), 0);
            _tfList.AddView(none);
            return;
        }
        var now = DateTimeOffset.Now;
        var ordered = new List<CardRef>(shown.Count);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transfer in shown)
        {
            keep.Add(transfer.Id);
            string sig = CardSignature(transfer);
            if (_cards.TryGetValue(transfer.Id, out var existing) && existing.Sig == sig && existing.Card.Parent == _tfList)
            {
                UpdateCard(existing, transfer, now);
                ordered.Add(existing);
            }
            else ordered.Add(_cards[transfer.Id] = TransferCard(transfer, now, sig));
        }
        foreach (var id in _cards.Keys.Where(id => !keep.Contains(id)).ToList()) _cards.Remove(id);
        bool same = _tfList!.ChildCount == ordered.Count;
        for (int i = 0; same && i < ordered.Count; i++) same = ReferenceEquals(_tfList.GetChildAt(i), ordered[i].Card);
        if (same) return;
        _tfList.RemoveAllViews();
        foreach (var card in ordered) _tfList.AddView(card.Card, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(8) });
    }

    private View TransfersEmpty()
    {
        var box = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        box.SetGravity(GravityFlags.Center);
        box.SetPadding(Dp(32), 0, Dp(32), Dp(96));
        var disc = new FrameLayout(_activity);
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Oval);
        shape.SetColor(new Color(Attr(Resource.Attribute.colorSecondaryContainer)));
        disc.Background = shape;
        var glyph = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        glyph.SetImageResource(Resource.Drawable.ic_remote_transfers);
        glyph.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorPrimary)));
        disc.AddView(glyph, new FrameLayout.LayoutParams(Dp(32), Dp(32), GravityFlags.Center));
        box.AddView(disc, new LinearLayout.LayoutParams(Dp(88), Dp(88)));
        var title = FolderText(_phoneHost ? "No transfers on this phone" : "No transfers on " + _hostName, 22, Resource.Attribute.colorOnSurface);
        title.Gravity = GravityFlags.Center;
        box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(24) });
        var text = FolderText("Files you move with Remote Files show up here with their progress.", 15, Resource.Attribute.colorOnSurfaceVariant);
        text.Gravity = GravityFlags.Center;
        box.AddView(text, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(8) });
        return box;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Cards

    private int ToneColor(TransferTone tone) => tone switch
    {
        TransferTone.Done => _activity.GetColor(Resource.Color.state_done),
        TransferTone.Failed => _activity.GetColor(Resource.Color.state_failed),
        TransferTone.Paused => _activity.GetColor(Resource.Color.state_waiting),
        _ => Attr(Resource.Attribute.colorOnSurfaceVariant),
    };

    private static int DirectionGlyph(SiteTransferDto t) => t.Direction switch
    {
        "upload" => Resource.Drawable.ic_remote_up,
        "copy" => Resource.Drawable.ic_remote_arrow,
        _ => Resource.Drawable.ic_remote_download,
    };

    private View DirectionTile(SiteTransferDto t, int size)
    {
        var tone = RemoteTransferText.ToneOf(t);
        int tint = tone switch
        {
            TransferTone.Done => _activity.GetColor(Resource.Color.state_done),
            TransferTone.Failed => _activity.GetColor(Resource.Color.state_failed),
            TransferTone.Canceled => Attr(Resource.Attribute.colorOnSurfaceVariant),
            _ => Attr(Resource.Attribute.colorPrimary),
        };
        var tile = new FrameLayout(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        var shape = new GradientDrawable();
        shape.SetCornerRadius(Dp(11));
        shape.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHighest)));
        tile.Background = shape;
        var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(DirectionGlyph(t));
        icon.ImageTintList = ColorStateList.ValueOf(new Color(tint));
        tile.AddView(icon, new FrameLayout.LayoutParams(Dp(size / 2 + 2), Dp(size / 2 + 2), GravityFlags.Center));
        return tile;
    }

    /// <summary>The round action of a card: what its state offers (pause, resume, retry, cancel, show folder), or null.</summary>
    private (int Icon, string Description, bool Primary, Action Click)? CardAction(SiteTransferDto t)
    {
        string title = TransferTitle(t);
        return t.State switch
        {
            "running" => (Resource.Drawable.ic_pause, "Pause " + title, true, () => _ = TransferActionAsync(t.Id, "pause")),
            "paused" => (Resource.Drawable.ic_play, "Resume " + title, false, () => _ = TransferActionAsync(t.Id, "resume")),
            "failed" when !RemoteTransferText.NeedsAttention(t) => (Resource.Drawable.ic_refresh, "Retry " + title, false, () => _ = TransferActionAsync(t.Id, "retry")),
            "failed" => AttentionCardAction(t, title),
            "queued" => (Resource.Drawable.ic_close, "Cancel " + title, false, () => ConfirmCancelTransfer(t)),
            "done" when CanShowTransferFolder(t) => (Resource.Drawable.ic_remote_folder, "Show " + title + " in folder", false, () => ShowTransferFolder(t, null)),
            _ => null,
        };
    }

    private static int AttentionIcon(TransferAttention attention) => attention switch
    {
        TransferAttention.SignIn => Resource.Drawable.ic_open,
        TransferAttention.ReviewKey => Resource.Drawable.ic_settings_site_key,
        _ => Resource.Drawable.ic_remote_lock,
    };

    /// <summary>A failed transfer that needs a sign-in or a trust decision first: that action takes the place of Retry.</summary>
    private (int Icon, string Description, bool Primary, Action Click)? AttentionCardAction(SiteTransferDto t, string title)
    {
        var attention = RemoteTransferText.AttentionOf(t);
        if (attention == TransferAttention.None) return (Resource.Drawable.ic_refresh, "Retry " + title, false, () => _ = TransferActionAsync(t.Id, "retry"));
        return (AttentionIcon(attention), RemoteTransferText.AttentionLabel(attention) + ": " + title, false, () => RunAttention(t));
    }

    /// <summary>Runs the existing sign-in or trust flow for the location of <paramref name="t"/>, then retries the transfer.</summary>
    private void RunAttention(SiteTransferDto t)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        var attention = RemoteTransferText.AttentionOf(t);
        if (FreshSite(t.SiteId) is not { } site)
        {
            _activity.ShowMessage("Open Remote Files again and refresh the locations first.");
            return;
        }
        void Retry() { if (SameHost()) _ = TransferActionAsync(t.Id, "retry"); }
        switch (attention)
        {
            case TransferAttention.SignIn:
                if (!CanSignIn(site)) { _activity.ShowMessage("Sign in to " + site.Name + " from the Host."); return; }
                _retryAfterSignIn = (site.Id, t.Id);
                BeginCloudSignIn(site, again: true);
                break;
            case TransferAttention.ReviewKey:
                if (!_secureHostConnection) { _activity.ShowMessage("The server key must be reviewed over a secure connection to the Host."); return; }
                _ = ReviewSftpKeyAsync(site, Retry);
                break;
            case TransferAttention.ReviewCertificate:
                if (!_secureHostConnection) { _activity.ShowMessage("The certificate must be reviewed over a secure connection to the Host."); return; }
                _ = ShowCertificateTrustAsync(site, Retry);
                break;
        }
    }

    private (string SiteId, string TransferId)? _retryAfterSignIn;

    private View RoundAction(int icon, string description, bool primary, bool enabled, Action click)
    {
        var button = new FrameLayout(_activity) { Clickable = enabled, Focusable = enabled, ContentDescription = description, Alpha = enabled ? 1f : 0.5f };
        var disc = new GradientDrawable();
        disc.SetShape(ShapeType.Oval);
        disc.SetColor(new Color(Attr(primary ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurfaceContainerHighest)));
        button.Background = disc;
        var glyph = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        glyph.SetImageResource(icon);
        glyph.ImageTintList = ColorStateList.ValueOf(new Color(Attr(primary ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurface)));
        button.AddView(glyph, new FrameLayout.LayoutParams(Dp(20), Dp(20), GravityFlags.Center));
        button.Click += (_, _) => click();
        return button;
    }

    private CardRef TransferCard(SiteTransferDto t, DateTimeOffset now, string sig)
    {
        var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true };
        var shape = new GradientDrawable();
        shape.SetCornerRadius(Dp(24));
        shape.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        card.Background = new RippleDrawable(ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorControlHighlight))), shape, null);
        card.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));

        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(DirectionTile(t, 40), new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(14) });

        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        string title = TransferTitle(t);
        var name = FolderText(title, 17, Resource.Attribute.colorOnSurface);
        name.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        name.SetSingleLine(true);
        name.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(name);
        var tone = RemoteTransferText.ToneOf(t);
        string status = RemoteTransferText.Status(t, now);
        var statusView = FolderText(status, 14, Resource.Attribute.colorOnSurfaceVariant);
        if (tone is TransferTone.Done or TransferTone.Failed or TransferTone.Paused) statusView.SetTextColor(new Color(ToneColor(tone)));
        statusView.SetSingleLine(true);
        statusView.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(statusView);
        var (source, destination) = RemoteTransferText.Route(t, _phoneHost);
        texts.AddView(RouteRow(source, destination));
        row.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));

        if (CardAction(t) is { } action)
        {
            bool busy = _transferBusy.Contains(t.Id);
            row.AddView(RoundAction(action.Icon, action.Description, action.Primary, !busy, action.Click),
                new LinearLayout.LayoutParams(Dp(40), Dp(40)) { LeftMargin = Dp(10) });
        }
        card.AddView(row, new LinearLayout.LayoutParams(-1, -2));

        LinearProgressIndicator? progress = null;
        if (t.State is "running" or "paused" && t.Bytes > 0)
        {
            var bar = progress = new LinearProgressIndicator(_activity) { Indeterminate = false, Max = 1000, TrackThickness = Dp(4), TrackCornerRadius = Dp(2) };
            bar.SetIndicatorColor(t.State == "paused" ? Attr(Resource.Attribute.colorOutline) : Attr(Resource.Attribute.colorPrimary));
            bar.TrackColor = Attr(Resource.Attribute.colorSurfaceContainerHighest);
            bar.Progress = (int)Math.Min(1000, t.BytesDone * 1000 / Math.Max(1, t.Bytes));
            bar.ImportantForAccessibility = ImportantForAccessibility.No;
            card.AddView(bar, new LinearLayout.LayoutParams(-1, Dp(4)) { TopMargin = Dp(12) });
        }

        card.ContentDescription = title + ", " + status + ", " + source + " to " + destination + ". Details";
        card.Click += (_, _) => ShowTransferDetails(t);
        return new CardRef(card, sig, statusView, progress);
    }

    /// <summary>"Source → Destination" on one line; when it is too long the source is shortened first so the destination stays readable.</summary>
    private View RouteRow(string source, string destination)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var from = FolderText(source, 14, Resource.Attribute.colorOnSurfaceVariant);
        from.SetSingleLine(true);
        from.Ellipsize = TextUtils.TruncateAt.End;
        var arrow = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        arrow.SetImageResource(Resource.Drawable.ic_remote_arrow);
        arrow.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
        var to = FolderText(destination, 14, Resource.Attribute.colorOnSurfaceVariant);
        to.SetSingleLine(true);
        to.Ellipsize = TextUtils.TruncateAt.End;
        row.AddView(from, new LinearLayout.LayoutParams(-2, -2));
        row.AddView(arrow, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { LeftMargin = Dp(6), RightMargin = Dp(6) });
        row.AddView(to, new LinearLayout.LayoutParams(-2, -2));
        // The source gives way first: after layout it may take only what the arrow and the destination leave; a destination
        // that alone is too long is cut with an ellipsis as well, leaving the source a little room.
        row.LayoutChange += (_, _) =>
        {
            if (row.Width <= 0) return;
            int arrowRoom = arrow.Width + Dp(12);
            int destinationWidth = (int)Math.Ceiling(to.Paint!.MeasureText(destination));
            int destinationMax = Math.Min(destinationWidth + 2, Math.Max(Dp(40), row.Width - arrowRoom - Dp(52)));
            int wanted = Math.Max(Dp(40), row.Width - arrowRoom - destinationMax);
            if (to.MaxWidth != destinationMax) to.SetMaxWidth(destinationMax);
            if (from.MaxWidth != wanted) from.SetMaxWidth(wanted);
        };
        row.ContentDescription = source + " to " + destination;
        return row;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Menu, actions, confirm

    private void ShowTransfersMenu(View anchor)
    {
        int ended = _transfers.Count(RemoteTransferText.IsEnded);
        var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetCornerRadius(Dp(8));
        background.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHigh)));
        card.Background = background;
        card.SetPadding(0, Dp(8), 0, Dp(8));
        var popup = new PopupWindow(card, Dp(270), ViewGroup.LayoutParams.WrapContent, true) { OutsideTouchable = true, Elevation = Dp(8) };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));

        void Item(int icon, string text, string? end, bool enabled, Action action)
        {
            var item = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = enabled, Focusable = enabled, Alpha = enabled ? 1f : 0.5f };
            item.SetGravity(GravityFlags.CenterVertical);
            item.Background = new TypedValueHolder(_activity).SelectableBackground();
            item.SetPadding(Dp(16), 0, Dp(16), 0);
            var image = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            image.SetImageResource(icon);
            image.ImageTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurface)));
            item.AddView(image, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(14) });
            item.AddView(FolderText(text, 16, Resource.Attribute.colorOnSurface), new LinearLayout.LayoutParams(0, -2, 1));
            if (end is not null) item.AddView(FolderText(end, 14, Resource.Attribute.colorOnSurfaceVariant));
            item.ContentDescription = text + (end is null ? "" : ", " + end);
            item.Click += (_, _) => { popup.Dismiss(); action(); };
            card.AddView(item, new LinearLayout.LayoutParams(-1, Dp(52)));
        }

        Item(Resource.Drawable.ic_refresh, "Refresh", null, true, () => _ = LoadTransfersAsync());
        Item(Resource.Drawable.ic_remote_clearall, "Clear ended transfers", ended.ToString(), ended > 0, () => _ = TransferActionAsync("", "clear"));
        popup.ShowAsDropDown(anchor, 0, -anchor.Height, GravityFlags.End);
    }

    private void ConfirmCancelTransfer(SiteTransferDto transfer)
    {
        if (!SameHost() || _confirm is not null) return;
        string from = transfer.Direction == "upload" ? (_phoneHost ? "this phone" : _hostName) : transfer.SiteName;
        string to = transfer.Direction == "download" ? (_phoneHost ? "this phone" : _hostName) : transfer.Direction == "upload" ? transfer.SiteName : transfer.DestinationSiteName ?? "";
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Cancel this transfer?")!
            .SetMessage(TransferTitle(transfer) + " · " + from + " to " + to + "\n\nA canceled transfer can't be resumed.")!
            .SetNegativeButton("Keep transfer", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Cancel transfer", (_, _) => _ = TransferActionAsync(transfer.Id, "cancel"))!
            .Show();
        if (_confirm is { } dialog)
        {
            dialog.GetButton(-1)?.SetTextColor(new Color(Attr(Resource.Attribute.colorError)));
            dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
        }
    }

    private async Task TransferActionAsync(string id, string action)
    {
        if (!SameHost() || _transferBusy.Contains(id)) return;
        _transferBusy.Add(id);
        _transferErrorTitle = null;
        UpdateTransfersUi();
        try
        {
            var result = action switch
            {
                "pause" => await _client.PauseTransferAsync(id, _life.Token),
                "resume" => await _client.ResumeTransferAsync(id, _life.Token),
                "retry" => await _client.RetryTransferAsync(id, _life.Token),
                "clear" => await _client.ClearTransferAsync(id, _life.Token),
                _ => await _client.CancelTransferAsync(id, _life.Token)
            };
            if (!SameHost()) return;
            if (!result.Ok)
            {
                string verb = action switch { "pause" => "pause", "resume" => "resume", "retry" => "retry", "clear" => "clear", _ => "cancel" };
                _transferErrorTitle = "Could not " + verb + (action == "clear" ? " the list" : " the transfer");
                _transferErrorText = string.IsNullOrWhiteSpace(result.Message) ? "The Host did not accept this action." : result.Message;
                _transferErrorRetry = false;
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (!SameHost()) return;
            _transferErrorTitle = "Could not confirm the action";
            _transferErrorText = "Refresh to check the transfer's state.";
            _transferErrorRetry = true;
        }
        finally
        {
            _transferBusy.Remove(id);
            if (!_closed && _showTransfers) { UpdateTransfersUi(); _ = LoadTransfersAsync(true); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Details sheet (x2, x2b)

    private View SheetPill(string text, int icon, Action click)
    {
        var pill = new MaterialButton(_activity) { Text = text, CornerRadius = Dp(20) };
        pill.SetAllCaps(false);
        pill.TextSize = 15;
        pill.InsetTop = 0;
        pill.InsetBottom = 0;
        pill.BackgroundTintList = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorSecondaryContainer)));
        pill.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSecondaryContainer)));
        pill.SetIconResource(icon);
        pill.IconTint = ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSecondaryContainer)));
        pill.IconGravity = MaterialButton.IconGravityTextStart;
        pill.IconPadding = Dp(8);
        pill.Click += (_, _) => { var open = _sheet; _sheet = null; open?.Dismiss(); click(); };
        return pill;
    }

    private void ShowTransferDetails(SiteTransferDto t)
    {
        if (!SameHost() || _sheet is not null) return;
        var dialog = NewSheet(out var content);
        var now = DateTimeOffset.Now;
        string title = TransferTitle(t);
        var tone = RemoteTransferText.ToneOf(t);

        var head = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetPadding(Dp(24), 0, Dp(24), Dp(12));
        head.AddView(DirectionTile(t, 48), new LinearLayout.LayoutParams(Dp(48), Dp(48)) { RightMargin = Dp(14) });
        var names = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var heading = FolderText(title, 20, Resource.Attribute.colorOnSurface);
        heading.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        names.AddView(heading);
        var status = FolderText(RemoteTransferText.Status(t, now), 15, Resource.Attribute.colorOnSurfaceVariant);
        if (tone is TransferTone.Done or TransferTone.Failed or TransferTone.Paused) status.SetTextColor(new Color(ToneColor(tone)));
        names.AddView(status);
        head.AddView(names, new LinearLayout.LayoutParams(0, -2, 1));
        content.AddView(head);

        if (RemoteTransferText.Reason(t) is { } reason)
        {
            var banner = TransferBanner(reason.Title, reason.Detail, reason.Warning, t.State == "paused" ? Resource.Drawable.ic_pause : 0);
            content.AddView(banner, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = Dp(24), RightMargin = Dp(24), BottomMargin = Dp(12) });
        }

        var pills = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        pills.SetPadding(Dp(24), 0, Dp(24), Dp(12));
        if (t.State == "failed" && !RemoteTransferText.NeedsAttention(t)) pills.AddView(SheetPill("Retry", Resource.Drawable.ic_refresh, () => _ = TransferActionAsync(t.Id, "retry")), new LinearLayout.LayoutParams(-2, Dp(40)) { RightMargin = Dp(8) });
        if (RemoteTransferText.AttentionOf(t) is var attention && attention != TransferAttention.None)
            pills.AddView(SheetPill(RemoteTransferText.AttentionLabel(attention), AttentionIcon(attention), () => RunAttention(t)), new LinearLayout.LayoutParams(-2, Dp(40)) { RightMargin = Dp(8) });
        if (t.State == "paused") pills.AddView(SheetPill("Resume", Resource.Drawable.ic_play, () => _ = TransferActionAsync(t.Id, "resume")), new LinearLayout.LayoutParams(-2, Dp(40)) { RightMargin = Dp(8) });
        if (t.State == "running") pills.AddView(SheetPill("Pause", Resource.Drawable.ic_pause, () => _ = TransferActionAsync(t.Id, "pause")), new LinearLayout.LayoutParams(-2, Dp(40)) { RightMargin = Dp(8) });
        if (CanShowTransferFolder(t))
        {
            string place = t.Direction == "download" ? "folder" : t.Direction == "upload" ? t.SiteName : t.DestinationSiteName ?? "location";
            pills.AddView(SheetPill("Show in " + place, Resource.Drawable.ic_remote_folder, () => ShowTransferFolder(t, null)), new LinearLayout.LayoutParams(-2, Dp(40)));
        }
        if (pills.ChildCount > 0) content.AddView(pills);

        // From, To, Size, ID.
        var (from, to) = RemoteTransferText.RouteDetail(t, _phoneHost);
        var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var infoShape = new GradientDrawable();
        infoShape.SetCornerRadius(Dp(24));
        infoShape.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHigh)));
        info.Background = infoShape;
        info.SetPadding(Dp(20), Dp(10), Dp(12), Dp(10));
        void Line(string label, string value, bool mono = false, bool copy = false)
        {
            var line = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            line.SetGravity(GravityFlags.CenterVertical);
            line.AddView(FolderText(label, 15, Resource.Attribute.colorOnSurfaceVariant), new LinearLayout.LayoutParams(Dp(92), -2));
            var text = FolderText(value, mono ? 13 : 16, Resource.Attribute.colorOnSurface);
            if (mono) text.Typeface = Typeface.Monospace;
            if (mono) text.SetTextIsSelectable(true);
            line.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
            if (copy)
                line.AddView(IconButton(Resource.Drawable.ic_copy, "Copy transfer ID", () =>
                {
                    if (_activity.GetSystemService(Context.ClipboardService) is Android.Content.ClipboardManager clipboard) clipboard.PrimaryClip = ClipData.NewPlainText("Transfer ID", value.Replace("\n", ""));
                    if (!OperatingSystem.IsAndroidVersionAtLeast(33)) Toast.MakeText(_activity, "Transfer ID copied", ToastLength.Short)?.Show();
                }), new LinearLayout.LayoutParams(Dp(44), Dp(44)));
            line.ContentDescription = label + ", " + value;
            info.AddView(line, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6), BottomMargin = Dp(6) });
        }
        Line("From", from);
        Line("To", to);
        Line("Size", (t.Bytes > 0 ? (t.State == "done" ? Fmt.Bytes(t.Bytes) : RemoteTransferText.Sizes(t)) + " · " : "") + RemoteTransferText.StartedText(t, now));
        Line("Transfer ID", RemoteTransferText.WrapId(t.Id), mono: true, copy: true);
        content.AddView(info, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = Dp(16), RightMargin = Dp(16), BottomMargin = Dp(12) });

        // Files, from sites.transfer.get.
        var filesHost = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        content.AddView(filesHost, new LinearLayout.LayoutParams(-1, -2));
        _ = LoadTransferFilesAsync(dialog, t, filesHost);

        SheetDivider(content);
        if (RemoteTransferText.IsEnded(t))
            SheetRow(content, Resource.Drawable.ic_remote_clearall, "Remove from list", null, () => _ = TransferActionAsync(t.Id, "clear"));
        if (RemoteTransferText.IsActive(t) || t.State == "failed")
            SheetRow(content, Resource.Drawable.ic_close, "Cancel transfer", null, () => ConfirmCancelTransfer(t), danger: true);
        ShowSheet(dialog);
    }

    private async Task LoadTransferFilesAsync(BottomSheetDialog dialog, SiteTransferDto t, LinearLayout host)
    {
        try
        {
#if INBOUND_TEST_BUILD
            var result = FixtureMode is null ? await _client.GetTransferAsync(t.Id, _life.Token) : new SiteTransferResultDto { Ok = true, Items = FixtureItems(t.Id) };
#else
            var result = await _client.GetTransferAsync(t.Id, _life.Token);
#endif
            if (!SameHost() || !dialog.IsShowing || result.Items is not { Count: > 0 } items) return;
            _transferPaths[t.Id] = [.. items.Select(item => item.Path).Take(2000)];
            bool all = false;
            void Fill()
            {
                host.RemoveAllViews();
                var ordered = items.Where(i => !i.IsFolder).OrderBy(i => i.State switch { "failed" => 0, "running" => 1, "done" => 2, "skipped" => 3, _ => 4 }).ToList();
                var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
                header.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
                header.AddView(FolderText("Files", 16, Resource.Attribute.colorOnSurfaceVariant), new LinearLayout.LayoutParams(0, -2, 1));
                if (ordered.Count > 3)
                {
                    var toggle = FolderText(all ? "Fewer" : "All " + ordered.Count, 16, Resource.Attribute.colorPrimary);
                    toggle.Clickable = true;
                    toggle.ContentDescription = all ? "Show fewer files" : "Show all " + ordered.Count + " files";
                    toggle.Click += (_, _) => { all = !all; Fill(); };
                    header.AddView(toggle);
                }
                host.AddView(header);
                foreach (var item in ordered.Take(all ? 300 : 3)) host.AddView(TransferFileRow(t, item));
            }
            Fill();
        }
        catch (Exception) { /* The sheet works without the list. */ }
    }

    private View TransferFileRow(SiteTransferDto t, SiteTransferItemDto item)
    {
        var (name, line, tone) = RemoteTransferText.FileLine(item);
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(24), Dp(4), Dp(16), Dp(4));
        var kind = FileKinds.Of(name);
        int tint = tone switch
        {
            TransferTone.Failed => _activity.GetColor(Resource.Color.state_failed),
            TransferTone.Done => _activity.GetColor(Resource.Color.state_done),
            _ => Attr(Resource.Attribute.colorOnSurfaceVariant),
        };
        var tile = new FrameLayout(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        var shape = new GradientDrawable();
        shape.SetCornerRadius(Dp(10));
        shape.SetColor(new Color(Attr(Resource.Attribute.colorSurfaceContainerHighest)));
        tile.Background = shape;
        var icon = new ImageView(_activity);
        icon.SetImageResource(kind switch
        {
            FileKind.Audio => Resource.Drawable.ic_remote_audio,
            FileKind.Document => Resource.Drawable.ic_remote_document,
            FileKind.Archive => Resource.Drawable.ic_remote_archive,
            FileKind.Image => Resource.Drawable.ic_remote_image,
            FileKind.Video => Resource.Drawable.ic_remote_video,
            _ => FileIcons.For(kind).Drawable,
        });
        icon.ImageTintList = ColorStateList.ValueOf(new Color(tint));
        tile.AddView(icon, new FrameLayout.LayoutParams(Dp(20), Dp(20), GravityFlags.Center));
        row.AddView(tile, new LinearLayout.LayoutParams(Dp(36), Dp(36)) { RightMargin = Dp(14) });
        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var nameView = FolderText(name, 16, Resource.Attribute.colorOnSurface);
        nameView.SetSingleLine(true);
        nameView.Ellipsize = TextUtils.TruncateAt.Middle;
        texts.AddView(nameView);
        var lineView = FolderText(line, 14, Resource.Attribute.colorOnSurfaceVariant);
        if (tone is TransferTone.Failed or TransferTone.Done) lineView.SetTextColor(new Color(tint));
        lineView.SetSingleLine(true);
        lineView.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(lineView);
        row.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        if (item.State == "done" && CanShowTransferFolder(t))
            row.AddView(IconButton(Resource.Drawable.ic_remote_folder, "Show " + name + " in folder", () =>
            {
                var open = _sheet; _sheet = null; open?.Dismiss();
                ShowTransferFolder(t, item.Path);
            }), new LinearLayout.LayoutParams(Dp(44), Dp(44)));
        row.ContentDescription = name + ", " + line;
        return row;
    }

    /// <summary>A filter chip speaks as a toggle button that is checked when it is the shown filter.</summary>
    private sealed class ChipDelegate(bool on) : View.AccessibilityDelegate
    {
        public override void OnInitializeAccessibilityNodeInfo(View host, Android.Views.Accessibility.AccessibilityNodeInfo info)
        {
            base.OnInitializeAccessibilityNodeInfo(host, info);
            info.ClassName = "android.widget.ToggleButton";
            info.Checkable = true;
#pragma warning disable CA1422
            info.Checked = on;
#pragma warning restore CA1422
        }
    }
}
