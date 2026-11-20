using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

internal sealed class QueueEditorScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _downloads;
    private readonly QueueSettingsModel _settings;
    private readonly CancellationTokenSource _life = new();
    private readonly LinearLayout _content, _daysRow, _times;
    private readonly TextView _title, _error, _scheduleLabel, _startText, _stopText, _concurrencyText, _speedText;
    private readonly TextView? _completionText;
    private readonly MaterialButton _save;
    private readonly MaterialSwitch _unmetered, _charging, _battery;
    private readonly MaterialButton[] _days;
    private readonly bool _conditions, _speedSupported, _completionSupported;
    private QueueDto? _baseline;
    private QueueDto _draft;
    private List<ScheduleDto> _originalSchedules, _draftSchedules;
    private Guid? _selectedId;
    private bool _closed, _busy, _uncertain, _refreshing;
    private readonly List<Android.App.Dialog> _dialogs = [];
    private readonly List<(TextView Value, View Row, string Title)> _valueRows = [];
    private Android.App.TimePickerDialog? _timeDialog;
    private Guid? _timeSchedule;
    private bool _timeStop;
    private string? _inputTitle, _inputText;
    private Android.App.Dialog? _speedDialog;
    private EditText? _speedInput;
    private bool _speedCustom, _reviewAfterDialog;
    private Guid? _inputScheduleId;
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static T Clone<T>(T value) => JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)IpcJsonContext.Default.GetTypeInfo(typeof(T))!), (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)IpcJsonContext.Default.GetTypeInfo(typeof(T))!)!;
    private static string QueueJson(QueueDto q) => JsonSerializer.Serialize(q, IpcJsonContext.Default.QueueDto);
    private static string ScheduleJson(ScheduleDto s) => JsonSerializer.Serialize(s, IpcJsonContext.Default.ScheduleDto);
    private bool CanEditSafely => _settings.CanEditQueuesSafely && (_baseline is null || _baseline.Generation is { } generation && generation != Guid.Empty);
    private ScheduleDto? Selected => _draftSchedules.FirstOrDefault(s => s.Id == _selectedId);

    public QueueEditorScreen(MainActivity activity, DownloadsModel downloads, QueueSettingsModel settings, QueueDto? queue, Bundle? saved = null)
    {
        _activity = activity; _downloads = downloads; _settings = settings;
        _baseline = saved?.GetString("baseline") is { } original ? JsonSerializer.Deserialize(original, IpcJsonContext.Default.QueueDto) : queue is null ? null : Clone(queue);
        _draft = saved?.GetString("draft") is { } draft ? JsonSerializer.Deserialize(draft, IpcJsonContext.Default.QueueDto)! : queue is null ? new QueueDto { Name = "" } : Clone(queue);
        _originalSchedules = saved?.GetString("original_schedules") is { } originals ? JsonSerializer.Deserialize(originals, IpcJsonContext.Default.SettingsDto)!.Schedules : queue is null ? [] : settings.Settings.Schedules.Where(s => s.QueueId == queue.Id).Select(Clone).ToList();
        if (saved is null) foreach (var schedule in _originalSchedules) NormalizeHostDays(schedule);
        _draftSchedules = saved?.GetString("draft_schedules") is { } drafts ? JsonSerializer.Deserialize(drafts, IpcJsonContext.Default.SettingsDto)!.Schedules : _originalSchedules.Select(Clone).ToList();
        _selectedId = Guid.TryParse(saved?.GetString("selected"), out var selected) ? selected : _draftSchedules.Count == 1 ? _draftSchedules[0].Id : null;
        _uncertain = saved?.GetBoolean("uncertain") == true;
        if (queue is null && saved is null && !activity.Capabilities.IsRemote) _draft.NotifyWhenFinished = true;
        _completionSupported = !activity.Capabilities.IsRemote || downloads.Hello?.Capabilities.Contains("queueCompletionNotification") == true;
        _conditions = !activity.Capabilities.IsRemote || downloads.Hello?.Capabilities.Contains("queueConditions") == true;
        _speedSupported = !activity.Capabilities.IsRemote || downloads.Hello?.Capabilities.Contains("queueSpeedLimit") == true;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.queue_editor, null, false)!;
        InsetsListener.Pad(Root, top: true, bottom: true);
        _title = Root.FindViewById<TextView>(Resource.Id.queue_editor_title)!;
        _content = Root.FindViewById<LinearLayout>(Resource.Id.queue_editor_content)!;
        _save = Root.FindViewById<MaterialButton>(Resource.Id.queue_editor_save)!;
        Root.FindViewById<View>(Resource.Id.queue_editor_back)!.Click += (_, _) => RequestClose();
        Root.FindViewById<View>(Resource.Id.queue_editor_more)!.Click += (_, _) => Options();
        _save.Click += async (_, _) => await SaveAsync();
        _error = Text("", 14, Resource.Attribute.colorError); _error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _error.TextChanged += (_, _) => _error.Visibility = string.IsNullOrEmpty(_error.Text) ? ViewStates.Gone : ViewStates.Visible;
        _content.AddView(_error);
        _scheduleLabel = Text("", 13, Resource.Attribute.colorOnSurfaceVariant);
        _scheduleLabel.Clickable = true; _scheduleLabel.Focusable = true; _scheduleLabel.Click += (_, _) => ScheduleList();
        _content.AddView(_scheduleLabel);
        _times = new LinearLayout(activity) { Orientation = Orientation.Horizontal }; _content.AddView(_times, Margin(16));
        _startText = TimeTile("Start", false); _stopText = TimeTile("Stop", true);
        _daysRow = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        _daysRow.SetGravity(GravityFlags.CenterVertical);
        _daysRow.SetMinimumHeight(Motion.Dp(activity, 48));
        _content.AddView(_daysRow, Margin(12));
        _days = DayNames.Select((day, i) =>
        {
            var b = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = day[..1], Checkable = true, ContentDescription = day, Id = DayResourceId(Array.IndexOf(DayNames, day)), CornerRadius = Motion.Dp(activity, 24) };
            b.InsetTop = 0; b.InsetBottom = 0;
            b.SetMinWidth(0); b.SetMinimumWidth(0); b.SetPadding(0, 0, 0, 0);
            b.Click += (_, _) => { if (!_refreshing && !_busy && Selected is { } s && Supported(s)) { var set = Days(s); if (b.Checked) set.Add(day); else set.Remove(day); s.Days = ScheduleDays.Serialize(set); PaintDays(); } };
            if (i > 0) _daysRow.AddView(new Space(activity), new LinearLayout.LayoutParams(0, 1, 1));
            var diameter = Motion.Dp(activity, 44);
            _daysRow.AddView(b, new LinearLayout.LayoutParams(diameter, diameter));
            return b;
        }).ToArray();
        _daysRow.LayoutChange += (_, _) =>
        {
            // Keep equal gaps and fit all seven buttons, even on a narrow screen.
            int size = Math.Min(Motion.Dp(activity, 44), _daysRow.Width / _days.Length);
            if (size <= 0) return;
            foreach (var day in _days)
                if (day.LayoutParameters?.Width != size)
                    day.LayoutParameters = new LinearLayout.LayoutParams(size, size);
        };
        Section("Only when").Visibility = _conditions ? ViewStates.Visible : ViewStates.Gone;
        _unmetered = Condition("Unmetered network", "Wait on metered connections", Resource.Drawable.ic_flow_wifi, _draft.RequireUnmeteredNetwork == true, v => _draft.RequireUnmeteredNetwork = v);
        _unmetered.Id = Resource.Id.queue_editor_unmetered;
        _charging = Condition("Charging", activity.Capabilities.IsRemote ? "Wait until plugged in" : "Waits until the phone is plugged in", Resource.Drawable.ic_flow_battery, _draft.RequireCharging == true, v => _draft.RequireCharging = v);
        _charging.Id = Resource.Id.queue_editor_charging;
        _battery = Condition("Battery saver is off", "", Resource.Drawable.ic_flow_bolt, _draft.RequireBatterySaverOff == true, v => _draft.RequireBatterySaverOff = v);
        if (!_conditions) { ((View)_unmetered.Parent!.Parent!).Visibility = ViewStates.Gone; ((View)_charging.Parent!.Parent!).Visibility = ViewStates.Gone; ((View)_battery.Parent!.Parent!).Visibility = ViewStates.Gone; }
        Section("Limits");
        _concurrencyText = Row("Downloads at once", Resource.Drawable.ic_flow_download, () => EditConcurrency());
        _speedText = Row(activity.Capabilities.IsRemote ? "HTTP speed limit" : "Speed limit", Resource.Drawable.ic_flow_speed, () => EditSpeed());
        if (!_speedSupported) ((View)_speedText.Parent!.Parent!).Visibility = ViewStates.Gone;
        if (_completionSupported) _completionText = Row("When the queue finishes", Resource.Drawable.ic_flow_bell, EditCompletion);
        var note = Text("Host: " + activity.HostName + ". Times use this Host's local time." + (activity.Capabilities.IsRemote ? "" : " Android may delay background starts."), 12, Resource.Attribute.colorOnSurfaceVariant);
        _content.AddView(note, Margin(16));
        if (!CanEditSafely) _error.Text = "Update the selected Host to edit queues safely. This editor is read-only.";
        else if (_uncertain) _error.Text = "A save may have reached the Host. Review the latest state before saving again.";
        Refresh();
        _reviewAfterDialog = _uncertain && saved is not null && (saved.GetBoolean("speed_dialog") || saved.GetString("time_schedule") is not null || saved.GetString("input_title") is not null);
        if (saved?.GetBoolean("speed_dialog") == true && CanEditSafely)
            Root.Post(() => { if (!_closed) EditSpeed(saved!.GetString("speed_text") ?? "", saved.GetBoolean("speed_custom"), saved.GetInt("speed_selection_start", -1), saved.GetInt("speed_selection_end", -1)); });
        else if (Guid.TryParse(saved?.GetString("time_schedule"), out var timeSchedule) && _draftSchedules.FirstOrDefault(s => s.Id == timeSchedule) is { } timeDraft)
            Root.Post(() => { if (!_closed) ShowTime(timeDraft, saved!.GetBoolean("time_stop"), saved.GetBundle("time_picker_state")); });
        else if (saved?.GetString("input_title") is { } inputTitle)
        {
            var inputText = saved.GetString("input_text") ?? "";
            Guid? inputSchedule = Guid.TryParse(saved.GetString("input_schedule"), out var inputId) ? inputId : null;
            Root.Post(() => { if (!_closed) RestoreInput(inputTitle, inputText, inputSchedule); });
        }
        else if (_uncertain && saved is not null) Root.Post(async () => { if (!_closed) await ReviewLatestAsync(); });
        else if (queue is null && saved is null && CanEditSafely) Root.Post(() => { if (!_closed) EditQueueName(first: true); });
    }
    public View Root { get; }
    public void Save(Bundle state)
    {
        state.PutString("host_stamp", Hosts.HostChoice.Hosts(_activity).Current.ConnectionStamp);
        if (_baseline is not null) state.PutString("baseline", QueueJson(_baseline));
        state.PutString("draft", QueueJson(_draft));
        state.PutString("original_schedules", JsonSerializer.Serialize(new SettingsDto { Schedules = _originalSchedules }, IpcJsonContext.Default.SettingsDto));
        state.PutString("draft_schedules", JsonSerializer.Serialize(new SettingsDto { Schedules = _draftSchedules }, IpcJsonContext.Default.SettingsDto));
        state.PutString("selected", _selectedId?.ToString()); state.PutBoolean("uncertain", _uncertain || _busy);
        if (_timeDialog is { } picker && _timeSchedule is { } timeSchedule)
        {
            state.PutString("time_schedule", timeSchedule.ToString()); state.PutBoolean("time_stop", _timeStop);
            state.PutBundle("time_picker_state", picker.OnSaveInstanceState());
        }
        if (_speedDialog is not null && _speedInput is { } speedInput)
        {
            state.PutBoolean("speed_dialog", true); state.PutBoolean("speed_custom", _speedCustom);
            state.PutString("speed_text", speedInput.Text); state.PutInt("speed_selection_start", speedInput.SelectionStart); state.PutInt("speed_selection_end", speedInput.SelectionEnd);
        }
        state.PutString("input_title", _inputTitle); state.PutString("input_text", _inputText); state.PutString("input_schedule", _inputScheduleId?.ToString());
    }
    private bool Dirty => _baseline is null || QueueJson(_draft) != QueueJson(_baseline) ||
        _originalSchedules.Count != _draftSchedules.Count || _draftSchedules.Any(s => _originalSchedules.FirstOrDefault(o => o.Id == s.Id) is not { } o || ScheduleJson(s) != ScheduleJson(o));
    public void RequestClose()
    {
        if (_closed || _busy) return;
        if (!Dirty) { _activity.CloseRowPage(Root); return; }
        new MaterialAlertDialogBuilder(_activity).SetTitle("Discard changes?")!.SetMessage("Your queue and schedule drafts have not all been saved.")!
            .SetNegativeButton("Keep editing", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Discard", (_, _) => _activity.CloseRowPage(Root))!.ShowTracked(_dialogs);
    }
    private LinearLayout.LayoutParams Margin(int top) => new(-1, -2) { TopMargin = Motion.Dp(_activity, top) };
    private TextView Text(string text, float size, int color)
    {
        var v = new TextView(_activity) { Text = text, TextSize = size }; v.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, color))); return v;
    }
    private TextView Section(string text)
    {
        var v = Text(text, 14, Resource.Attribute.colorPrimary); _content.AddView(v, Margin(14)); return v;
    }
    private TextView TimeTile(string label, bool stop)
    {
        var card = new MaterialCardView(_activity) { Radius = Motion.Dp(_activity, 20), CardElevation = 0, StrokeWidth = 0, Clickable = true, Focusable = true };
        card.SetCardBackgroundColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; var pad = Motion.Dp(_activity, 16); column.SetPadding(pad, pad, pad, pad);
        column.AddView(Text(label, 14, Resource.Attribute.colorOnSurfaceVariant));
        var value = Text("", 46, Resource.Attribute.colorOnSurface); column.AddView(value); card.AddView(column);
        card.ContentDescription = label + " time"; card.Click += (_, _) => PickTime(stop);
        _times.AddView(card, new LinearLayout.LayoutParams(0, Motion.Dp(_activity, 110), 1) { MarginEnd = Motion.Dp(_activity, stop ? 0 : 12) });
        _valueRows.Add((value, card, label + " time"));
        return value;
    }
    private MaterialSwitch Condition(string title, string subtitle, int icon, bool value, Action<bool> changed)
    {
        var row = RowShell(title, subtitle, icon, out var subtitleView, out var tail);
        if (subtitle.Length == 0) subtitleView.Visibility = ViewStates.Gone;
        var control = new MaterialSwitch(_activity) { Checked = value, ContentDescription = title };
        control.CheckedChange += (_, e) => { if (!_refreshing && !_busy && CanEditSafely) changed(e.IsChecked); };
        control.ContentDescription = title + (subtitle.Length == 0 ? "" : ". " + subtitle);
        tail.AddView(control); _content.AddView(row); return control;
    }
    private static int DayResourceId(int index) => index switch
    {
        0 => Resource.Id.queue_editor_day_mon,
        1 => Resource.Id.queue_editor_day_tue,
        2 => Resource.Id.queue_editor_day_wed,
        3 => Resource.Id.queue_editor_day_thu,
        4 => Resource.Id.queue_editor_day_fri,
        5 => Resource.Id.queue_editor_day_sat,
        _ => Resource.Id.queue_editor_day_sun
    };
    private TextView Row(string title, int icon, Action click)
    {
        var row = RowShell(title, "", icon, out var value, out var tail);
        var arrow = new ImageView(_activity); arrow.SetImageResource(Resource.Drawable.ic_chevron_right); tail.AddView(arrow, new LinearLayout.LayoutParams(Motion.Dp(_activity, 24), Motion.Dp(_activity, 24)));
        row.Clickable = true; row.Focusable = true; row.ContentDescription = title; row.Click += (_, _) => { if (!_busy) click(); }; _content.AddView(row); _valueRows.Add((value, row, title)); return value;
    }
    private LinearLayout RowShell(string title, string subtitle, int icon, out TextView value, out LinearLayout tail)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal }; row.SetGravity(GravityFlags.CenterVertical); row.SetMinimumHeight(Motion.Dp(_activity, 68));
        var well = new FrameLayout(_activity); well.SetBackgroundResource(Resource.Drawable.bg_circle_dark);
        var image = new ImageView(_activity); image.SetImageResource(icon); var ip = new FrameLayout.LayoutParams(Motion.Dp(_activity, 22), Motion.Dp(_activity, 22), GravityFlags.Center); well.AddView(image, ip);
        row.AddView(well, new LinearLayout.LayoutParams(Motion.Dp(_activity, 40), Motion.Dp(_activity, 40)));
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; var cp = new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Motion.Dp(_activity, 16) }; row.AddView(column, cp);
        column.AddView(Text(title, 16, Resource.Attribute.colorOnSurface)); value = Text(subtitle, 13, Resource.Attribute.colorOnSurfaceVariant); column.AddView(value);
        tail = new LinearLayout(_activity); tail.SetGravity(GravityFlags.CenterVertical); row.AddView(tail); return row;
    }
    private static bool Supported(ScheduleDto s)
    {
        if (!string.Equals(s.Kind, "daily", StringComparison.OrdinalIgnoreCase) || !s.TimedStartEnabled) return false;
        try { ScheduleDays.FromDraft(s.Days); return true; } catch (FormatException) { return false; }
    }
    private static HashSet<string> Days(ScheduleDto s)
    {
        try { return ScheduleDays.FromDraft(s.Days); } catch (FormatException) { return new HashSet<string>(); }
    }
    private static void NormalizeHostDays(ScheduleDto s)
    {
        try { s.Days = ScheduleDays.Serialize(ScheduleDays.FromHost(s.Days)); } catch (FormatException) { /* Preserve an unknown Host format as read-only. */ }
    }
    private void Refresh()
    {
        if (_closed) return; _refreshing = true;
        _title.Text = string.IsNullOrWhiteSpace(_draft.Name) ? "New queue" :
            _draft.Name.EndsWith(" queue", StringComparison.OrdinalIgnoreCase) || string.Equals(_draft.Name, "queue", StringComparison.OrdinalIgnoreCase) ? _draft.Name : _draft.Name + " queue";
        var s = Selected; _scheduleLabel.Text = s is null ? "Choose or add a schedule" : s.Name + (s.Enabled ? "" : " \u00b7 off");
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? ViewStates.Gone : ViewStates.Visible;
        _scheduleLabel.Visibility = _draftSchedules.Count == 1 && s is not null ? ViewStates.Gone : ViewStates.Visible;
        _startText.Text = s?.StartTime ?? "--:--"; _stopText.Text = s?.StopTime ?? "None";
        var days = s is null ? new HashSet<string>() : Days(s);
        for (int i = 0; i < _days.Length; i++) { _days[i].Checked = days.Contains(DayNames[i]); _days[i].Enabled = !_busy && CanEditSafely && s is not null && Supported(s); }
        _concurrencyText.Text = _draft.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        _speedText.Text = _draft.SpeedLimitBytesPerSecond is > 0 ? Fmt.Rate(_draft.SpeedLimitBytesPerSecond.Value) : "None";
        if (_completionText is not null) _completionText.Text = _draft.NotifyWhenFinished == true
            ? (_activity.Capabilities.IsRemote ? "Notify and finish" : "Notify when the queue finishes")
            : "Do nothing";
        foreach (var (value, row, title) in _valueRows) row.ContentDescription = title + ", " + value.Text;
        PaintDays();
        _save.Enabled = !_busy && !_uncertain && CanEditSafely; _scheduleLabel.Enabled = !_busy && CanEditSafely;
        _unmetered.Enabled = _charging.Enabled = _battery.Enabled = !_busy && CanEditSafely;
        foreach (var (_, row, _) in _valueRows) row.Enabled = !_busy && CanEditSafely;
        _refreshing = false;
    }
    private void PaintDays()
    {
        foreach (var button in _days)
        {
            button.StrokeWidth = button.Checked ? 0 : Motion.Dp(_activity, 1);
            int background = button.Checked ? Motion.Attr(_activity, Resource.Attribute.colorPrimary) : 0;
            int foreground = Motion.Attr(_activity, button.Checked ? Resource.Attribute.colorOnPrimary : Resource.Attribute.colorOnSurfaceVariant);
            button.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(background));
            button.SetTextColor(new Android.Graphics.Color(foreground));
        }
    }
    private void EditCompletion()
    {
        if (!_completionSupported || !CanEditSafely) return;
        int choice = _draft.NotifyWhenFinished == true ? 0 : 1;
        new MaterialAlertDialogBuilder(_activity).SetTitle("When the queue finishes")!
            .SetSingleChoiceItems(["Notify and finish", "Do nothing"], choice, (_, e) => choice = e.Which)!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Apply", (_, _) => { if (!_closed) { _draft.NotifyWhenFinished = choice == 0; Refresh(); } })!.ShowTracked(_dialogs);
    }
    private static string SpeedText(long bytes) => (bytes / Fmt.BytesPerKilobyte + (bytes % Fmt.BytesPerKilobyte == 0 ? 0 : 1)).ToString(CultureInfo.InvariantCulture);
    private void Options()
    {
        if (_busy || _closed) return;
        var choices = new List<string>();
        if (CanEditSafely) { choices.Add("Schedules"); choices.Add("Rename queue"); }
        if (_uncertain) choices.Add("Review latest state");
        if (choices.Count == 0) { _error.Text = "Update the selected Host to edit queues safely."; return; }
        new MaterialAlertDialogBuilder(_activity).SetTitle(_draft.Name)!.SetItems(choices.ToArray(), async (_, e) =>
        {
            if (_closed || _busy) return;
            switch (choices[e.Which]) { case "Schedules": ScheduleList(); break; case "Rename queue": EditQueueName(false); break; case "Review latest state": await ReviewLatestAsync(); break; }
        })!.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs);
    }
    private void EditQueueName(bool first, string? initial = null) => Input(first ? "New queue" : "Rename queue", "Queue name", initial ?? _draft.Name, false, first ? "Continue" : "Save", text =>
    {
        if (text.Length is 0 or > 80) return "Enter a name up to 80 characters.";
        _draft.Name = text; Refresh(); return null;
    }, () => { if (first && string.IsNullOrWhiteSpace(_draft.Name) && !_closed) _activity.CloseRowPage(Root); });
    private void ScheduleList()
    {
        if (_busy || _closed) return;
        var sheet = new BottomSheetDialog(_activity);
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var pad = Motion.Dp(_activity, 24); body.SetPadding(pad, pad, pad, pad);
        var title = Text((_draft.Name.Length == 0 ? "Queue" : _draft.Name) + " schedules", 22, Resource.Attribute.colorOnSurface);
        title.AccessibilityHeading = true; body.AddView(title);
        var scroll = new ScrollView(_activity);
        var list = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; scroll.AddView(list);
        // A bounded sheet remains usable when another client has created many schedules.
        var listHeight = Math.Min(Motion.Dp(_activity, _draftSchedules.Count * 76), _activity.Resources!.DisplayMetrics!.HeightPixels / 2);
        body.AddView(scroll, new LinearLayout.LayoutParams(-1, listHeight) { TopMargin = Motion.Dp(_activity, 12) });
        foreach (var schedule in _draftSchedules)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical); row.SetMinimumHeight(Motion.Dp(_activity, 72));
            var labels = new LinearLayout(_activity) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true };
            labels.Background = new TypedValueHolder(_activity).SelectableBackground();
            labels.SetPadding(0, Motion.Dp(_activity, 12), 0, Motion.Dp(_activity, 12));
            labels.AddView(Text(schedule.Name, 17, Resource.Attribute.colorOnSurface));
            labels.AddView(Text(schedule.Days + " · " + schedule.StartTime + (schedule.StopTime is { } stop ? "–" + stop : ""), 13, Resource.Attribute.colorOnSurfaceVariant));
            labels.ContentDescription = schedule.Name + ", " + schedule.Days + ", " + schedule.StartTime + ". Edit schedule";
            labels.Click += (_, _) => { sheet.Dismiss(); if (_closed) return; if (Supported(schedule)) { _selectedId = schedule.Id; Refresh(); } else ScheduleOptions(schedule); };
            row.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1));
            if (Supported(schedule))
            {
                var enabled = new MaterialSwitch(_activity) { Checked = schedule.Enabled, ContentDescription = "Enable " + schedule.Name };
                enabled.CheckedChange += (_, e) => { if (!_closed && !_busy) { schedule.Enabled = e.IsChecked; Refresh(); } };
                row.AddView(enabled);
                var more = new ImageButton(_activity); more.SetImageResource(Resource.Drawable.ic_more); more.ContentDescription = "Options for " + schedule.Name;
                more.Background = new TypedValueHolder(_activity).SelectableBackground();
                more.Click += (_, _) => { sheet.Dismiss(); if (!_closed) ScheduleOptions(schedule); };
                row.AddView(more, new LinearLayout.LayoutParams(Motion.Dp(_activity, 48), Motion.Dp(_activity, 48)));
            }
            list.AddView(row);
        }
        var add = Text("+  Add schedule", 17, Resource.Attribute.colorPrimary); add.SetMinHeight(Motion.Dp(_activity, 56)); add.Gravity = GravityFlags.CenterVertical;
        add.Clickable = true; add.Focusable = true; add.Background = new TypedValueHolder(_activity).SelectableBackground();
        add.Click += (_, _) => { sheet.Dismiss(); if (!_closed) AddSchedule(); }; body.AddView(add);
        var done = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle) { Text = "Done" };
        done.Click += (_, _) => sheet.Dismiss();
        body.AddView(done, new LinearLayout.LayoutParams(-2, Motion.Dp(_activity, 48)) { Gravity = GravityFlags.End });
        sheet.SetContentView(body); _dialogs.Add(sheet); sheet.DismissEvent += (_, _) => _dialogs.Remove(sheet); sheet.Show();
    }
    private void AddSchedule(string? initial = null) => Input("New schedule", "Schedule name", initial ?? "", false, "Continue", text =>
    {
        if (text.Length is 0 or > 80) return "Enter a name up to 80 characters.";
        var s = new ScheduleDto { Id = Guid.NewGuid(), QueueId = _draft.Id, Name = text, Kind = "daily", Enabled = true, TimedStartEnabled = true, StartTime = "09:00", Days = string.Join(',', DayNames) };
        _draftSchedules.Add(s); _selectedId = s.Id; Refresh(); return null;
    });
    private void ScheduleOptions(ScheduleDto s)
    {
        if (!Supported(s)) { new MaterialAlertDialogBuilder(_activity).SetTitle("View this schedule")!.SetMessage($"{s.Name}\n{s.StartTime} \u00b7 {(s.Enabled ? "enabled" : "off")}\n\nEdit this schedule from a client that supports its type. Its values are kept.")!.SetPositiveButton("Close", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs); return; }
        string[] choices = ["Edit times and days", s.Enabled ? "Disable schedule" : "Enable schedule", "Rename schedule", "Delete schedule"];
        new MaterialAlertDialogBuilder(_activity).SetTitle(s.Name)!.SetItems(choices, (_, e) =>
        {
            switch (e.Which)
            {
                case 0: _selectedId = s.Id; Refresh(); break;
                case 1: s.Enabled = !s.Enabled; Refresh(); ScheduleList(); break;
                case 2: EditScheduleName(s); break;
                case 3:
                    var dialog = new MaterialAlertDialogBuilder(_activity)
                        .SetTitle($"Delete {s.Name}?")!
                        .SetMessage("Only this schedule will be removed when you save. The queue and downloads stay.")!
                        .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
                        .SetPositiveButton("Delete", (_, _) =>
                        {
                            _draftSchedules.Remove(s);
                            if (_selectedId == s.Id) _selectedId = null;
                            Refresh();
                        })!
                        .ShowTracked(_dialogs);
                    dialog?.GetButton((int)DialogButtonType.Positive)?.SetTextColor(
                        new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorError)));
                    break;
            }
        })!.SetNegativeButton("Back", (_, _) => ScheduleList())!.ShowTracked(_dialogs);
    }
    private void EditScheduleName(ScheduleDto schedule, string? initial = null)
    {
        _inputScheduleId = schedule.Id;
        Input("Rename schedule", "Schedule name", initial ?? schedule.Name, false, "Save", text =>
        { if (text.Length is 0 or > 80) return "Enter a name up to 80 characters."; schedule.Name = text; Refresh(); return null; });
    }
    private void PickTime(bool stop)
    {
        if (_busy || _closed) return;
        if (Selected is not { } s) { ScheduleList(); return; }
        if (!Supported(s)) { ScheduleOptions(s); return; }
        if (stop)
        {
            new MaterialAlertDialogBuilder(_activity).SetTitle("Stop time")!.SetItems(["Choose time", "No stop time"], (_, e) => { if (e.Which == 0) ShowTime(s, true); else { s.StopTime = null; Refresh(); } })!.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs);
        }
        else ShowTime(s, false);
    }
    private void ShowTime(ScheduleDto s, bool stop, Bundle? restored = null)
    {
        var current = stop ? s.StopTime : s.StartTime;
        var t = TimeOnly.TryParseExact(current, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : new TimeOnly(9, 0);
        var dialog = new Android.App.TimePickerDialog(_activity, (_, e) => { if (_closed || _busy) return; var text = $"{e.HourOfDay:00}:{e.Minute:00}"; if (stop) s.StopTime = text; else s.StartTime = text; Refresh(); }, t.Hour, t.Minute, Android.Text.Format.DateFormat.Is24HourFormat(_activity));
        _timeDialog = dialog; _timeSchedule = s.Id; _timeStop = stop; _dialogs.Add(dialog);
        dialog.DismissEvent += (_, _) => { _dialogs.Remove(dialog); if (_timeDialog == dialog) { _timeDialog = null; _timeSchedule = null; } ReviewAfterDialog(); };
        dialog.Show();
        if (restored is not null) dialog.OnRestoreInstanceState(restored);
    }
    private void EditConcurrency(string? initial = null) => Input("Downloads at once", "Downloads at once (1\u201316)", initial ?? _draft.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture), true, "Apply", text =>
    {
        if (!int.TryParse(text, out var n) || n is < 1 or > 16) return "Enter a number from 1 to 16.";
        _draft.MaxConcurrentDownloads = n; Refresh(); return null;
    });
    private void EditSpeed(string? restoredText = null, bool? restoredCustom = null, int selectionStart = -1, int selectionEnd = -1)
    {
        if (!_speedSupported || !CanEditSafely || _closed || _busy) return;
        var current = _draft.SpeedLimitBytesPerSecond;
        var initial = restoredText ?? (current is > 0 ? SpeedText(current.Value) : "");
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        int pad = Motion.Dp(_activity, 24); body.SetPadding(pad, Motion.Dp(_activity, 8), pad, Motion.Dp(_activity, 16));
        var choices = new RadioGroup(_activity) { Orientation = Orientation.Vertical };
        var none = new RadioButton(_activity) { Text = "None", ContentDescription = "No queue speed limit" };
        var custom = new RadioButton(_activity) { Text = "Custom limit", ContentDescription = "Custom queue speed limit" };
        choices.AddView(none); choices.AddView(custom); body.AddView(choices);
        var input = new EditText(_activity) { Hint = "KB/s", Text = initial, InputType = Android.Text.InputTypes.ClassNumber, ContentDescription = "Queue speed limit in KB per second" };
        input.SetSingleLine(true); body.AddView(input);
        var note = Text("Shared by this queue's HTTP downloads.", 14, Resource.Attribute.colorOnSurfaceVariant); body.AddView(note);
        var error = Text("", 14, Resource.Attribute.colorError); error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite; body.AddView(error);
        var buttons = new LinearLayout(_activity) { Orientation = Orientation.Horizontal }; buttons.SetGravity(GravityFlags.End);
        var cancel = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Cancel" };
        var apply = new MaterialButton(_activity) { Text = "Apply" }; buttons.AddView(cancel); buttons.AddView(apply); body.AddView(buttons);
        _speedCustom = restoredCustom ?? current is > 0; custom.Checked = _speedCustom; none.Checked = !_speedCustom; input.Enabled = _speedCustom;
        custom.CheckedChange += (_, e) => { if (_closed) return; _speedCustom = e.IsChecked; input.Enabled = e.IsChecked; };
        if (selectionStart >= 0 && selectionEnd >= 0) input.SetSelection(Math.Clamp(selectionStart, 0, initial.Length), Math.Clamp(selectionEnd, 0, initial.Length));
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Speed limit")!.SetView(body)!.Create()!;
        _speedDialog = dialog; _speedInput = input; _dialogs.Add(dialog);
        dialog.DismissEvent += (_, _) => { _dialogs.Remove(dialog); if (_speedDialog == dialog) { _speedDialog = null; _speedInput = null; } ReviewAfterDialog(); };
        cancel.Click += (_, _) => { if (!_closed) dialog.Dismiss(); };
        apply.Click += (_, _) =>
        {
            if (_closed || _busy || !CanEditSafely) return;
            if (!_speedCustom) _draft.SpeedLimitBytesPerSecond = 0;
            else
            {
                var text = (input.Text ?? "").Trim();
                if (!(current is > 0 && text == SpeedText(current.Value)))
                {
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) || kb < 1 || kb > long.MaxValue / Fmt.BytesPerKilobyte)
                    { error.Text = "Enter a positive whole number in KB/s."; return; }
                    _draft.SpeedLimitBytesPerSecond = kb * Fmt.BytesPerKilobyte;
                }
            }
            Refresh(); dialog.Dismiss();
        };
        dialog.SetCanceledOnTouchOutside(false); dialog.Show();
    }
    private void RestoreInput(string title, string text, Guid? scheduleId)
    {
        switch (title)
        {
            case "New queue": EditQueueName(true, text); break;
            case "Rename queue": EditQueueName(false, text); break;
            case "New schedule": AddSchedule(text); break;
            case "Rename schedule": if (_draftSchedules.FirstOrDefault(s => s.Id == scheduleId) is { } schedule) EditScheduleName(schedule, text); break;
            case "Downloads at once": EditConcurrency(text); break;
            case "Speed limit": EditSpeed(text, true); break;
        }
    }
    private void Input(string title, string hint, string initial, bool number, string confirm, Func<string, string?> apply, Action? cancel = null, string? error = null)
    {
        if (_closed || _busy || !CanEditSafely) return;
        var wrapper = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; var pad = Motion.Dp(_activity, 24); wrapper.SetPadding(pad, 0, pad, 0);
        var fieldLayout = new TextInputLayout(_activity, null, Resource.Attribute.textInputOutlinedStyle) { Hint = hint };
        _inputTitle = title; _inputText = initial;
        var field = new TextInputEditText(fieldLayout.Context!) { Text = initial, ContentDescription = hint }; field.SetSingleLine(true);
        field.TextChanged += (_, _) => _inputText = field.Text;
        if (number) field.InputType = Android.Text.InputTypes.ClassNumber;
        fieldLayout.AddView(field); wrapper.AddView(fieldLayout); if (error is not null) fieldLayout.Error = error;
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle(title)!.SetView(wrapper)!
            .SetNegativeButton("Cancel", (_, _) => cancel?.Invoke())!.SetPositiveButton(confirm, (IDialogInterfaceOnClickListener?)null)!.Create();
        dialog!.DismissEvent += (_, _) => { _inputTitle = null; _inputText = null; _inputScheduleId = null; ReviewAfterDialog(); };
        _dialogs.Add(dialog);
        dialog.DismissEvent += (_, _) => _dialogs.Remove(dialog);
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            if (_closed || _busy) return;
            var failure = apply((field.Text ?? "").Trim());
            if (failure is not null) { fieldLayout.Error = failure; return; }
            dialog.Dismiss();
        };
    }
    private async Task SaveAsync()
    {
        if (_busy || _closed || _uncertain || !CanEditSafely) return;
        if (string.IsNullOrWhiteSpace(_draft.Name)) { EditQueueName(true); return; }
        foreach (var schedule in _draftSchedules.Where(Supported))
        {
            if (Days(schedule).Count == 0) { _selectedId = schedule.Id; _error.Text = "Choose at least one day for " + schedule.Name + "."; Refresh(); return; }
        }
        _busy = true; _error.Text = ""; Refresh();
        bool queueSaved = false;
        try
        {
            var ids = _settings.Settings.Queues.Select(q => q.Id).ToHashSet();
            var result = await _settings.SaveQueueAsync(_draft, _baseline, _life.Token);
            if (_closed) return;
            _downloads.ApplySettingsResult(_settings.LastResult);
            if (!result.Ok) { QueueFailure(result.Error); return; }
            var queues = _settings.LastResult?.Settings.Queues ?? [];
            var found = _baseline is null ? queues.Where(q => !ids.Contains(q.Id)).ToArray() : queues.Where(q => q.Id == _baseline.Id).ToArray();
            if (found.Length != 1) { MarkUncertain("The Host saved the queue but did not identify it. Check the queue list before saving again."); return; }
            _baseline = Clone(found[0]); _draft = Clone(found[0]); queueSaved = true;
            foreach (var item in _draftSchedules) item.QueueId = _baseline.Id;
            foreach (var removed in _originalSchedules.Where(o => _draftSchedules.All(d => d.Id != o.Id)).ToArray())
            {
                result = await _settings.DeleteScheduleAsync(removed, _life.Token, _baseline.Generation);
                if (_closed) return;
                _downloads.ApplySettingsResult(_settings.LastResult);
                if (!result.Ok) { ScheduleFailure(removed, null, result.Error, queueSaved); return; }
                _originalSchedules.Remove(removed);
            }
            foreach (var item in _draftSchedules.ToArray())
            {
                var old = _originalSchedules.FirstOrDefault(o => o.Id == item.Id);
                if (old is not null && ScheduleJson(item) == ScheduleJson(old)) continue;
                result = await _settings.SaveScheduleAsync(item, old, _life.Token, _baseline.Generation);
                if (_closed) return;
                _downloads.ApplySettingsResult(_settings.LastResult);
                if (!result.Ok) { ScheduleFailure(old, item, result.Error, queueSaved); return; }
                var committed = _settings.LastResult?.Settings.Schedules.FirstOrDefault(s => s.Id == item.Id);
                if (committed is null) { MarkUncertain("The Host did not identify the saved schedule. Check the queue before saving again."); return; }
                if (old is not null) _originalSchedules.Remove(old);
                _originalSchedules.Add(HostSchedule(committed));
                var index = _draftSchedules.FindIndex(s => s.Id == item.Id); _draftSchedules[index] = HostSchedule(committed);
            }
            _activity.CloseRowPage(Root);
        }
        catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { if (!_closed) { _busy = false; Refresh(); } }
    }
    private void MarkUncertain(string message)
    {
        _uncertain = true; _error.Text = message + " Review latest state from the options menu before saving again.";
        new MaterialAlertDialogBuilder(_activity).SetTitle("Check before saving again")!.SetMessage(message + " Your draft is kept here.")!
            .SetNegativeButton("Keep editing", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Reconnect", (_, _) => { if (!_closed) _activity.Recreate(); })!.ShowTracked(_dialogs);
    }
    private static ScheduleDto HostSchedule(ScheduleDto source)
    {
        var schedule = Clone(source); NormalizeHostDays(schedule); return schedule;
    }
    private static bool SameVisibleQueue(QueueDto a, QueueDto b) =>
        a.Name == b.Name && a.MaxConcurrentDownloads == b.MaxConcurrentDownloads &&
        (a.SpeedLimitBytesPerSecond ?? 0) == (b.SpeedLimitBytesPerSecond ?? 0) &&
        (a.RequireUnmeteredNetwork ?? false) == (b.RequireUnmeteredNetwork ?? false) &&
        (a.RequireCharging ?? false) == (b.RequireCharging ?? false) &&
        (a.RequireBatterySaverOff ?? false) == (b.RequireBatterySaverOff ?? false) &&
        (a.NotifyWhenFinished ?? false) == (b.NotifyWhenFinished ?? false);
    private static bool SameVisibleSchedule(ScheduleDto a, ScheduleDto b) =>
        a.Name == b.Name && a.Enabled == b.Enabled && a.StartTime == b.StartTime &&
        a.StopTime == b.StopTime && (Supported(a) && Supported(b) ? Days(a).SetEquals(Days(b)) : a.Days == b.Days);
    private static ScheduleDto OverlaySchedule(ScheduleDto? original, ScheduleDto draft, ScheduleDto latest)
    {
        var result = Clone(latest);
        if (original is null || draft.Name != original.Name) result.Name = draft.Name;
        if (original is null || draft.Enabled != original.Enabled) result.Enabled = draft.Enabled;
        if (original is null || draft.StartTime != original.StartTime) result.StartTime = draft.StartTime;
        if (original is null || draft.StopTime != original.StopTime) result.StopTime = draft.StopTime;
        if (original is null || !Days(draft).SetEquals(Days(original))) result.Days = draft.Days;
        return result;
    }
    private void ReviewAfterDialog()
    {
        if (!_reviewAfterDialog || _closed) return;
        _reviewAfterDialog = false;
        Root.Post(async () => { if (!_closed && _uncertain) await ReviewLatestAsync(); });
    }
    private async Task ReviewLatestAsync()
    {
        if (_closed || _busy || !_uncertain) return;
        _busy = true; Refresh();
        try
        {
            var result = await _settings.RefreshAsync(_life.Token);
            if (_closed) return;
            _downloads.ApplySettingsResult(_settings.LastResult);
            if (!result.Ok || _settings.LastResult is not { Ok: true } response)
            {
                _error.Text = result.Error ?? "Could not read the selected Host. Your draft is kept."; return;
            }
            ShowLatest(response.Settings);
        }
        catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { if (!_closed) { _busy = false; Refresh(); } }
    }
    private void ShowLatest(SettingsDto settings)
    {
        if (_closed) return;
        // A lost create reply has no trusted queue ID. A name match must never identify it.
        if (_baseline is null)
        {
            _error.Text = "The Host did not confirm which queue was created. Your draft is retained; check the queue list before creating another.";
            new MaterialAlertDialogBuilder(_activity).SetTitle("Review latest state")!.SetMessage(_error.Text)!
                .SetPositiveButton("Keep draft", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs); return;
        }
        var latest = settings.Queues.FirstOrDefault(q => q.Id == _baseline.Id);
        if (latest is null)
        {
            _error.Text = "The original queue is missing on the Host. Your draft is retained and cannot be rebound automatically.";
            new MaterialAlertDialogBuilder(_activity).SetTitle("Review latest state")!.SetMessage(_error.Text)!
                .SetPositiveButton("Keep draft", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs); return;
        }
        var hostSchedules = settings.Schedules.Where(s => s.QueueId == latest.Id).Select(HostSchedule).ToList();
        bool identityMatches = _baseline.Generation is { } generation && generation != Guid.Empty && latest.Generation == generation;
        string? blocked = identityMatches ? null : "The queue identity is different or cannot be verified.";
        foreach (var old in _originalSchedules)
        {
            var local = _draftSchedules.FirstOrDefault(s => s.Id == old.Id);
            var host = hostSchedules.FirstOrDefault(s => s.Id == old.Id);
            if (local is not null && host is null) blocked ??= "An original schedule is missing; keeping the draft would recreate it.";
            if (host is not null && (!string.Equals(old.Kind, host.Kind, StringComparison.OrdinalIgnoreCase) || old.TimedStartEnabled != host.TimedStartEnabled || Supported(old) != Supported(host)))
                blocked ??= "A schedule type changed on the Host.";
        }
        foreach (var local in _draftSchedules.Where(s => _originalSchedules.All(o => o.Id != s.Id)))
        {
            var host = hostSchedules.FirstOrDefault(s => s.Id == local.Id);
            if (host is not null && (!Supported(local) || !Supported(host))) blocked ??= "A draft schedule now has an unsupported Host type.";
        }
        bool applied = blocked is null && SameVisibleQueue(RebaseQueue(latest), latest);
        if (applied)
        {
            foreach (var old in _originalSchedules)
            {
                var local = _draftSchedules.FirstOrDefault(s => s.Id == old.Id);
                var host = hostSchedules.FirstOrDefault(s => s.Id == old.Id);
                if (local is null ? host is not null : host is null || !SameVisibleSchedule(OverlaySchedule(old, local, host), host)) { applied = false; break; }
            }
            foreach (var local in _draftSchedules.Where(s => _originalSchedules.All(o => o.Id != s.Id)))
            {
                var host = hostSchedules.FirstOrDefault(s => s.Id == local.Id);
                if (host is null || !SameVisibleSchedule(local, host)) { applied = false; break; }
            }
        }
        var hostDescription = hostSchedules.Count == 0 ? "No schedules" : string.Join("\n", hostSchedules.Select(ScheduleSummary));
        var draftDescription = _draftSchedules.Count == 0 ? "No schedules" : string.Join("\n", _draftSchedules.Select(ScheduleSummary));
        var message = (applied ? "Your requested changes already match the Host. " : "No changes will be written by this review. ") +
            "\n\nYour queue: " + QueueSummary(_draft) + "\nHost queue: " + QueueSummary(latest) +
            "\n\nYour schedules:\n" + draftDescription + "\n\nHost schedules:\n" + hostDescription +
            (blocked is null ? "" : "\n\nCannot keep draft against this queue: " + blocked);
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle(applied ? "Changes already applied" : "Review latest state")!.SetMessage(message)!
            .SetNeutralButton("Keep editing", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Use Host values", (_, _) => UseHostValues(latest, hostSchedules));
        if (blocked is null)
            dialog!.SetPositiveButton("Keep draft", (_, _) => KeepDraftAgainst(latest, hostSchedules));
        dialog!.ShowTracked(_dialogs);
    }
    private void UseHostValues(QueueDto latest, List<ScheduleDto> schedules)
    {
        if (_closed || _busy) return;
        _baseline = Clone(latest); _draft = Clone(latest);
        _originalSchedules = schedules.Select(Clone).ToList(); _draftSchedules = schedules.Select(Clone).ToList();
        if (_draftSchedules.All(s => s.Id != _selectedId)) _selectedId = _draftSchedules.Count == 1 ? _draftSchedules[0].Id : null;
        _uncertain = false; _error.Text = CanEditSafely ? "" : "Update the selected Host to edit queues safely. This editor is read-only."; SyncConditions(); Refresh();
    }
    private void KeepDraftAgainst(QueueDto latest, List<ScheduleDto> schedules)
    {
        if (_closed || _busy || _baseline is null || _baseline.Generation is not { } generation || generation == Guid.Empty || latest.Generation != generation) return;
        var rebasedQueue = RebaseQueue(latest);
        var next = schedules.Select(Clone).ToList();
        foreach (var old in _originalSchedules)
        {
            var local = _draftSchedules.FirstOrDefault(s => s.Id == old.Id);
            var index = next.FindIndex(s => s.Id == old.Id);
            if (index >= 0 && (!string.Equals(old.Kind, next[index].Kind, StringComparison.OrdinalIgnoreCase) || old.TimedStartEnabled != next[index].TimedStartEnabled || Supported(old) != Supported(next[index]))) return;
            if (local is null) { if (index >= 0) next.RemoveAt(index); continue; }
            if (index < 0) return; // Never recreate a missing original.
            var host = next[index];
            next[index] = OverlaySchedule(old, local, host);
        }
        foreach (var local in _draftSchedules.Where(s => _originalSchedules.All(o => o.Id != s.Id)))
        {
            var index = next.FindIndex(s => s.Id == local.Id);
            if (index < 0) next.Add(Clone(local));
            else { if (!Supported(local) || !Supported(next[index])) return; next[index] = OverlaySchedule(null, local, next[index]); }
        }
        _baseline = Clone(latest); _draft = rebasedQueue;
        _originalSchedules = schedules.Select(Clone).ToList(); _draftSchedules = next;
        _uncertain = false; _error.Text = "Draft rebased on the latest Host state. Review it before saving."; SyncConditions(); Refresh();
    }
    private void QueueFailure(string? error)
    {
        var response = _settings.LastResult;
        if (response is null) { MarkUncertain("The Host did not confirm the queue save. It may have been saved."); return; }
        if ((response.Ok || response.Error == "Settings changed on the Host. Reopen the editor and try again.") && _baseline is not null)
        {
            var latest = response.Settings.Queues.FirstOrDefault(q => q.Id == _baseline.Id);
            if (latest is null) { _error.Text = "This queue no longer exists on the Host. Your draft is kept."; return; }
            if (_baseline.Generation is not { } generation || generation == Guid.Empty || latest.Generation != generation)
            {
                MarkUncertain("The queue identity changed or cannot be verified. Review the latest state before choosing what to keep."); return;
            }
            var comparison = "Your draft: " + QueueSummary(_draft) + "\n\nHost now: " + QueueSummary(latest) + "\n\nChoose which values to keep in the draft. Nothing will be saved yet.";
            new MaterialAlertDialogBuilder(_activity).SetTitle("Settings changed elsewhere")!.SetMessage(comparison)!
                .SetNegativeButton("Use Host values", (_, _) => { _baseline = Clone(latest); _draft = Clone(latest); SyncConditions(); Refresh(); })!
                .SetPositiveButton("Keep my values", (_, _) => { _draft = RebaseQueue(latest); _baseline = Clone(latest); SyncConditions(); Refresh(); })!.ShowTracked(_dialogs);
            return;
        }
        _error.Text = error ?? "The Host refused the queue save.";
        new MaterialAlertDialogBuilder(_activity).SetTitle("Queue not saved")!.SetMessage(_error.Text)!.SetPositiveButton("Keep editing", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs);
    }
    private QueueDto RebaseQueue(QueueDto latest)
    {
        var result = Clone(latest); var original = _baseline!;
        if (_draft.Name != original.Name) result.Name = _draft.Name;
        if (_draft.MaxConcurrentDownloads != original.MaxConcurrentDownloads) result.MaxConcurrentDownloads = _draft.MaxConcurrentDownloads;
        if (_draft.SpeedLimitBytesPerSecond != original.SpeedLimitBytesPerSecond) result.SpeedLimitBytesPerSecond = _draft.SpeedLimitBytesPerSecond;
        if (_draft.RequireUnmeteredNetwork != original.RequireUnmeteredNetwork) result.RequireUnmeteredNetwork = _draft.RequireUnmeteredNetwork;
        if (_draft.RequireCharging != original.RequireCharging) result.RequireCharging = _draft.RequireCharging;
        if (_draft.RequireBatterySaverOff != original.RequireBatterySaverOff) result.RequireBatterySaverOff = _draft.RequireBatterySaverOff;
        if (_draft.NotifyWhenFinished != original.NotifyWhenFinished) result.NotifyWhenFinished = _draft.NotifyWhenFinished;
        return result;
    }
    private static string QueueSummary(QueueDto q) => $"{q.Name}; {q.MaxConcurrentDownloads} at once; " +
        (q.SpeedLimitBytesPerSecond is > 0 ? Fmt.Rate(q.SpeedLimitBytesPerSecond.Value) : "no speed limit") +
        $"; unmetered {q.RequireUnmeteredNetwork == true}; charging {q.RequireCharging == true}; battery saver off {q.RequireBatterySaverOff == true}; queue summary {q.NotifyWhenFinished == true}";
    private void SyncConditions()
    {
        _refreshing = true; _unmetered.Checked = _draft.RequireUnmeteredNetwork == true; _charging.Checked = _draft.RequireCharging == true; _battery.Checked = _draft.RequireBatterySaverOff == true; _refreshing = false;
    }
    private void ScheduleFailure(ScheduleDto? old, ScheduleDto? draft, string? error, bool queueSaved)
    {
        var response = _settings.LastResult;
        if (response is null) { MarkUncertain("Queue settings were saved, but the Host did not confirm the schedule change. It may have been saved."); return; }
        var latestQueue = _baseline is null ? null : response.Settings.Queues.FirstOrDefault(q => q.Id == _baseline.Id);
        if (_baseline is null || _baseline.Generation is not { } generation || generation == Guid.Empty || latestQueue?.Generation != generation)
        { MarkUncertain("The queue identity changed or cannot be verified. Review the latest state before choosing what to keep."); return; }
        var latest = old is null ? null : response.Settings.Schedules.FirstOrDefault(s => s.Id == old.Id && s.QueueId == _baseline.Id);
        if (latest is not null) latest = HostSchedule(latest);
        if ((response.Ok || response.Error == "Settings changed on the Host. Reopen the editor and try again.") && old is not null && latest is not null)
        {
            var mine = draft is null ? "Delete this schedule" : ScheduleSummary(draft);
            var comparison = "Your draft: " + mine + "\n\nHost now: " + ScheduleSummary(latest) + "\n\nChoose which values to keep. Nothing will be saved yet.";
            new MaterialAlertDialogBuilder(_activity).SetTitle("Schedule changed elsewhere")!.SetMessage(comparison)!
                .SetNegativeButton("Use Host values", (_, _) => { _originalSchedules.Remove(old); _originalSchedules.Add(Clone(latest)); _draftSchedules.RemoveAll(s => s.Id == latest.Id); _draftSchedules.Add(Clone(latest)); _selectedId = latest.Id; Refresh(); })!
                .SetPositiveButton("Keep my values", (_, _) =>
                {
                    if (draft is not null && !Supported(latest)) { _error.Text = "The Host changed this schedule to a type this editor cannot modify. Use Host values."; return; }
                    if (draft is not null)
                    {
                        var rebased = Clone(latest);
                        if (draft.Name != old.Name) rebased.Name = draft.Name;
                        if (draft.Enabled != old.Enabled) rebased.Enabled = draft.Enabled;
                        if (draft.StartTime != old.StartTime) rebased.StartTime = draft.StartTime;
                        if (draft.StopTime != old.StopTime) rebased.StopTime = draft.StopTime;
                        if (draft.Days != old.Days) rebased.Days = draft.Days;
                        var i = _draftSchedules.FindIndex(s => s.Id == old.Id); if (i >= 0) _draftSchedules[i] = rebased;
                    }
                    _originalSchedules.Remove(old); _originalSchedules.Add(Clone(latest)); Refresh();
                })!.ShowTracked(_dialogs);
            return;
        }
        var prefix = queueSaved ? "Queue settings were saved. " : "";
        _error.Text = prefix + (error ?? "The Host refused the schedule change.") + " Your draft is kept.";
        new MaterialAlertDialogBuilder(_activity).SetTitle("Schedule not saved")!.SetMessage(_error.Text)!.SetPositiveButton("Keep editing", (IDialogInterfaceOnClickListener?)null)!.ShowTracked(_dialogs);
    }
    private static string ScheduleSummary(ScheduleDto s) => $"{s.Name}; {(s.Enabled ? "enabled" : "off")}; {s.Days}; {s.StartTime} to {s.StopTime ?? "none"}";
    public void Dispose()
    {
        if (_closed) return; _closed = true;
        foreach (var dialog in _dialogs.ToArray()) dialog.Dismiss();
        _dialogs.Clear(); _life.Cancel(); _life.Dispose();
    }
}

internal static class QueueDialogLifecycle
{
    public static AndroidX.AppCompat.App.AlertDialog? ShowTracked(this AndroidX.AppCompat.App.AlertDialog.Builder builder, List<Android.App.Dialog> owned)
    {
        var dialog = builder.Show();
        if (dialog is null) return null;
        owned.Add(dialog); dialog.DismissEvent += (_, _) => owned.Remove(dialog); return dialog;
    }
}
