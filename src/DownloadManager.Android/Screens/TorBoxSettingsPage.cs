using Android.Graphics;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Archives;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.MaterialSwitch;

namespace Inbound.App.Screens;

/// <summary>
/// "Torrents, TorBox, archives" (t7). The Torrents group first (when the Host answers torrent.*): Download torrents here, Seeding and
/// Connections and limits, which are the Host's own torrent settings. The TorBox group (when the Host offers TorBox): the Account row (the key is entered in a masked sheet
/// and never shown again) and "Send magnets to TorBox first". The Archives group (when it offers extraction): Extract when finished and
/// Delete the archive after extracting. Saved passwords is the Host's list.
/// </summary>
internal sealed class TorBoxSettingsPage
{
    public const string Title = "Torrents, TorBox, archives";
    private readonly MainActivity _activity;
    private readonly TorBoxClient? _client;
    private readonly ArchiveClient? _archives;
    private readonly TorBoxChoice _choice;
    private TextView? _status;
    private MaterialSwitch? _extractSwitch, _deleteSwitch;
    private TextView? _toolNote, _savedLine;
    private readonly DownloadsModel _downloads;
    private readonly QueueSettingsModel _settings;
    private MaterialSwitch? _hereSwitch, _wifiSwitch;
    private TextView? _hereSub, _seedingSub, _connectionsSub;
    private bool _hasKey, _disposed, _settingArchives, _renderingTorrents, _savingTorrents;

    /// <param name="client">Null when the Host does not offer TorBox: the TorBox group is left out.</param>
    /// <param name="archives">Null when the Host does not offer extraction: the Archives group is left out.</param>
    /// <param name="torrentsOffered">The Host answers torrent.*: the Torrents group is shown.</param>
    public TorBoxSettingsPage(MainActivity activity, TorBoxClient? client, ArchiveClient? archives, TorBoxChoice choice, bool torrentsOffered,
        DownloadsModel downloads, QueueSettingsModel settings)
    {
        _activity = activity;
        _client = client;
        _archives = archives;
        _choice = choice;
        _downloads = downloads;
        _settings = settings;
        var dp = (int d) => Motion.Dp(activity, d);
        int Attr(int a) => Motion.Attr(activity, a);

        Root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        Root.SetBackgroundColor(new Color(Attr(Android.Resource.Attribute.ColorBackground)));
        InsetsListener.Pad(Root, top: true, bottom: true);
        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(dp(8), 0, dp(16), 0);
        var back = new ImageButton(activity) { ContentDescription = "Back" };
        back.SetImageResource(Resource.Drawable.ic_arrow_back);
        back.Background = new TypedValueHolder(activity).SelectableBackground();
        header.AddView(back, new LinearLayout.LayoutParams(dp(48), dp(48)));
        var heading = new TextView(activity) { Text = Title, TextSize = 24, AccessibilityHeading = true };
        heading.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
        header.AddView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        Root.AddView(header);
        back.Click += (_, _) => _activity.CloseRowPage(Root);

        var scroll = new ScrollView(activity) { FillViewport = true };
        var body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        body.SetPadding(dp(16), dp(8), dp(16), dp(24));
        scroll.AddView(body);
        Root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        if (torrentsOffered) BuildTorrents(body);
        if (client is not null) BuildTorBox(body);
        if (archives is not null) BuildArchives(body);
    }

