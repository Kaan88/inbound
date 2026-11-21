using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Inbound.App.Background;
using Inbound.App.Hosts;

namespace Inbound.App.Screens;

/// <summary>The C7 navigation card and the selected Host or phone-local detail pages.</summary>
internal sealed class HostSettingsScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _downloads;
    private readonly QueueSettingsModel _model;
    private readonly CancellationTokenSource _life = new();
    private readonly List<Action> _render = new();
    private readonly List<View> _controls = new();
    private readonly List<View> _categoryRows = new();
    private readonly View _categoryCard;
    private readonly View _torBoxRow = null!;
    private View? _page;
    private TextView? _error;
    private Android.App.Dialog? _dialog;
    private string? _numericId;
    private EditText? _numericInput;
    private long? _numericOriginalSpeed;
    private bool _numericUncertain, _numericCustom;
    private bool _busy, _disposed, _rendering;
    private int _pageGeneration;

    public HostSettingsScreen(MainActivity activity, View parent, DownloadsModel downloads, QueueSettingsModel model)
    {
        _activity = activity; _downloads = downloads; _model = model;
        var container = parent as LinearLayout ?? throw new ArgumentException("Settings content must be a LinearLayout", nameof(parent));
        var card = Card();
        _categoryCard = card;
        var rows = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        rows.SetPadding(0, Dp(8), 0, Dp(8)); card.AddView(rows);
        container.AddView(card, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(16), BottomMargin = Dp(16) });
        NavigationRow(rows, "Downloads", "Folder and connections", Resource.Drawable.ic_flow_folder, () => OpenDetail("downloads"));
        NavigationRow(rows, "Network", "Network rule and speed limit", Resource.Drawable.ic_flow_wifi, () => OpenDetail("network"));
        if (!activity.Capabilities.IsRemote)
        {
            NavigationRow(rows, "Background and battery", "Resume and sleep prevention", Resource.Drawable.ic_flow_battery, () => OpenDetail("background"));
            NavigationRow(rows, "Notifications and sounds", "", Resource.Drawable.ic_flow_bell, () => OpenDetail("notifications"));
        }
        NavigationRow(rows, "Catching links", "Share, browser and clipboard", Resource.Drawable.ic_flow_globe, () => OpenDetail("links"));
        if (!activity.Capabilities.IsRemote)
            NavigationRow(rows, "Site logins", "Manage sign-ins in Remote Files", Resource.Drawable.ic_settings_site_key,
                activity.OpenRemoteFilesSource);
        NavigationRow(rows, "Hosts and web UI", "Select or pair a Host", Resource.Drawable.ic_flow_host, () => OpenDetail("hosts"));
        // Offered only while the shown Host answers remote.* (its hello says so); the hello can come after this screen is built.
        NavigationRow(rows, TorBoxSettingsPage.Title, "TorBox account, sending magnets and extracting archives", Resource.Drawable.ic_flow_bolt, () => activity.OpenTorBoxSettings());
        _torBoxRow = _categoryRows[^1];
        ShowTorBoxRow();
        downloads.HostHelloChanged += ShowTorBoxRow;
        downloads.SettingsChanged += Refresh;
    }

    public string? OpenDetailId { get; private set; }
    private int Dp(int value) => Motion.Dp(_activity, value);
    private int Attr(int value) => Motion.Attr(_activity, value);
    private bool Ready() => !_disposed && !_busy && _downloads.SnapshotShown && _model.CanWriteSafely;
    private MaterialCardView Card()
    {
        var card = new MaterialCardView(_activity) { Radius = Dp(24), CardElevation = 0, StrokeWidth = 0 };
        card.SetCardBackgroundColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer))); return card;
    }
    private TextView Label(string text, float size, int colour)
    {
        var label = new TextView(_activity) { Text = text, TextSize = size };
        label.SetTextColor(new Color(Attr(colour))); return label;
    }
    private void NavigationRow(LinearLayout parent, string title, string subtitle, int icon, Action action)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical); row.SetMinimumHeight(Dp(68)); row.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
        row.Background = new TypedValueHolder(_activity).SelectableBackground();
        var well = new FrameLayout(_activity); well.SetBackgroundResource(Resource.Drawable.bg_circle_dark);
        var image = new ImageView(_activity); image.SetImageResource(icon); image.ImportantForAccessibility = ImportantForAccessibility.No;
        image.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurface)));
        well.AddView(image, new FrameLayout.LayoutParams(Dp(22), Dp(22), GravityFlags.Center));
        row.AddView(well, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { MarginEnd = Dp(16) });
        var text = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        text.AddView(Label(title, 17, Resource.Attribute.colorOnSurface));
        if (subtitle.Length != 0) text.AddView(Label(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        var chevron = new ImageView(_activity); chevron.SetImageResource(Resource.Drawable.ic_chevron_right); chevron.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(chevron, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        row.ContentDescription = title + (subtitle.Length > 0 ? ". " + subtitle : "");
        row.Click += (_, _) => { if (!_disposed) action(); }; parent.AddView(row);
        _categoryRows.Add(row);
    }

    // The page serves both TorBox and archives: it is offered when the shown Host answers either.
    private bool TorBoxOffered => DownloadManager.Mobile.TorBox.TorBoxClient.Offered(_downloads.Hello)
        || DownloadManager.Mobile.Archives.ArchiveClient.Offered(_downloads.Hello);

    private void ShowTorBoxRow()
    {
        if (_disposed) return;
        _torBoxRow.Visibility = TorBoxOffered ? ViewStates.Visible : ViewStates.Gone;
    }

    public void FilterCategories(string query)
    {
        bool any = false;
        foreach (var row in _categoryRows)
        {
            bool matches = row.ContentDescription?.Contains(query, StringComparison.OrdinalIgnoreCase) == true && (row != _torBoxRow || TorBoxOffered);
            row.Visibility = matches ? ViewStates.Visible : ViewStates.Gone;
            any |= matches;
        }
        _categoryCard.Visibility = any ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Called by the activity when restoring a detail page for the same Host.</summary>
    public void OpenDetail(string id)
    {
        if (_disposed || _page is not null || _busy) return;
        if (id is not ("downloads" or "network" or "background" or "notifications" or "links" or "hosts")) return;
        if (_activity.Capabilities.IsRemote && id is ("background" or "notifications")) return;
        if (id is ("downloads" or "network" or "background" or "notifications") && !_downloads.SnapshotShown) return;
        _render.Clear(); _controls.Clear(); _error = null;
        var title = id switch { "downloads" => "Downloads", "network" => "Network", "background" => "Background and battery", "notifications" => "Notifications and sounds", "links" => "Catching links", _ => "Hosts and web UI" };
        var root = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(new Color(Attr(Android.Resource.Attribute.ColorBackground)));
        InsetsListener.Pad(root, top: true, bottom: true);
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical); header.SetPadding(Dp(8), 0, Dp(16), 0);
        var back = new ImageButton(_activity); back.SetImageResource(Resource.Drawable.ic_arrow_back); back.ContentDescription = "Back";
        back.Background = new TypedValueHolder(_activity).SelectableBackground();
        header.AddView(back, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        var heading = Label(title, 24, Resource.Attribute.colorOnSurface); heading.AccessibilityHeading = true;
        header.AddView(heading, new LinearLayout.LayoutParams(0, -2, 1)); root.AddView(header);
        back.Click += (_, _) => _activity.CloseRowPage(root);
        var scroll = new ScrollView(_activity) { FillViewport = true };
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; body.SetPadding(Dp(16), Dp(8), Dp(16), Dp(24));
        scroll.AddView(body); root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        _error = Label("", 14, Resource.Attribute.colorError); _error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _error.Visibility = ViewStates.Gone; _error.TextChanged += (_, _) => { if (_error is { } error) error.Visibility = string.IsNullOrEmpty(error.Text) ? ViewStates.Gone : ViewStates.Visible; };
        body.AddView(_error);
        _page = root; OpenDetailId = id; var generation = ++_pageGeneration;
        switch (id)
        {
            case "downloads": Downloads(body); break;
            case "network": Network(body); break;
            case "background": Background(body); break;
            case "notifications": NotificationsPage(body); break;
            case "links": Links(body, root); break;
            case "hosts": Hosts(body); break;
        }
        if (!_model.CanWriteSafely) Note(body, "Update this Host to edit its settings safely. Current values are read-only.");
        Refresh();
        if (!_activity.OpenHostSettingsPage(root, title, () => PageClosed(generation))) PageClosed(generation);
        else if (_activity.FindViewById<EditText>(Resource.Id.settings_search_input) is { } search)
        {
            search.ClearFocus();
            var keyboard = (Android.Views.InputMethods.InputMethodManager?)_activity.GetSystemService(Context.InputMethodService);
            keyboard?.HideSoftInputFromWindow(search.WindowToken, 0);
        }
    }

    private void PageClosed(int generation)
    {
        if (generation != _pageGeneration) return;
        _dialog?.Dismiss(); _dialog = null; _page = null; OpenDetailId = null; _error = null;
        _render.Clear(); _controls.Clear(); _pageGeneration++;
    }
    private LinearLayout DetailCard(LinearLayout body, string caption)
    {
        if (caption.Length != 0) SectionCaption(body, caption);
        var card = Card(); var list = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        list.SetPadding(0, Dp(4), 0, Dp(4)); card.AddView(list); body.AddView(card, new LinearLayout.LayoutParams(-1, -2)); return list;
    }
    private void SectionCaption(LinearLayout body, string caption)
    {
        var label = Label(caption, 14, Resource.Attribute.colorPrimary);
        body.AddView(label, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(16), BottomMargin = Dp(8) });
    }
    private void Note(LinearLayout body, string text)
    {
        var label = Label(text, 14, Resource.Attribute.colorOnSurfaceVariant);
        body.AddView(label, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(16) });
    }
    private string Target => _activity.Capabilities.IsRemote ? _activity.HostName : "This phone";
    private TextView ValueRow(LinearLayout list, string name, int icon, Action? action, bool settingsWrite = true)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Focusable = action is not null, Clickable = action is not null };
        row.SetGravity(GravityFlags.CenterVertical); row.SetMinimumHeight(Dp(72)); row.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
        if (action is not null) row.Background = new TypedValueHolder(_activity).SelectableBackground();
        var well = new FrameLayout(_activity); well.SetBackgroundResource(Resource.Drawable.bg_circle_dark);
        var image = new ImageView(_activity); image.SetImageResource(icon); image.ImportantForAccessibility = ImportantForAccessibility.No;
        image.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurface)));
        well.AddView(image, new FrameLayout.LayoutParams(Dp(22), Dp(22), GravityFlags.Center));
        row.AddView(well, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { MarginEnd = Dp(16) });
        var text = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        text.AddView(Label(name, 17, Resource.Attribute.colorOnSurface));
        var value = Label("", 14, Resource.Attribute.colorOnSurfaceVariant); text.AddView(value);
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        if (action is not null)
        {
            var arrow = new ImageView(_activity); arrow.SetImageResource(Resource.Drawable.ic_chevron_right); arrow.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(arrow, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
            row.Click += (_, _) => { if (!_disposed && (!settingsWrite || Ready())) action(); };
            if (settingsWrite) _controls.Add(row);
        }
        list.AddView(row); return value;
    }
    private void SwitchRow(LinearLayout list, string name, string subtitle, int icon, Func<SettingsDto, bool> get, Action<SettingsDto, bool> set)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal }; row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(Dp(72)); row.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
        var well = new FrameLayout(_activity); well.SetBackgroundResource(Resource.Drawable.bg_circle_dark);
        var image = new ImageView(_activity); image.SetImageResource(icon); image.ImportantForAccessibility = ImportantForAccessibility.No;
        image.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurface)));
        well.AddView(image, new FrameLayout.LayoutParams(Dp(22), Dp(22), GravityFlags.Center)); row.AddView(well, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { MarginEnd = Dp(16) });
        var text = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; text.AddView(Label(name, 17, Resource.Attribute.colorOnSurface)); text.AddView(Label(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        var toggle = new MaterialSwitch(_activity) { ContentDescription = name }; row.AddView(toggle); list.AddView(row); _controls.Add(toggle);
        _render.Add(() => toggle.Checked = get(_downloads.Settings));
        toggle.CheckedChange += (_, e) => { if (!_rendering && Ready()) _ = SaveAsync(s => set(s, e.IsChecked)); };
    }
    private void NumericRow(LinearLayout list, string id, string name, int icon, Func<SettingsDto, int> get)
    {
        var value = ValueRow(list, name, icon, () => OpenNumberDialog(id));
        _render.Add(() => value.Text = get(_downloads.Settings).ToString(CultureInfo.InvariantCulture));
    }
    private void Downloads(LinearLayout body)
    {
        var list = DetailCard(body, Target);
        var canBrowse = _activity.Capabilities.IsRemote && FolderPicker.Offered(_downloads);
        var folder = ValueRow(list, "Download folder", Resource.Drawable.ic_flow_folder, canBrowse ? () =>
        {
            FolderSheet.Show(_activity, _downloads, _downloads.Settings.DefaultDownloadDirectory, path => { if (Ready() && _page is not null) _ = SaveAsync(s => s.DefaultDownloadDirectory = path); });
        } : null);
        _render.Add(() => folder.Text = _downloads.Settings.DefaultDownloadDirectory);
        NumericRow(list, "max_downloads", "Downloads at once", Resource.Drawable.ic_flow_download, s => s.MaxConcurrentDownloads);
        NumericRow(list, "per_file", "Connections per file", Resource.Drawable.ic_flow_connections, s => s.DefaultConnections);
        NumericRow(list, "per_host", "Connections per host", Resource.Drawable.ic_flow_connections, s => s.MaxConnectionsPerHost);
        Note(body, _activity.Capabilities.IsRemote ? "The selected Host owns these settings." : "Downloads use this phone's shared Download folder. The per-host limit can reduce connections per file.");
    }
    private void Network(LinearLayout body)
    {
        var list = DetailCard(body, Target);
        if (!_activity.Capabilities.IsRemote || _downloads.Hello?.Capabilities.Contains("meteredNetworkRule") == true)
            SwitchRow(list, "Unmetered network only", "Wait on metered connections", Resource.Drawable.ic_flow_wifi, s => s.DownloadOnlyOnUnmeteredNetwork, (s, v) => s.DownloadOnlyOnUnmeteredNetwork = v);
        var value = ValueRow(list, "Overall speed limit", Resource.Drawable.ic_flow_speed, () => OpenNumberDialog("speed"));
        _render.Add(() => value.Text = _downloads.Settings.GlobalSpeedLimitBytesPerSecond is > 0 and var bytes ? Fmt.Rate(bytes) : "None");
        Note(body, "Queue-specific restrictions and limits are in each queue's editor.");
    }
    private void Background(LinearLayout body)
    {
        var list = DetailCard(body, "This phone");
        SwitchRow(list, "Resume on launch", "Resume eligible unfinished downloads", Resource.Drawable.ic_refresh, s => s.AutoResumeUnfinishedOnStartup, (s, v) => s.AutoResumeUnfinishedOnStartup = v);
        SwitchRow(list, "Keep awake while transferring", "CPU and Wi-Fi during active work", Resource.Drawable.ic_flow_bolt, s => s.PreventSleepWhileDownloading, (s, v) => s.PreventSleepWhileDownloading = v);
        var settings = ValueRow(list, "Android battery settings", Resource.Drawable.ic_flow_battery, BatterySettings, settingsWrite: false); settings.Text = "Manage Inbound in system settings";
        Note(body, "Android battery saving and Doze can delay scheduled work. After a force-stop, open Inbound again.");
    }
    private void NotificationsPage(LinearLayout body)
    {
        var list = DetailCard(body, "This phone");
        SwitchRow(list, "Download finished", "Notify when a download completes", Resource.Drawable.ic_flow_bell, s => s.NotifyOnCompletion, (s, v) => s.NotifyOnCompletion = v);
        var sound = ValueRow(list, "Sound and vibration", Resource.Drawable.ic_audio, () => NotificationSettings(channel: true), settingsWrite: false); sound.Text = "Managed by Android";
        var settings = ValueRow(list, "Notification settings", Resource.Drawable.ic_flow_bell, () => NotificationSettings(channel: false), settingsWrite: false); settings.Text = "Permissions and channels";
        Note(body, "If Android notifications are off, enable them in system settings to receive completion alerts.");
    }
    private void Links(LinearLayout body, View root)
    {
        var list = DetailCard(body, "");
        var share = ValueRow(list, "Share to Inbound", Resource.Drawable.ic_flow_globe, null); share.Text = "Use Share in your browser or another app";
        var browser = ValueRow(list, "Built-in browser", Resource.Drawable.ic_flow_globe, () => _activity.OpenBrowserFromSettings(root), settingsWrite: false); browser.Text = "Browse a page and choose its downloads";
        var clip = ValueRow(list, "Clipboard links", Resource.Drawable.ic_paste, null); clip.Text = "Offer a copied link while Inbound is open";
        Note(body, "Clipboard links are not monitored in the background.");
    }
    private void Hosts(LinearLayout body)
    {
        var list = DetailCard(body, "Selected Host");
        var host = ValueRow(list, _activity.HostName, Resource.Drawable.ic_flow_host, null); host.Text = _activity.Capabilities.IsRemote ? "Downloads saved on the selected Host" : "Downloads saved on this device";
        var choose = ValueRow(list, "Switch Host", Resource.Drawable.ic_switch_host, () => HostSwitcherSheet.Show(_activity), settingsWrite: false); choose.Text = "Phone and paired computers";
        var pair = ValueRow(list, "Pair another Host", Resource.Drawable.ic_add, () => PairingSheet.Show(_activity), settingsWrite: false); pair.Text = "Enter a Host address and pair";
        var scope = Card();
        var scopeText = Label("Queue, schedule and download settings follow the selected Host. Appearance, phone battery and Android notifications stay local.",
            14, Resource.Attribute.colorOnSurfaceVariant);
        scopeText.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        scope.AddView(scopeText);
        body.AddView(scope, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(16) });
        SectionCaption(body, "Web interface");
        body.AddView(Label("Manage a web interface on the computer or server that provides it. This phone does not start a web server.",
            14, Resource.Attribute.colorOnSurfaceVariant));
    }

    /// <summary>Capture the open numeric draft before activity recreation; it is never applied by restoring.</summary>
    public void Save(Bundle state)
    {
        state.PutString("host_stamp", HostChoice.Hosts(_activity).Current.ConnectionStamp);
        state.PutString("numeric_id", _numericId);
        if (_numericInput is { } input)
        {
            state.PutString("numeric_text", input.Text);
            state.PutInt("numeric_selection_start", input.SelectionStart);
            state.PutInt("numeric_selection_end", input.SelectionEnd);
        }
        state.PutBoolean("numeric_custom", _numericCustom);
        state.PutBoolean("numeric_original_speed_present", _numericOriginalSpeed.HasValue);
        if (_numericOriginalSpeed is { } speed) state.PutLong("numeric_original_speed", speed);
        // A request may have committed even if its reply was lost during recreation.
        state.PutBoolean("uncertain", _busy || _numericUncertain);
    }

    /// <summary>Call after reopening the recorded detail page, only for the same selected Host.</summary>
    public void Restore(Bundle state)
    {
        if (_disposed || _page is null || state.GetString("host_stamp") != HostChoice.Hosts(_activity).Current.ConnectionStamp) return;
        var id = state.GetString("numeric_id");
        if (id is not null)
        {
            var originalSpeed = state.GetBoolean("numeric_original_speed_present") ? state.GetLong("numeric_original_speed") : (long?)null;
            OpenNumberDialog(id, state.GetString("numeric_text") ?? "", state.GetInt("numeric_selection_start", -1), state.GetInt("numeric_selection_end", -1), state.GetBoolean("uncertain"), originalSpeed, restored: true, customSpeed: state.GetBoolean("numeric_custom"));
        }
        else if (state.GetBoolean("uncertain") && _error is { } error)
            error.Text = "A setting may have been saved before the connection closed. Check the Host's current value before changing it again.";
    }

    private void OpenNumberDialog(string id, string? draft = null, int selectionStart = -1, int selectionEnd = -1, bool uncertain = false, long? originalSpeed = null, bool restored = false, bool customSpeed = false)
    {
        if (_disposed || _page is null || _busy || _dialog is not null) return;
        var settings = _downloads.Settings;
        string title; string initial; long min = 1, max; bool allowEmpty = false;
        Func<long?, Action<SettingsDto>> edit;
        switch (id)
        {
            case "max_downloads" when OpenDetailId == "downloads":
                title = "Downloads at once"; initial = settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture); max = 16;
                edit = value => s => s.MaxConcurrentDownloads = (int)value!.Value; break;
            case "per_file" when OpenDetailId == "downloads":
                title = "Connections per file"; initial = settings.DefaultConnections.ToString(CultureInfo.InvariantCulture); max = 32;
                edit = value => s => s.DefaultConnections = (int)value!.Value; break;
            case "per_host" when OpenDetailId == "downloads":
                title = "Connections per host"; initial = settings.MaxConnectionsPerHost.ToString(CultureInfo.InvariantCulture); max = 32;
                edit = value => s => s.MaxConnectionsPerHost = (int)value!.Value; break;
            case "speed" when OpenDetailId == "network":
                title = "Speed limit"; max = long.MaxValue / Fmt.BytesPerKilobyte; allowEmpty = true;
                var bytes = restored ? originalSpeed : settings.GlobalSpeedLimitBytesPerSecond;
                initial = bytes is > 0 ? Math.Max(1, bytes.Value / Fmt.BytesPerKilobyte + (bytes.Value % Fmt.BytesPerKilobyte == 0 ? 0 : 1)).ToString(CultureInfo.InvariantCulture) : "";
                // A rounded legacy value may be one step above the largest newly representable whole-KB value.
                if (bytes is > 0) max = Math.Max(max, long.Parse(initial, CultureInfo.InvariantCulture));
                edit = value => s => s.GlobalSpeedLimitBytesPerSecond = value is null ? null : bytes is > 0 && value.Value.ToString(CultureInfo.InvariantCulture) == initial ? bytes : value.Value * Fmt.BytesPerKilobyte;
                originalSpeed = bytes; if (!restored) customSpeed = bytes is > 0; break;
            default: return;
        }
        NumberDialog(id, title, draft ?? initial, min, max, allowEmpty, edit, selectionStart, selectionEnd, uncertain, originalSpeed, customSpeed);
    }

    private void NumberDialog(string id, string title, string initial, long min, long max, bool allowEmpty, Func<long?, Action<SettingsDto>> edit,
        int selectionStart, int selectionEnd, bool uncertain, long? originalSpeed, bool customSpeed)
    {
        var generation = _pageGeneration;
        var content = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; content.SetPadding(Dp(24), Dp(8), Dp(24), Dp(16));
        RadioButton? none = null, custom = null;
        if (id == "speed")
        {
            var choices = new RadioGroup(_activity) { Orientation = Orientation.Vertical };
            none = new RadioButton(_activity) { Text = "None", ContentDescription = "No overall speed limit" };
            custom = new RadioButton(_activity) { Text = "Custom limit", ContentDescription = "Custom overall speed limit" };
            choices.AddView(none); choices.AddView(custom); content.AddView(choices);
        }
        var input = new EditText(_activity) { Text = initial, InputType = Android.Text.InputTypes.ClassNumber, ContentDescription = id == "speed" ? "Speed limit in KB per second" : title };
        if (id == "speed") input.Hint = "KB/s";
        input.SetSingleLine(true); content.AddView(input);
        if (custom is not null && none is not null)
        {
            _numericCustom = customSpeed; custom.Checked = customSpeed; none.Checked = !customSpeed; input.Enabled = customSpeed;
            custom.CheckedChange += (_, e) => { if (_disposed || generation != _pageGeneration) return; _numericCustom = e.IsChecked; input.Enabled = e.IsChecked && !_busy; };
            var note = Label("Shared by the Host's HTTP downloads.", 14, Resource.Attribute.colorOnSurfaceVariant); content.AddView(note);
        }
        if (selectionStart >= 0 && selectionEnd >= 0) input.SetSelection(Math.Clamp(selectionStart, 0, initial.Length), Math.Clamp(selectionEnd, 0, initial.Length));
        var error = Label("", 14, Resource.Attribute.colorError); error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite; content.AddView(error);
        var buttons = new LinearLayout(_activity) { Orientation = Orientation.Horizontal }; buttons.SetGravity(GravityFlags.End);
        var cancel = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle) { Text = "Cancel" }; var save = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle) { Text = "Apply" };
        buttons.AddView(cancel); buttons.AddView(save); content.AddView(buttons);
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle(title)!.SetView(content)!.Create()!; _dialog = dialog;
        _numericId = id; _numericInput = input; _numericOriginalSpeed = originalSpeed; _numericUncertain = uncertain;
        if (uncertain)
        {
            error.Text = "The Host may have saved this value. Close and check its current value before changing it again.";
            save.Enabled = false;
        }
        cancel.Click += (_, _) => { if (!_busy && !_disposed && generation == _pageGeneration) dialog.Dismiss(); };
        save.Click += async (_, _) =>
        {
            if (!Ready() || _numericUncertain || generation != _pageGeneration) return;
            var text = (input.Text ?? "").Trim(); long? value = null;
            if (!(allowEmpty && !_numericCustom))
            {
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
                { error.Text = $"Enter a number from {min} to {max}."; return; }
                value = parsed;
            }
            save.Enabled = cancel.Enabled = input.Enabled = false;
            if (none is not null) none.Enabled = false;
            if (custom is not null) custom.Enabled = false;
            dialog.SetCancelable(false); error.Text = "";
            var result = await SaveAsync(edit(value));
            if (_disposed || generation != _pageGeneration || _dialog != dialog) return;
            dialog.SetCancelable(true); cancel.Enabled = true;
            if (none is not null) none.Enabled = true;
            if (custom is not null) custom.Enabled = true;
            input.Enabled = id != "speed" || _numericCustom;
            if (result.Ok) dialog.Dismiss();
            else if (_model.LastResult is null)
            {
                _numericUncertain = true; save.Enabled = false;
                error.Text = "The Host did not confirm this change. Close and check its current value before trying again.";
            }
            else { save.Enabled = true; error.Text = result.Error ?? "Could not save this setting."; }
        };
        dialog.DismissEvent += (_, _) =>
        {
            if (_dialog != dialog) return;
            _dialog = null; _numericId = null; _numericInput = null; _numericOriginalSpeed = null; _numericUncertain = false; _numericCustom = false;
        };
        dialog.SetCanceledOnTouchOutside(false); dialog.Show();
    }
    private async Task<CommandResult> SaveAsync(Action<SettingsDto> edit)
    {
        if (!Ready()) return CommandResult.Fail("Settings are not ready.");
        var generation = _pageGeneration;
        _busy = true; if (_error is { } error) error.Text = ""; Refresh();
        try
        {
            var result = await _model.UpdateSettingAsync(edit, _life.Token);
            if (_disposed) return result;
            if (result.Ok && _model.LastResult is { } settings) _downloads.ApplySettingsResult(settings);
            else if (!result.Ok && generation == _pageGeneration && _error is { } current) current.Text = result.Error ?? "Could not save this setting.";
            return result;
        }
        catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { return CommandResult.Fail("Cancelled."); }
        finally { if (!_disposed) { _busy = false; Refresh(); } }
    }
    public void Refresh()
    {
        if (_disposed) return; _rendering = true;
        try { foreach (var control in _controls) control.Enabled = Ready(); foreach (var render in _render) render(); }
        finally { _rendering = false; }
    }
    private void BatterySettings()
    {
        try { _activity.StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings)); }
        catch (ActivityNotFoundException)
        {
            try { _activity.StartActivity(new Intent(Settings.ActionApplicationDetailsSettings, Android.Net.Uri.Parse("package:" + _activity.PackageName))); }
            catch (ActivityNotFoundException) { if (_error is { } error) error.Text = "Battery settings are unavailable on this device."; }
        }
    }
    private void NotificationSettings(bool channel)
    {
        try
        {
            Notifications.EnsureChannels(_activity);
            var intent = new Intent(channel ? Settings.ActionChannelNotificationSettings : Settings.ActionAppNotificationSettings);
            intent.PutExtra(Settings.ExtraAppPackage, _activity.PackageName);
            if (channel) intent.PutExtra(Settings.ExtraChannelId, Notifications.FinishedChannel);
            _activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException) { if (_error is { } error) error.Text = "Notification settings are unavailable on this device."; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _downloads.SettingsChanged -= Refresh; _downloads.HostHelloChanged -= ShowTorBoxRow; _life.Cancel(); _dialog?.Dismiss(); _dialog = null; _life.Dispose();
        _render.Clear(); _controls.Clear(); _page = null; OpenDetailId = null;
    }
}
