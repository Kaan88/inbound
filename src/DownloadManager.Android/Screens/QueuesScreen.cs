using System.Collections.Specialized;
using System.ComponentModel;
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
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>Stable queue cards: progress updates change text and bars, preserving keyboard and TalkBack focus.</summary>
internal sealed class QueuesScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _downloads;
    private readonly QueueSettingsModel _settings;
    private readonly Action<QueueDto?> _open;
    private readonly LinearLayout _cards;
    private readonly View _new;
    private readonly TextView _notice;
    private readonly CancellationTokenSource _life = new();
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly Java.Lang.Runnable _startLabelExpiry;
    private DateTimeOffset? _startLabelExpiresAt;
    private readonly HashSet<JobRow> _rows = new();
    private readonly Dictionary<int, Card> _byId = new();
    private bool _closed, _queued, _busy, _visible;
    private Android.App.Dialog? _dialog;
    // A lost reply can mean the Host committed; do not blindly repeat the action in this screen session.
    private readonly Dictionary<int, QueueDto> _uncertain = new();
    private QueueDto? _deleteInFlight, _deleteTargetInFlight, _moveInFlight, _moveTargetInFlight, _actionInFlight;
    private bool _renameInFlight;
    private int _moveConfirmed;
    private bool _movePending, _reviewSnapshotFresh;
    private Guid[] _moveJobs = [];
    private bool _createDraftOpen, _createInFlight, _createUncertain, _createDraftAvailable;
    private string _createDraft = "";
    private QueueDto? _renameBaseline;
    private string _renameDraft = "";

    /// <summary>Save a rename draft and uncertain in-flight outcome for the same Host.</summary>
    public void Save(Bundle state)
    {
        var uncertain = _uncertain.Values.Select(CopyQueue).ToList();
        foreach (var pending in new[] { _deleteInFlight, _deleteTargetInFlight, _moveInFlight, _moveTargetInFlight, _actionInFlight, _renameInFlight ? _renameBaseline : null })
            if (pending is not null && uncertain.All(q => q.Id != pending.Id)) uncertain.Add(CopyQueue(pending));
        if (_moveInFlight is not null || _movePending) { state.PutInt("move_confirmed", _moveConfirmed); state.PutBoolean("move_pending", true); state.PutStringArray("move_jobs", _moveJobs.Select(id => id.ToString()).ToArray()); }
        state.PutString("uncertain_queue_state", JsonSerializer.Serialize(new SettingsDto { Queues = uncertain }, IpcJsonContext.Default.SettingsDto));
        state.PutBoolean("create_open", _createDraftOpen);
        state.PutString("create_draft", _createDraft); state.PutBoolean("create_draft_available", _createDraftAvailable);
        state.PutBoolean("create_uncertain", _createUncertain || _createInFlight);
        // Keep the old key readable by older in-memory state, but persist generation in the DTOs above.
        if (_renameBaseline is null) return;
        state.PutString("rename_baseline", JsonSerializer.Serialize(_renameBaseline, IpcJsonContext.Default.QueueDto));
        state.PutString("rename_draft", _renameDraft);
        state.PutBoolean("rename_uncertain", _renameInFlight || _uncertain.ContainsKey(_renameBaseline.Id));
    }

    /// <summary>Restore only after the activity confirms the same Host and receives its snapshot.</summary>
    public void Restore(Bundle state)
    {
        if (_closed || _dialog is not null) return;
        if (state.GetString("uncertain_queue_state") is { } savedQueues)
        {
            try { foreach (var q in JsonSerializer.Deserialize(savedQueues, IpcJsonContext.Default.SettingsDto)?.Queues ?? []) _uncertain[q.Id] = q; }
            catch (JsonException) { }
        }
        foreach (var id in state.GetIntArray("uncertain_queues") ?? [])
            if (!_uncertain.ContainsKey(id)) _uncertain[id] = new QueueDto { Id = id, Generation = null, Name = "Queue " + id };
        _createUncertain = state.GetBoolean("create_uncertain");
        _createDraft = state.GetString("create_draft") ?? ""; _createDraftAvailable = state.GetBoolean("create_draft_available") || _createUncertain;
        _reviewSnapshotFresh = true;
        _movePending = state.GetBoolean("move_pending"); _moveConfirmed = state.GetInt("move_confirmed");
        _moveJobs = (state.GetStringArray("move_jobs") ?? []).Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
        if (_visible) Render();
        if (state.GetBoolean("move_pending"))
            _activity.ShowMessage($"{state.GetInt("move_confirmed")} moves were confirmed. Check all downloads before trying again; the last move may have completed.");
        if (state.GetBoolean("create_open") && !_createUncertain)
        {
            ShowCreateQueue(state.GetString("create_draft") ?? "");
            return;
        }
        if (state.GetString("rename_baseline") is not { } json)
        {
            if (_uncertain.Count > 0) _activity.ShowMessage("A queue change may have reached the Host. Check the queue and downloads before trying again.");
            return;
        }
        QueueDto? baseline;
        try { baseline = JsonSerializer.Deserialize(json, IpcJsonContext.Default.QueueDto); }
        catch (JsonException) { return; }
        if (baseline is null) return;
        _renameBaseline = baseline; _renameDraft = state.GetString("rename_draft") ?? baseline.Name;
        if (state.GetBoolean("rename_uncertain") || !HasGeneration(baseline)) { MarkUncertain(baseline); _reviewSnapshotFresh = true; if (_visible) Render(); return; }
        RenameQueue(baseline.Id, baseline, _renameDraft);
    }

    public QueuesScreen(MainActivity activity, View page, DownloadsModel downloads, QueueSettingsModel settings, Action<QueueDto?> openEditor, Action showSpeedGraph)
    {
        _activity = activity; _downloads = downloads; _settings = settings; _open = openEditor;
        page.FindViewById<View>(Resource.Id.queues_chart)!.Click += (_, _) => showSpeedGraph();
        _startLabelExpiry = new Java.Lang.Runnable(() =>
        {
            _startLabelExpiresAt = null;
            if (_visible && !_closed) Render();
        });
        InsetsListener.Pad(page, top: true, bottom: false);
        _cards = page.FindViewById<LinearLayout>(Resource.Id.queue_cards)!;
        var host = page.FindViewById<TextView>(Resource.Id.queues_host)!;
        host.Visibility = activity.Capabilities.IsRemote ? ViewStates.Visible : ViewStates.Gone;
        if (activity.Capabilities.IsRemote) host.Text = "Host: " + activity.HostName;
        _new = page.FindViewById<View>(Resource.Id.queue_new)!;
        _new.Enabled = downloads.SnapshotShown;
        _new.Click += (_, _) => { if (!_busy && downloads.SnapshotShown) ShowCreateQueue(_createDraftAvailable ? _createDraft : ""); };
        _notice = Label("Waiting for the selected Host…", 16, Resource.Attribute.colorOnSurfaceVariant);
        _notice.Clickable = true; _notice.Focusable = true;
        _notice.Click += async (_, _) => await ReviewUncertainAsync();
        _cards.AddView(_notice);
        downloads.SettingsChanged += ScheduleRender;
        downloads.Rows.CollectionChanged += OnRows;
        TrackRows();
    }
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) Render();
        else ArmStartLabelExpiry(null);
    }

    private void ArmStartLabelExpiry(DateTimeOffset? at)
    {
        if (_startLabelExpiresAt == at) return;
        _ui.RemoveCallbacks(_startLabelExpiry);
        _startLabelExpiresAt = at;
        if (at is { } expiry)
            _ui.PostDelayed(_startLabelExpiry, Math.Max(1L, (long)Math.Ceiling((expiry - DateTimeOffset.UtcNow).TotalMilliseconds)));
    }
    private void OnRows(object? sender, NotifyCollectionChangedEventArgs e) { TrackRows(); ScheduleRender(); }
    private void OnRow(object? sender, PropertyChangedEventArgs e) => ScheduleRender();
    private void TrackRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= OnRow;
        _rows.Clear();
        foreach (var row in _downloads.Rows) { _rows.Add(row); row.PropertyChanged += OnRow; }
    }
    private void ScheduleRender()
    {
        if (_closed || !_visible || _queued) return;
        _queued = true; _ui.Post(() => { _queued = false; if (!_closed && _visible) Render(); });
    }
    private TextView Label(string text, float size, int color)
    {
        var v = new TextView(_activity) { Text = text, TextSize = size };
        v.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, color))); return v;
    }
    private static void Text(TextView view, string value) { if (view.Text != value) view.Text = value; }
    private void Render()
    {
        if (_closed) return;
        _new.Enabled = !_busy && !_createUncertain && _downloads.SnapshotShown;
        bool needsReview = _createUncertain || _uncertain.Count > 0;
        _notice.Visibility = !_downloads.SnapshotShown || needsReview ? ViewStates.Visible : ViewStates.Gone;
        _notice.Text = !_downloads.SnapshotShown ? "Waiting for the selected Host…" : "Review unconfirmed queue changes";
        _notice.Enabled = !_busy && needsReview;
        if (!_downloads.SnapshotShown) return;
        var queues = _settings.Settings.Queues.OrderBy(q => q.Order).ThenBy(q => q.Id).ToArray();
        var ids = queues.Select(q => q.Id).ToHashSet();
        foreach (var id in _byId.Keys.Where(id => !ids.Contains(id)).ToArray()) { _cards.RemoveView(_byId[id].Root); _byId.Remove(id); }
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? firstExpiry = null;
        foreach (var queue in queues)
        {
            if (!_byId.TryGetValue(queue.Id, out var card)) { card = MakeCard(queue.Id); _byId.Add(queue.Id, card); }
            var index = Array.FindIndex(queues, q => q.Id == queue.Id) + 1;
            if (_cards.IndexOfChild(card.Root) != index) { _cards.RemoveView(card.Root); _cards.AddView(card.Root, index); }
            var jobs = _downloads.Rows.Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id).ToArray();
            var unfinished = jobs.Where(r => r.Group != RowGroup.Finished).ToArray();
            var active = unfinished.Count(r => r.Group == RowGroup.Active);
            var waiting = unfinished.Count(r => r.Group == RowGroup.Queued);
            var rate = jobs.Sum(r => r.Job.BytesPerSecond ?? 0);
            var schedules = _settings.Settings.Schedules.Where(x => x.QueueId == queue.Id).ToArray();
            bool scheduled = schedules.Any(x => x.Enabled);
            var upcoming = !queue.IsRunning && scheduled && queue.NextTimedStartAt is { } startsAt && startsAt > now
                ? startsAt : (DateTimeOffset?)null;
            string state = queue.IsRunning
                ? queue.ConditionWaitReason ?? (active > 0 ? "Running \u00b7 " + Fmt.Rate(rate) : "Ready for downloads")
                : upcoming is { } at ? "Scheduled for " + at.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)
                : scheduled ? "Schedule enabled" : "Stopped";
            if (upcoming is { } expiry && (firstExpiry is null || expiry < firstExpiry))
                firstExpiry = expiry;
            Text(card.Name, queue.Name); Text(card.State, state);
            card.State.SetTextColor(new Android.Graphics.Color(queue.IsRunning && queue.ConditionWaitReason is null
                ? _activity.GetColor(Resource.Color.state_done)
                : Motion.Attr(_activity, scheduled && !queue.IsRunning ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurfaceVariant)));
            Text(card.Action, queue.IsRunning ? "" : scheduled ? "Start now" : "Start");
            if ((card.Action.Icon is not null) != queue.IsRunning)
            {
                card.Action.InsetTop = queue.IsRunning ? 0 : card.InsetTop;
                card.Action.InsetBottom = queue.IsRunning ? 0 : card.InsetBottom;
                card.Action.SetIconResource(queue.IsRunning ? Resource.Drawable.ic_stop_outline : 0);
            }
            card.Action.ContentDescription = (queue.IsRunning ? "Stop " : "Start ") + queue.Name;
            var actionWidth = Motion.Dp(_activity, queue.IsRunning ? 48 : scheduled ? 104 : 80);
            if (card.Action.LayoutParameters is LinearLayout.LayoutParams actionLayout && actionLayout.Width != actionWidth)
            { actionLayout.Width = actionWidth; card.Action.LayoutParameters = actionLayout; }
            card.Action.Enabled = !_busy;
            string detail;
            if (schedules.Length == 0) detail = queue.IsRunning ? "Starts when downloads are added" : "No schedule";
            else if (schedules.Length > 1) detail = $"{schedules.Length} schedules";
            else
            {
                var schedule = schedules[0];
                string days = string.Equals(schedule.Kind, "oneTime", StringComparison.OrdinalIgnoreCase)
                    ? schedule.StartDate?.ToString("yyyy-MM-dd") ?? "One-time"
                    : DisplayDays(schedule.Days);
                string timing = schedule.TimedStartEnabled ? days + " " + schedule.StartTime +
                    (string.IsNullOrWhiteSpace(schedule.StopTime) ? "" : " to " + schedule.StopTime) : "At Host startup";
                if (schedule.StartOnHostStartup && schedule.TimedStartEnabled) timing += " \u00b7 at Host startup";
                detail = (schedule.Enabled ? "" : "Off: ") + timing;
            }
            if (queue.RequireUnmeteredNetwork == true) detail += " \u00b7 unmetered";
            if (queue.RequireCharging == true) detail += " \u00b7 charging";
            if (queue.RequireBatterySaverOff == true) detail += " \u00b7 battery saver off";
            if (queue.SpeedLimitBytesPerSecond is > 0) detail += " \u00b7 limit " + Fmt.Rate(queue.SpeedLimitBytesPerSecond.Value);
            Text(card.Schedule, detail);
            var known = unfinished.Where(r => r.Job.TotalBytes is > 0).ToArray();
            long bytes = queue.IsRunning
                ? known.Sum(r => r.Job.TotalBytes!.Value - Math.Clamp(r.Job.ReceivedBytes, 0L, r.Job.TotalBytes.Value))
                : known.Sum(r => r.Job.TotalBytes!.Value);
            string files = active > 0
                ? $"{active} of {unfinished.Length} running \u00b7 {waiting} waiting"
                : $"{unfinished.Length} {(unfinished.Length == 1 ? "file" : "files")}";
            if (known.Length > 0)
                files += " \u00b7 " + Fmt.Bytes(bytes) + (known.Length < unfinished.Length ? " known" : "") + (queue.IsRunning ? " left" : "");
            Text(card.Files, files);
            card.Root.ContentDescription = queue.Name + ", " + state + ". " + detail + ". " + files + ". Double tap to edit. Long press for queue actions.";
            double total = known.Sum(r => (double)r.Job.TotalBytes!.Value);
            double received = known.Sum(r => (double)Math.Clamp(r.Job.ReceivedBytes, 0L, r.Job.TotalBytes!.Value));
            bool showProgress = active > 0 && known.Length == unfinished.Length && total > 0 && received > 0;
            card.Progress.Visibility = showProgress ? ViewStates.Visible : ViewStates.Gone;
            if (showProgress) card.Progress.Progress = (int)Math.Clamp(1000 * received / total, 0, 1000);
        }
        ArmStartLabelExpiry(_visible ? firstExpiry : null);
    }
    private Card MakeCard(int id)
    {
        var root = new MaterialCardView(_activity) { Radius = Motion.Dp(_activity, 28), CardElevation = 0, StrokeWidth = 0, Clickable = true, Focusable = true };
        root.SetCardBackgroundColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer)));
        root.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Motion.Dp(_activity, 12) };
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var pad = Motion.Dp(_activity, 20); body.SetPadding(pad, pad, pad, pad);
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal }; header.SetGravity(GravityFlags.CenterVertical);
        var title = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var name = Label("", 22, Resource.Attribute.colorOnSurface); var state = Label("", 14, Resource.Attribute.colorOnSurfaceVariant);
        title.AddView(name); title.AddView(state); header.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        var action = new MaterialButton(_activity) { CornerRadius = Motion.Dp(_activity, 24) };
        var insetTop = action.InsetTop; var insetBottom = action.InsetBottom;
        action.TextSize = 14;
        action.IconGravity = MaterialButton.IconGravityTextStart;
        action.IconSize = Motion.Dp(_activity, 24);
        action.IconTint = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSecondaryContainer)));
        action.SetAllCaps(false); action.SetMinWidth(0); action.SetMinimumWidth(0); action.SetPadding(0, 0, 0, 0);
        action.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorSecondaryContainer)));
        action.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSecondaryContainer)));
        action.Click += async (_, _) => await ActionAsync(id);
        header.AddView(action, new LinearLayout.LayoutParams(Motion.Dp(_activity, 80), Motion.Dp(_activity, 48))); body.AddView(header);
        var schedule = Label("", 14, Resource.Attribute.colorOnSurfaceVariant);
        var files = Label("", 14, Resource.Attribute.colorOnSurfaceVariant);
        body.AddView(InfoRow(new ClockIcon(_activity), schedule, 12));
        var download = new ImageView(_activity); download.SetImageResource(Resource.Drawable.ic_flow_download);
        download.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        body.AddView(InfoRow(download, files, 12));
        var progress = new LinearProgressIndicator(_activity) { Max = 1000, TrackThickness = Motion.Dp(_activity, 6), TrackCornerRadius = Motion.Dp(_activity, 3), TrackStopIndicatorSize = 0, IndicatorTrackGapSize = 0 };
        progress.TrackColor = Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainerHighest);
        var progressLayout = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 12) };
        progress.ImportantForAccessibility = ImportantForAccessibility.No; body.AddView(progress, progressLayout);
        root.AddView(body);
        root.Click += (_, _) => { if (!_busy && MayChangeQueue(id) && _settings.Settings.Queues.FirstOrDefault(q => q.Id == id) is { } queue) _open(queue); };
        root.LongClickable = true;
        root.LongClick += (_, e) => { if (!_busy && !_closed) ShowQueueActions(id); e.Handled = true; };
        return new Card(root, name, state, schedule, files, action, progress, insetTop, insetBottom);
    }
    private View InfoRow(View icon, TextView text, int top)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, top) };
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        row.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 20), Motion.Dp(_activity, 20)));
        var textLayout = new LinearLayout.LayoutParams(0, -2, 1) { LeftMargin = Motion.Dp(_activity, 8) };
        row.AddView(text, textLayout); return row;
    }
    private sealed class ClockIcon : View
    {
        private readonly Android.Graphics.Paint _paint = new(Android.Graphics.PaintFlags.AntiAlias);
        public ClockIcon(MainActivity activity) : base(activity)
        {
            _paint.Color = new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorOnSurfaceVariant));
            _paint.SetStyle(Android.Graphics.Paint.Style.Stroke); _paint.StrokeWidth = Motion.Dp(activity, 1.5f);
            _paint.StrokeCap = Android.Graphics.Paint.Cap.Round;
        }
        protected override void OnDraw(Android.Graphics.Canvas canvas)
        {
            base.OnDraw(canvas);
            float cx = Width / 2f, cy = Height / 2f, r = Math.Min(Width, Height) * .37f;
            canvas.DrawCircle(cx, cy, r, _paint);
            canvas.DrawLine(cx, cy - r * .55f, cx, cy, _paint);
            canvas.DrawLine(cx, cy, cx + r * .45f, cy + r * .25f, _paint);
        }
    }
    private static string DisplayDays(string? days)
    {
        try { var parsed = ScheduleDays.FromHost(days); return parsed.Count == 7 ? "Every day" : ScheduleDays.Serialize(parsed).Replace(",", ", "); }
        catch (FormatException) { return days ?? ""; }
    }
    private QueueDto? CurrentQueue(int id) => _settings.Settings.Queues.FirstOrDefault(q => q.Id == id);
    private static bool HasGeneration(QueueDto queue) => queue.Generation is { } generation && generation != Guid.Empty;
    private bool SupportsIdentity => !_activity.Capabilities.IsRemote || _downloads.Hello?.Capabilities.Contains("queueIdentity") == true;
    private void MarkUncertain(QueueDto queue) { _uncertain[queue.Id] = CopyQueue(queue); _reviewSnapshotFresh = false; }
    private bool SameCurrent(QueueDto captured)
    {
        if (!SupportsIdentity || !HasGeneration(captured))
        {
            _activity.ShowMessage("Update the selected Host before changing queues safely."); return false;
        }
        if (CurrentQueue(captured.Id) is not { } current || current.Generation != captured.Generation)
        {
            _activity.ShowMessage("This queue changed or was replaced. Review the queue list before trying again."); return false;
        }
        return true;
    }
    private static QueueDto CopyQueue(QueueDto queue) => JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(queue, IpcJsonContext.Default.QueueDto), IpcJsonContext.Default.QueueDto)!;

    private void Track(Android.App.Dialog dialog)
    {
        _dialog = dialog;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_dialog, dialog)) _dialog = null; };
    }
    private void ShowQueueActions(int id)
    {
        if (_closed || _busy || CurrentQueue(id) is not { } queue || _dialog is not null) return;
        queue = CopyQueue(queue);
        var sheet = new BottomSheetDialog(_activity);
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var pad = Motion.Dp(_activity, 20); body.SetPadding(pad, Motion.Dp(_activity, 12), pad, Motion.Dp(_activity, 28));
        var handle = new View(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        handle.SetBackgroundResource(Resource.Drawable.bg_handle);
        body.AddView(handle, new LinearLayout.LayoutParams(Motion.Dp(_activity, 32), Motion.Dp(_activity, 4))
            { Gravity = GravityFlags.CenterHorizontal, BottomMargin = Motion.Dp(_activity, 20) });
        var title = Label(queue.Name, 22, Resource.Attribute.colorOnSurface); title.AccessibilityHeading = true; body.AddView(title);
        bool first = true;
        void Option(string text, string subtitle, int iconResource, Action run)
        {
            if (!first)
            {
                var divider = new View(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
                divider.SetBackgroundColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainerHighest)));
                body.AddView(divider, new LinearLayout.LayoutParams(-1, Motion.Dp(_activity, 1)));
            }
            first = false;
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(Motion.Dp(_activity, 60)); row.SetPadding(Motion.Dp(_activity, 8), Motion.Dp(_activity, 10), Motion.Dp(_activity, 8), Motion.Dp(_activity, 10));
            row.Background = new TypedValueHolder(_activity).SelectableBackground();
            var icon = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            icon.SetImageResource(iconResource);
            icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
            row.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(_activity, 24), Motion.Dp(_activity, 24)));
            var labels = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            labels.AddView(Label(text, 17, Resource.Attribute.colorOnSurface));
            if (subtitle.Length != 0) labels.AddView(Label(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
            row.AddView(labels, new LinearLayout.LayoutParams(0, -2, 1) { MarginStart = Motion.Dp(_activity, 16) });
            row.ContentDescription = text + (subtitle.Length > 0 ? ". " + subtitle : "");
            row.Click += (_, _) => { sheet.Dismiss(); if (!_closed && !_busy) run(); };
            body.AddView(row);
        }
        Option("Edit schedule", "Times, conditions and limits", Resource.Drawable.ic_queue_schedule, () => { if (MayChangeQueue(id) && SameCurrent(queue) && CurrentQueue(id) is { } q) _open(q); });
        if (_renameBaseline is { } draftBaseline && draftBaseline.Id == queue.Id && draftBaseline.Generation == queue.Generation)
            Option("Resume rename draft", "Review the name you entered", Resource.Drawable.ic_queue_rename, () => RenameQueue(id, draftBaseline, _renameDraft));
        Option("Rename queue", "", Resource.Drawable.ic_queue_rename, () => RenameQueue(id, queue));
        if (!_activity.Capabilities.IsRemote || _downloads.Hello?.Capabilities.Contains("queueConditionalMove") == true)
            Option("Move downloads", "Choose another queue", Resource.Drawable.ic_queue_move, () => ChooseMoveDestination(queue));
        if (id != _settings.Settings.DefaultQueueId)
            Option("Delete queue", "Move downloads and keep files", Resource.Drawable.ic_remote_trash, () => ChooseDeleteDestination(queue));
        sheet.SetContentView(body); Track(sheet); sheet.Show();
    }
    private bool MayChangeQueue(int id)
    {
        if (!_uncertain.ContainsKey(id)) return true;
        _activity.ShowMessage("A previous change was not confirmed. Select Review unconfirmed queue changes before trying again.");
        return false;
    }
    private void RenameQueue(int id, QueueDto? savedBaseline = null, string? savedDraft = null)
    {
        if (_closed || _busy || (savedBaseline is null && !MayChangeQueue(id)) || CurrentQueue(id) is not { } current) return;
        var baseline = savedBaseline is null ? CopyQueue(current) : CopyQueue(savedBaseline);
        bool sameIdentity = SupportsIdentity && HasGeneration(baseline) && current.Generation == baseline.Generation;
        _renameBaseline = baseline; _renameDraft = savedDraft ?? baseline.Name;
        var wrapper = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; var pad = Motion.Dp(_activity, 24); wrapper.SetPadding(pad, 0, pad, 0);
        var fieldLayout = new TextInputLayout(_activity, null, Resource.Attribute.textInputOutlinedStyle) { Hint = "Queue name" };
        var field = new TextInputEditText(fieldLayout.Context!) { Text = _renameDraft, ContentDescription = "Queue name" };
        field.TextChanged += (_, _) => _renameDraft = field.Text ?? "";
        field.SetSingleLine(true); fieldLayout.AddView(field); wrapper.AddView(fieldLayout);
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Rename queue")!.SetView(wrapper)!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.SetPositiveButton("Save", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog);
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_renameBaseline, baseline)) _renameBaseline = null; };
        dialog.Show();
        var save = dialog.GetButton((int)DialogButtonType.Positive)!;
        var cancel = dialog.GetButton((int)DialogButtonType.Negative)!;
        if (_uncertain.ContainsKey(id) || !sameIdentity) { save.Enabled = false; fieldLayout.Error = sameIdentity ? "The rename may have reached the Host. Review unconfirmed queue changes before saving again." : "This queue identity changed or the Host must be updated. Review the current queue before editing."; }
        save.Click += async (_, _) =>
        {
            if (_closed || _busy || !MayChangeQueue(id) || !SameCurrent(baseline)) return;
            var name = (field.Text ?? "").Trim();
            if (name.Length is 0 or > 80) { fieldLayout.Error = "Enter a queue name up to 80 characters."; return; }
            var edited = CopyQueue(baseline); edited.Name = name;
            _busy = _renameInFlight = true; Render(); save.Enabled = cancel.Enabled = field.Enabled = false; dialog.SetCancelable(false); fieldLayout.Error = null;
            try
            {
                var result = await _settings.SaveQueueAsync(edited, baseline, _life.Token);
                if (_closed) return;
                _downloads.ApplySettingsResult(_settings.LastResult);
                if (result.Ok) { dialog.Dismiss(); return; }
                if (_settings.LastResult is null)
                {
                    MarkUncertain(baseline); fieldLayout.Error = "The change was not confirmed. Reconnect and check the queue before trying again."; save.Enabled = false;
                }
                else { fieldLayout.Error = result.Error ?? "The Host refused the change."; save.Enabled = true; }
            }
            catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
            finally { _renameInFlight = false; if (!_closed) { dialog.SetCancelable(true); cancel.Enabled = field.Enabled = true; _busy = false; Render(); } }
        };
    }
    private void ShowCreateQueue(string draft = "")
    {
        if (_closed || _busy || !_downloads.SnapshotShown || _dialog is not null) return;
        if (!SupportsIdentity || !_settings.CanWriteSafely) { _activity.ShowMessage("Update the selected Host before creating queues safely."); return; }
        _createDraftOpen = true; _createDraft = draft;
        var wrapper = new LinearLayout(_activity) { Orientation = Orientation.Vertical }; var pad = Motion.Dp(_activity, 24); wrapper.SetPadding(pad, 0, pad, 0);
        var explanation = Label("Keep downloads together with their own schedule and limits.", 14, Resource.Attribute.colorOnSurfaceVariant); wrapper.AddView(explanation);
        var fieldLayout = new TextInputLayout(_activity, null, Resource.Attribute.textInputOutlinedStyle) { Hint = "Queue name" };
        var field = new TextInputEditText(fieldLayout.Context!) { Text = _createDraft, ContentDescription = "Queue name" };
        field.TextChanged += (_, _) => _createDraft = field.Text ?? ""; field.SetSingleLine(true); fieldLayout.AddView(field);
        wrapper.AddView(fieldLayout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 12) });
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("New queue")!.SetView(wrapper)!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.SetPositiveButton("Create", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog); dialog.DismissEvent += (_, _) => _createDraftOpen = false; dialog.Show();
        var create = dialog.GetButton((int)DialogButtonType.Positive)!; var cancel = dialog.GetButton((int)DialogButtonType.Negative)!;
        if (_createUncertain) { create.Enabled = false; fieldLayout.Error = "The queue may have been created. Reconnect and check the queue list before creating it again."; }
        create.Click += async (_, _) =>
        {
            if (_closed || _busy || _createUncertain || !SupportsIdentity || !_settings.CanWriteSafely) return;
            var name = (field.Text ?? "").Trim();
            if (name.Length is 0 or > 80) { fieldLayout.Error = "Enter a queue name up to 80 characters."; return; }
            var before = _settings.Settings.Queues.Select(q => q.Id).ToHashSet();
            bool notices = !_activity.Capabilities.IsRemote || _downloads.Hello?.Capabilities.Contains("queueCompletionNotification") == true;
            var draftQueue = new QueueDto { Name = name, IsRunning = false, NotifyWhenFinished = notices ? true : null };
            _busy = _createInFlight = true; Render(); create.Enabled = cancel.Enabled = field.Enabled = false; dialog.SetCancelable(false); fieldLayout.Error = null;
            try
            {
                var result = await _settings.SaveQueueAsync(draftQueue, null, _life.Token);
                if (_closed) return;
                _downloads.ApplySettingsResult(_settings.LastResult);
                if (!result.Ok)
                {
                    if (_settings.LastResult is null) { _createUncertain = true; _createDraftAvailable = true; _reviewSnapshotFresh = false; fieldLayout.Error = "The result was not confirmed. Reconnect and check the queue list before trying again."; }
                    else { fieldLayout.Error = result.Error ?? "The Host refused to create the queue."; create.Enabled = true; }
                    return;
                }
                var created = _settings.LastResult!.Settings.Queues.Where(q => !before.Contains(q.Id)).ToArray();
                if (created.Length != 1 || !string.Equals(created[0].Name, name, StringComparison.Ordinal) || !HasGeneration(created[0]))
                {
                    _createUncertain = true; _createDraftAvailable = true; _reviewSnapshotFresh = false; fieldLayout.Error = "The Host saved the queue but its identity is uncertain. Check the queue list before trying again."; return;
                }
                var queue = CopyQueue(created[0]); _createDraftAvailable = false; dialog.Dismiss(); _open(queue);
            }
            catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
            finally { _createInFlight = false; if (!_closed) { dialog.SetCancelable(true); cancel.Enabled = field.Enabled = true; _busy = false; Render(); } }
        };
    }

    private void ChooseMoveDestination(QueueDto captured)
    {
        if (_closed || _busy || !MayChangeQueue(captured.Id) || !SameCurrent(captured)) return;
        var source = CopyQueue(captured);
        var targets = _settings.Settings.Queues.Where(q => q.Id != source.Id).OrderBy(q => q.Order).ThenBy(q => q.Id).Select(CopyQueue).ToArray();
        var labels = targets.Select(q => q.Name + (q.IsRunning ? " \u00b7 running" : " \u00b7 stopped")).Append("No queue").ToArray();
        int chosen = 0;
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Move downloads from " + source.Name)!
            .SetSingleChoiceItems(labels, chosen, (_, e) => chosen = e.Which)!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Continue", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog); dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            if (_closed || _busy || chosen < 0 || chosen > targets.Length || !SameCurrent(source)) return;
            var target = chosen == targets.Length ? null : targets[chosen];
            if (target is not null && (!MayChangeQueue(target.Id) || !SameCurrent(target))) return;
            dialog.Dismiss(); _ui.Post(() => ConfirmMove(source, target));
        };
    }
    private void ConfirmMove(QueueDto source, QueueDto? target)
    {
        if (_closed || _busy || !MayChangeQueue(source.Id) || !SameCurrent(source)) return;
        if (target is not null && (!MayChangeQueue(target.Id) || !SameCurrent(target))) return;
        var jobs = _downloads.Rows.Where(r => r.Job.IsQueued && r.Job.QueueId == source.Id).Select(r => r.Id).Distinct().ToArray();
        if (jobs.Length == 0) { _activity.ShowMessage("There are no downloads in this queue to move."); return; }
        string destination = target?.Name ?? "No queue";
        string effect = target is null ? "Queued downloads will wait until resumed." : target.IsRunning ? "The destination is running; eligible downloads may start." : "The destination is stopped; active downloads may pause.";
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Move downloads?")!
            .SetMessage($"Move {jobs.Length} downloads from {source.Name} to {destination}. {effect} Moves happen one at a time; a failure can leave a partial result.")!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Move", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog); dialog.Show();
        var move = dialog.GetButton((int)DialogButtonType.Positive)!; var cancel = dialog.GetButton((int)DialogButtonType.Negative)!;
        move.Click += async (_, _) =>
        {
            if (_closed || _busy || !SameCurrent(source) || (target is not null && !SameCurrent(target))) return;
            _busy = true; _moveInFlight = source; _moveTargetInFlight = target; _moveJobs = jobs; _moveConfirmed = 0; _movePending = true;
            Render(); move.Enabled = cancel.Enabled = false; dialog.SetCancelable(false);
            try
            {
                foreach (var job in jobs)
                {
                    var row = _downloads.Rows.FirstOrDefault(r => r.Id == job);
                    if (row is null || !row.Job.IsQueued || row.Job.QueueId != source.Id || !SameCurrent(source) || (target is not null && !SameCurrent(target)))
                    {
                        MarkUncertain(source); if (target is not null) MarkUncertain(target); dialog.Dismiss();
                        _activity.ShowMessage($"{_moveConfirmed} moves confirmed. Review all downloads before trying again."); return;
                    }
                    var result = await _downloads.MoveToQueueAsync(job, target?.Id, _life.Token, expectedQueueId: source.Id,
                        expectedSourceQueueGeneration: source.Generation, expectedDestinationQueueGeneration: target?.Generation);
                    if (_closed) return;
                    if (!result.Ok)
                    {
                        MarkUncertain(source); if (target is not null) MarkUncertain(target); dialog.Dismiss();
                        _activity.ShowMessage($"{_moveConfirmed} moves confirmed. Review all downloads before trying again; the last move may have completed."); return;
                    }
                    _moveConfirmed++;
                }
                _movePending = false; _moveJobs = []; dialog.Dismiss(); _activity.ShowMessage($"Moved {_moveConfirmed} downloads.");
            }
            catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
            finally { _moveInFlight = _moveTargetInFlight = null; if (!_closed) { dialog.SetCancelable(true); _busy = false; Render(); } }
        };
    }
    private void ChooseDeleteDestination(QueueDto captured)
    {
        if (_closed || _busy || !MayChangeQueue(captured.Id) || !SameCurrent(captured)) return;
        var queue = CopyQueue(captured);
        if (queue.Id == _settings.Settings.DefaultQueueId) { _activity.ShowMessage("The default queue cannot be deleted."); return; }
        var destinations = _settings.Settings.Queues.Where(q => q.Id != queue.Id).OrderBy(q => q.Order).Select(CopyQueue).ToArray();
        if (destinations.Length == 0) { _activity.ShowMessage("There is no other queue to receive these downloads."); return; }
        var labels = destinations.Select(q => q.Name + (q.IsRunning ? " \u00b7 running" : " \u00b7 stopped")).ToArray();
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Move downloads to")!.SetItems(labels, (_, e) =>
        {
            if (_closed || _busy || e.Which < 0 || e.Which >= destinations.Length) return;
            var destination = destinations[e.Which];
            _ui.Post(() => { if (!_closed) ConfirmDelete(queue, destination); });
        })!.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog); dialog.Show();
    }
    private void ConfirmDelete(QueueDto source, QueueDto target)
    {
        if (_closed || _busy || !MayChangeQueue(source.Id) || !MayChangeQueue(target.Id) || !SameCurrent(source) || !SameCurrent(target)) return;
        string state = target.IsRunning ? "It is running; eligible waiting downloads may start." : "It is stopped; downloads will wait.";
        bool removeSchedules = !_activity.Capabilities.IsRemote || _downloads.Hello?.Capabilities.Contains("queueDeleteSchedules") == true;
        var schedules = removeSchedules ? "This queue's schedules will be removed." : "This Host moves the queue's schedules to the destination.";
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Delete " + source.Name + "?")!
            .SetMessage("Move this queue's downloads to " + target.Name + ". Files are kept. " + schedules + " " + state + " If a move fails, the queue is kept and some downloads may already have moved.")!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Delete queue", (IDialogInterfaceOnClickListener?)null)!.Create()!;
        Track(dialog); dialog.Show();
        var save = dialog.GetButton((int)DialogButtonType.Positive)!; var cancel = dialog.GetButton((int)DialogButtonType.Negative)!;
        save.Click += async (_, _) =>
        {
            if (_closed || _busy || !SameCurrent(source) || !SameCurrent(target)) return;
            _busy = true; _deleteInFlight = source; _deleteTargetInFlight = target;
            _moveJobs = _downloads.Rows.Where(r => r.Job.IsQueued && r.Job.QueueId == source.Id).Select(r => r.Id).Distinct().ToArray();
            _moveConfirmed = 0; _movePending = true; Render(); save.Enabled = cancel.Enabled = false; dialog.SetCancelable(false);
            try
            {
                var result = await _settings.QueueActionAsync(new QueueActionRequest { Action = "delete", QueueId = source.Id, MoveContentsToQueueId = target.Id, RemoveSchedules = removeSchedules,
                    ExpectedQueueGeneration = source.Generation, ExpectedDestinationQueueGeneration = target.Generation }, _life.Token);
                if (_closed) return;
                _downloads.ApplySettingsResult(_settings.LastResult);
                if (result.Ok) { _movePending = false; _moveJobs = []; dialog.Dismiss(); _activity.ShowMessage(removeSchedules ? "Queue deleted; downloads moved and schedules removed." : "Queue deleted; downloads and schedules moved."); return; }
                // A refused delete may still have moved some downloads before failing.
                MarkUncertain(source); MarkUncertain(target); dialog.Dismiss();
                _activity.ShowMessage((result.Error ?? "The deletion was not confirmed.") + " Review the queue and downloads before trying again.");
            }
            catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
            finally { _deleteInFlight = _deleteTargetInFlight = null; if (!_closed) { dialog.SetCancelable(true); _busy = false; Render(); } }
        };
    }
    private async Task ActionAsync(int id)
    {
        if (_busy || _closed || !MayChangeQueue(id) || CurrentQueue(id) is not { } current) return;
        var queue = CopyQueue(current);
        if (!SameCurrent(queue)) return;
        _busy = true; _actionInFlight = queue; Render();
        try
        {
            var result = await _settings.QueueActionAsync(new QueueActionRequest { QueueId = id, Action = queue.IsRunning ? "stop" : "start", ExpectedQueueGeneration = queue.Generation }, _life.Token);
            if (_closed) return;
            _downloads.ApplySettingsResult(_settings.LastResult);
            if (!result.Ok)
            {
                if (_settings.LastResult is null) { MarkUncertain(queue); _activity.ShowMessage("The queue action was not confirmed. Review the latest state before trying again."); }
                else _activity.ShowMessage(result.Error ?? "The Host refused the queue action.");
            }
        }
        catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { _actionInFlight = null; if (!_closed) { _busy = false; Render(); } }
    }
    private async Task ReviewUncertainAsync()
    {
        if (_closed || _busy || _dialog is not null || (!_createUncertain && _uncertain.Count == 0)) return;
        if (!_downloads.SnapshotShown) { _activity.ShowMessage("Wait for the selected Host to reconnect before reviewing."); return; }
        if (!_reviewSnapshotFresh)
        {
            // Recreate obtains a new jobs snapshot from this Host before Restore allows review.
            _activity.Recreate(); return;
        }
        _busy = true; Render();
        try
        {
            var result = await _settings.RefreshAsync(_life.Token);
            if (_closed) return;
            _downloads.ApplySettingsResult(_settings.LastResult);
            if (!result.Ok || _settings.LastResult is not { Ok: true })
            { _activity.ShowMessage(result.Error ?? "Could not refresh this Host. Review is still required."); return; }
            var lines = new List<string>();
            if (_createUncertain) lines.Add("A queue may have been created. No queue is identified by its name. Check the list before creating another.");
            foreach (var old in _uncertain.Values)
            {
                var now = CurrentQueue(old.Id);
                if (now is null) lines.Add(old.Name + ": no longer present.");
                else if (!HasGeneration(old) || now.Generation != old.Generation) lines.Add(old.Name + ": its earlier identity is unverified or was replaced; current queue is " + now.Name + ".");
                else
                {
                    int count = _downloads.Rows.Count(r => r.Job.IsQueued && r.Job.QueueId == now.Id);
                    lines.Add(now.Name + ": " + (now.IsRunning ? "running" : "stopped") + $", {count} downloads.");
                }
            }
            if (_movePending)
            {
                lines.Add($"{_moveConfirmed} moves were confirmed before the interruption. Current membership of the selected downloads:");
                foreach (var id in _moveJobs)
                {
                    var row = _downloads.Rows.FirstOrDefault(r => r.Id == id);
                    if (row is null) { lines.Add("A selected download is no longer in the list."); continue; }
                    var queue = row.Job.IsQueued ? CurrentQueue(row.Job.QueueId)?.Name ?? "unknown queue" : "No queue";
                    lines.Add((string.IsNullOrWhiteSpace(row.Job.FileName) ? "Unnamed download" : row.Job.FileName) + ": " + queue);
                }
            }
            lines.Add("Acknowledging this review does not repeat any action. Any new operation requires a new confirmation.");
            var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Review unconfirmed changes")!.SetMessage(string.Join("\n\n", lines))!
                .SetNegativeButton("Keep reviewing", (IDialogInterfaceOnClickListener?)null)!
                .SetPositiveButton("I have reviewed", (_, _) =>
                {
                    if (_closed) return;
                    _uncertain.Clear(); _createUncertain = false; _movePending = false; _moveJobs = []; _reviewSnapshotFresh = false; Render();
                })!.Create()!;
            Track(dialog); dialog.Show();
        }
        catch (System.OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { if (!_closed) { _busy = false; Render(); } }
    }
    public void Dispose()
    {
        if (_closed) return; _closed = true; _dialog?.Dismiss(); _dialog = null; _life.Cancel(); _life.Dispose();
        _ui.RemoveCallbacksAndMessages(null); _startLabelExpiry.Dispose(); _downloads.SettingsChanged -= ScheduleRender; _downloads.Rows.CollectionChanged -= OnRows;
        foreach (var row in _rows) row.PropertyChanged -= OnRow; _rows.Clear(); _ui.Dispose();
    }
    private sealed record Card(MaterialCardView Root, TextView Name, TextView State, TextView Schedule, TextView Files, MaterialButton Action, LinearProgressIndicator Progress, int InsetTop, int InsetBottom);
}