    private LinearLayout Group(LinearLayout body, string title)
    {
        var dp = (int d) => Motion.Dp(_activity, d);
        var caption = new TextView(_activity) { Text = title, TextSize = 14 };
        caption.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorPrimary)));
        body.AddView(caption, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(16), BottomMargin = dp(8) });
        var card = new MaterialCardView(_activity) { Radius = dp(24), CardElevation = 0, StrokeWidth = 0 };
        card.SetCardBackgroundColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));
        var rows = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        rows.SetPadding(0, dp(4), 0, dp(4));
        card.AddView(rows);
        body.AddView(card, new LinearLayout.LayoutParams(-1, -2));
        return rows;
    }

    private MaterialSwitch SwitchRow(LinearLayout rows, string title, string? subtitle, bool on)
    {
        Row(rows, title, out var sub, out var row);
        if (subtitle is null) sub.Visibility = ViewStates.Gone; else sub.Text = subtitle;
        var toggle = new MaterialSwitch(_activity) { Checked = on, ContentDescription = title };
        row.AddView(toggle);
        row.Clickable = true;
        row.Focusable = true;
        row.Background = new TypedValueHolder(_activity).SelectableBackground();
        row.Click += (_, _) => { if (toggle.Enabled) toggle.Toggle(); };
        return toggle;
    }

    /// <summary>
    /// The Torrents group (t7). All three rows are the Host's settings, so the title names the Host: "this phone" only when it is the phone.
    /// The "Wi-Fi only" switch and the "Wi-Fi and charging" words are pipe-only Host settings, so a paired PC or server does not show them.
    /// </summary>
    private void BuildTorrents(LinearLayout body)
    {
        var rows = Group(body, TorrentText.GroupTitle(!_activity.Capabilities.IsRemote, _activity.HostName));
        Row(rows, "Download torrents here", out _hereSub, out var hereRow);
        _hereSwitch = new MaterialSwitch(_activity) { ContentDescription = "Download torrents here" };
        hereRow.AddView(_hereSwitch);
        hereRow.Clickable = true;
        hereRow.Focusable = true;
        hereRow.Background = new TypedValueHolder(_activity).SelectableBackground();
        hereRow.Click += (_, _) => { if (_hereSwitch.Enabled) _hereSwitch.Toggle(); };
        _hereSwitch.CheckedChange += (_, e) =>
        {
            if (_renderingTorrents || _savingTorrents) return;
            SaveTorrents(s => s.TorrentsEnabled = e.IsChecked);
        };
        if (!_activity.Capabilities.IsRemote)
        {
            // Pipe-only on the Host, so a paired PC or server does not show this row.
            Row(rows, TorrentText.WifiOnlyTitle, out var wifiSub, out var wifiRow);
            wifiSub.Text = TorrentText.WifiOnlySubtitle;
            _wifiSwitch = new MaterialSwitch(_activity) { ContentDescription = TorrentText.WifiOnlyTitle };
            wifiRow.AddView(_wifiSwitch);
            wifiRow.Clickable = true;
            wifiRow.Focusable = true;
            wifiRow.Background = new TypedValueHolder(_activity).SelectableBackground();
            wifiRow.Click += (_, _) => { if (_wifiSwitch.Enabled) _wifiSwitch.Toggle(); };
            _wifiSwitch.CheckedChange += (_, e) =>
            {
                if (_renderingTorrents || _savingTorrents) return;
                SaveTorrents(s => s.TorrentOnlyOnUnmeteredNetwork = e.IsChecked);
            };
        }

        NavRow(rows, "Seeding", out _seedingSub, () => TorrentSettingsSheets.ShowSeeding(_activity, _downloads, _settings));
        NavRow(rows, "Connections and limits", out _connectionsSub, () => TorrentSettingsSheets.ShowConnections(_activity, _downloads, _settings));
        _downloads.SettingsChanged += RenderTorrents;
        RenderTorrents();
    }

    /// <summary>Draws the rows from the Host's settings as the list last had them; also after a change was refused, so the switch falls back.</summary>
    private void RenderTorrents()
    {
        if (_disposed || _hereSwitch is null) return;
        var settings = _downloads.Settings;
        _renderingTorrents = true;
        _hereSwitch.Checked = settings.TorrentsEnabled;
        _renderingTorrents = false;
        _hereSwitch.Enabled = !_savingTorrents && _downloads.SnapshotShown && _settings.CanWriteSafely;
        if (_wifiSwitch is not null)
        {
            _renderingTorrents = true;
            _wifiSwitch.Checked = settings.TorrentOnlyOnUnmeteredNetwork;
            _renderingTorrents = false;
            _wifiSwitch.Enabled = _hereSwitch.Enabled;
        }

        _hereSub!.Text = TorrentText.DownloadHereSubtitle(settings.TorrentsEnabled, _client is not null);
        _seedingSub!.Text = TorrentText.SeedingSubtitle(settings, _activity.Capabilities.IsRemote);
        _connectionsSub!.Text = TorrentText.ConnectionsSubtitle(settings);
    }

    private void SaveTorrents(Action<SettingsDto> edit)
    {
        _savingTorrents = true;
        _hereSwitch!.Enabled = false;
        if (_wifiSwitch is not null) _wifiSwitch.Enabled = false;
        TorBoxUi.Run(_activity, () => _settings.UpdateSettingAsync(edit), result =>
        {
            if (_disposed) return;
            _savingTorrents = false;
            if (result.Ok && _settings.LastResult is { } latest) _downloads.ApplySettingsResult(latest);
            else if (!result.Ok) _activity.ShowMessage(string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept that setting." : result.Error.Trim());
            RenderTorrents();
        }, words =>
        {
            if (_disposed) return;
            _savingTorrents = false;
            _activity.ShowMessage(words);
            RenderTorrents();
        });
    }

    /// <summary>A row with a chevron that opens something (a sheet).</summary>
    private void NavRow(LinearLayout rows, string title, out TextView subtitle, Action open)
    {
        Row(rows, title, out subtitle, out var row);
        var chevron = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
        chevron.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        row.AddView(chevron, new LinearLayout.LayoutParams(Motion.Dp(_activity, 20), Motion.Dp(_activity, 20)));
        row.Clickable = true;
        row.Focusable = true;
        row.Background = new TypedValueHolder(_activity).SelectableBackground();
        row.Click += (_, _) => open();
    }

    private void BuildTorBox(LinearLayout body)
    {
        var activity = _activity;
        var dp = (int d) => Motion.Dp(activity, d);
        var rows = Group(body, "TorBox");

        // Account: the words say whether a key is saved; Change opens the sheet.
        Row(rows, "Account", out _status, out var accountRow);
        _status.Text = "Checking…";
        var change = new MaterialButton(activity, null, Resource.Attribute.materialButtonTonalStyle) { Text = "Change", ContentDescription = "Change the TorBox account key" };
        change.SetAllCaps(false);
        accountRow.AddView(change, new LinearLayout.LayoutParams(-2, dp(44)));
        change.Click += (_, _) => TorBoxKeySheet.Show(_activity, _client!, _hasKey, _activity.Capabilities.IsRemote ? _activity.HostName : "", Load);

        // Send magnets to TorBox first: kept on this device and read by Add torrent.
        Row(rows, "Send magnets to TorBox first", out var sub, out var firstRow);
        sub.Text = "Add torrent opens with TorBox chosen";
        var toggle = new MaterialSwitch(activity) { Checked = _choice.SendFirst, ContentDescription = "Send magnets to TorBox first" };
        firstRow.AddView(toggle);
        toggle.CheckedChange += (_, e) => _choice.SendFirst = e.IsChecked;
        firstRow.Clickable = true;
        firstRow.Focusable = true;
        firstRow.Background = new TypedValueHolder(activity).SelectableBackground();
        firstRow.Click += (_, _) => toggle.Toggle();
        Load();
    }

    /// <summary>The Archives group (t7): both switches are the Host's settings, read and written through archive.settings.*.</summary>
    private void BuildArchives(LinearLayout body)
    {
        var rows = Group(body, "Archives");
        _extractSwitch = SwitchRow(rows, "Extract when finished", "zip, rar, 7z, tar and split sets", false);
        _deleteSwitch = SwitchRow(rows, "Delete the archive after extracting", null, false);
        Row(rows, "Saved passwords", out _savedLine, out var savedRow);
        _savedLine.Text = "…";
        var chevron = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
        chevron.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        savedRow.AddView(chevron, new LinearLayout.LayoutParams(Motion.Dp(_activity, 20), Motion.Dp(_activity, 20)));
        savedRow.Clickable = true;
        savedRow.Focusable = true;
        savedRow.Background = new TypedValueHolder(_activity).SelectableBackground();
        savedRow.Click += (_, _) => SavedPasswordsSheet.Show(_activity, _archives!, count => { if (!_disposed) _savedLine!.Text = ArchiveClient.SavedPasswordsLine(count); });
        _extractSwitch.Enabled = _deleteSwitch.Enabled = false;
        _toolNote = new TextView(_activity) { TextSize = 14, Visibility = ViewStates.Gone };
        _toolNote.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        body.AddView(_toolNote, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 8), LeftMargin = Motion.Dp(_activity, 16), RightMargin = Motion.Dp(_activity, 16) });
        _extractSwitch.CheckedChange += (_, e) => { if (!_settingArchives) SaveArchive(new ArchiveSettingsUpdate { AutoExtract = e.IsChecked }); };
        _deleteSwitch.CheckedChange += (_, e) => { if (!_settingArchives) SaveArchive(new ArchiveSettingsUpdate { DeleteArchives = e.IsChecked }); };
        LoadArchives(() => _archives!.SettingsAsync());
    }

    private void SaveArchive(ArchiveSettingsUpdate update)
    {
        var client = _archives!;
        _extractSwitch!.Enabled = _deleteSwitch!.Enabled = false;
        LoadArchives(() => client.UpdateSettingsAsync(update));
    }

    /// <summary>Shows what the Host answered, for a read and for a change alike: after a refused change the switches fall back to the Host's own values.</summary>
    private void LoadArchives(Func<Task<ArchiveSettingsResult>> request)
    {
        TorBoxUi.Run(_activity, request, result =>
        {
            if (_disposed) return;
            _settingArchives = true;
            _extractSwitch!.Checked = result.Settings.AutoExtract;
            _deleteSwitch!.Checked = result.Settings.DeleteArchives;
            _settingArchives = false;
            _savedLine!.Text = ArchiveClient.SavedPasswordsLine(result.Settings.SavedPasswords);
            var found = result.Tool.Found;
            _extractSwitch.Enabled = _deleteSwitch.Enabled = found;
            _toolNote!.Text = result.Tool.Message;
            _toolNote.Visibility = !found && !string.IsNullOrWhiteSpace(result.Tool.Message) ? ViewStates.Visible : ViewStates.Gone;
            if (!result.Ok) _activity.ShowMessage(string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept that setting." : result.Error.Trim());
        }, words =>
        {
            if (_disposed) return;
            // Unknown after a failure: leave the switches off and tell the person; reopening the page reads them again.
            _extractSwitch!.Enabled = _deleteSwitch!.Enabled = false;
            _activity.ShowMessage(words);
        });
    }

    public LinearLayout Root { get; }

    public void Dispose()
    {
        _disposed = true;
        _downloads.SettingsChanged -= RenderTorrents;
    }

    private TextView Row(LinearLayout parent, string title, out TextView subtitle, out LinearLayout row)
    {
        var dp = (int d) => Motion.Dp(_activity, d);
        row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(dp(72));
        row.SetPadding(dp(16), dp(8), dp(16), dp(8));
        var text = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var name = new TextView(_activity) { Text = title, TextSize = 17 };
        name.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        subtitle = new TextView(_activity) { TextSize = 14 };
        subtitle.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        text.AddView(name);
        text.AddView(subtitle);
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        parent.AddView(row);
        return name;
    }

    private void Load()
    {
        TorBoxUi.Run(_activity, () => _client!.AccountAsync(), result =>
        {
            if (_disposed) return;
            var account = result.Ok ? result.Accounts.FirstOrDefault() : null;
            if (account is null) { _status!.Text = result.Ok ? "No TorBox account on this Host" : TorBoxText.Failure(result.Error, result.FailureKind); return; }
            _hasKey = account is { HasKey: true };
            _status!.Text = TorBoxText.KeyStatus(account.HasKey, account.KeyUnreadable, !_activity.Capabilities.IsRemote, _activity.HostName);
        }, words => { if (!_disposed) _status!.Text = words; });
    }
}
