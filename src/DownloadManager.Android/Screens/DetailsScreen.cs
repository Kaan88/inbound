using System.Collections.Specialized;
using System.ComponentModel;
using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Inbound.App.Host;

namespace Inbound.App.Screens;

/// <summary>Details of one download (A6): the hero with percent, rate and the main buttons, the connections, and the list of facts.</summary>
internal sealed class DetailsScreen
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _model;
    private readonly JobRow _row;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextView _percent, _rate, _progress, _connTitle, _connNote;
    private readonly LinearProgressIndicator _bar;
    private readonly MaterialButton _main;
    private readonly View _again, _stop, _connCard;
    private readonly LinearLayout _segments, _info;
    private readonly Func<bool> _canAsk;
    private readonly List<(string Label, TextView Value, View Row)> _lines = new();
    private bool _closed;
    private bool _polling;
    private bool _visible = true;
    private bool _renderQueued;
    private Android.App.Dialog? _queueDialog;
    private IReadOnlyList<float> _fractions = Array.Empty<float>();

    /// <param name="canAsk">False while the Host can't be asked (a remote host that isn't connected): no polling then.</param>
    public DetailsScreen(MainActivity activity, DownloadsModel model, JobRow row, Func<bool> canAsk)
    {
        _activity = activity;
        _model = model;
        _row = row;
        _canAsk = canAsk;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.details, null, false)!;
        InsetsListener.Pad(Root, top: true, bottom: true);
        _percent = Root.FindViewById<TextView>(Resource.Id.hero_percent)!;
        _rate = Root.FindViewById<TextView>(Resource.Id.hero_rate)!;
        _progress = Root.FindViewById<TextView>(Resource.Id.hero_progress)!;
        _bar = Root.FindViewById<LinearProgressIndicator>(Resource.Id.hero_bar)!;
        _main = Root.FindViewById<MaterialButton>(Resource.Id.hero_main)!;
        _stop = Root.FindViewById<View>(Resource.Id.hero_stop)!;
        _stop.Click += (_, _) => { if (_row.Group == RowGroup.Active) Fire(_model.StopAsync(_row.Id), "Download stopped"); };
        _again = Root.FindViewById<View>(Resource.Id.hero_again)!;
        _connCard = Root.FindViewById<View>(Resource.Id.connections_card)!;
        _connTitle = Root.FindViewById<TextView>(Resource.Id.connections_title)!;
        _connNote = Root.FindViewById<TextView>(Resource.Id.connections_note)!;
        _segments = Root.FindViewById<LinearLayout>(Resource.Id.segments)!;
        _info = Root.FindViewById<LinearLayout>(Resource.Id.info_lines)!;
        Root.FindViewById<TextView>(Resource.Id.details_title)!.Text = row.Name;

        Root.FindViewById<View>(Resource.Id.details_back)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.details_more)!.Click += (_, _) => ShowMenu(Root.FindViewById<View>(Resource.Id.details_more)!);
        _main.Click += (_, _) => OnMain();
        _again.Click += (_, _) => Fire(_model.RedownloadAsync(_row.Id), "Added again");
        Root.FindViewById<View>(Resource.Id.hero_delete)!.Click += (_, _) => _activity.ConfirmDeleteFromDetails(_row);

        _row.PropertyChanged += OnRowChanged;
        _model.Rows.CollectionChanged += OnRows;
        _model.SettingsChanged += QueueRender;
        Render();
        StartPolling();
    }

    public View Root { get; }

    public JobRow Row => _row;

    /// <summary>Stops listening and polling; called when the page has gone, and when the activity is destroyed with it open.</summary>
    public void Dispose()
    {
        _closed = true;
        _queueDialog?.Dismiss(); _queueDialog = null;
        _ui.RemoveCallbacksAndMessages(null);
        _row.PropertyChanged -= OnRowChanged;
        _model.Rows.CollectionChanged -= OnRows;
        _model.SettingsChanged -= QueueRender;
    }

    /// <summary>
    /// The app went to the background (false) or came back (true): no polling while nothing is shown (findings 11, 15), and it
    /// starts again when the page is seen again.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) { QueueRender(); StartPolling(); }
    }

    // One render per batch of row changes (a progress update changes several properties), and the info lines are updated in
    // place, so TalkBack keeps its place while the download runs (finding 25).
    private void OnRowChanged(object? s, PropertyChangedEventArgs e)
    {
        QueueRender();
        StartPolling();
    }

    private void QueueRender()
    {
        if (_renderQueued || _closed) return;
        _renderQueued = true;
        _ui.Post(() => { _renderQueued = false; Render(); });
    }

    private void OnRows(object? s, NotifyCollectionChangedEventArgs e)
    {
        // The download was removed (here or elsewhere): there is nothing left to show.
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.Contains(_row) == true && !_closed) _activity.CloseRowPage(Root);
    }

    private void Render()
    {
        if (_closed) return;
        var j = _row.Job;
        var finished = _row.Group == RowGroup.Finished;
        Root.FindViewById<TextView>(Resource.Id.details_title)!.Text = _row.Name;
        var pct = DetailsInfo.Percent(_row);
        _percent.Visibility = pct.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        _percent.Text = pct;
        _rate.Text = DetailsInfo.RateLine(_row);
        _progress.Text = DetailsInfo.ProgressLine(_row, DateTimeOffset.Now);
        _bar.Visibility = _row.ShowBar && !finished ? ViewStates.Visible : ViewStates.Gone;
        if (_row.ShowBar)
        {
            if (_row.Indeterminate) { if (!_bar.Indeterminate) _bar.Indeterminate = true; }
            else { if (_bar.Indeterminate) _bar.Indeterminate = false; _bar.SetProgressCompat((int)(_row.Progress * 1000), true); }
        }

        // The main button is what you most likely want now.
        var (text, icon) = _row.Group switch
        {
            RowGroup.Active => ("Pause", Resource.Drawable.ic_pause),
            RowGroup.Finished => ("Open", Resource.Drawable.ic_open),
            RowGroup.Failed => ("Retry", Resource.Drawable.ic_refresh),
            _ => ("Resume", Resource.Drawable.ic_play),
        };
        if (MayRefresh && SuggestRefresh) text = "Refresh link";
        _main.Text = text;
        _main.SetIconResource(icon);
        // A finished file on another Inbound is on that machine: no Open here (ANDROID.7).
        _main.Visibility = finished && (_row.Action == RowAction.None || !_activity.Capabilities.CanOpenFiles) ? ViewStates.Gone : ViewStates.Visible;
        _stop.Visibility = _row.Group == RowGroup.Active && _model.Hello?.Capabilities.Contains(JobActionRequest.StopUnfinishedCapability) == true ? ViewStates.Visible : ViewStates.Gone;
        _again.Visibility = _row.Group is RowGroup.Finished or RowGroup.Failed ? ViewStates.Visible : ViewStates.Gone;

        var running = _row.Group is RowGroup.Active or RowGroup.Paused;
        _connCard.Visibility = running ? ViewStates.Visible : ViewStates.Gone;
        _connTitle.Text = DetailsInfo.ConnectionsTitle(_row);
        _connNote.Text = running ? "Part file in " + DetailsInfo.FolderName(j.DestinationDirectory) : "";
        BindSegments();

        var sharedDownloadFolder = _activity.Capabilities.IsRemote ? null : AndroidHost.SharedDownloadFolder;
        var lines = DetailsInfo.Lines(j, _model.Settings, DateTimeOffset.Now, sharedDownloadFolder).ToList();
        if (lines.Count == _lines.Count && lines.Select(l => l.Label).SequenceEqual(_lines.Select(l => l.Label)))
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (_lines[i].Value.Text == lines[i].Value) continue;
                _lines[i].Value.Text = lines[i].Value;
                _lines[i].Row.ContentDescription = lines[i].Label + ", " + lines[i].Value;
            }

            return;
        }

        _info.RemoveAllViews();
        _lines.Clear();
        foreach (var line in lines) _info.AddView(InfoRow(line));
    }

    private View InfoRow(InfoLine line)
    {
        var ctx = _activity;
        var row = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Motion.Dp(ctx, 14), 0, Motion.Dp(ctx, 14));
        var label = new TextView(ctx) { Text = line.Label };
        label.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        label.SetTextColor(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnSurfaceVariant)));
        row.AddView(label, new LinearLayout.LayoutParams(Motion.Dp(ctx, 96), ViewGroup.LayoutParams.WrapContent));
        var value = new TextView(ctx) { Text = line.Value };
        value.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        value.SetTextColor(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnSurface)));
        row.AddView(value, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.ContentDescription = line.Label + ", " + line.Value;
        _lines.Add((line.Label, value, row));
        return row;
    }

    private void BindSegments()
    {
        var ctx = _activity;
        _segments.ContentDescription = _fractions.Count == 0 ? null : $"{_fractions.Count} connections, " + DetailsInfo.Percent(_row);
        while (_segments.ChildCount > _fractions.Count) _segments.RemoveViewAt(_segments.ChildCount - 1);
        for (var i = 0; i < _fractions.Count; i++)
        {
            LinearProgressIndicator bar;
            if (i < _segments.ChildCount) bar = (LinearProgressIndicator)_segments.GetChildAt(i)!;
            else
            {
                bar = new LinearProgressIndicator(ctx) { Max = 1000, TrackThickness = Motion.Dp(ctx, 6), TrackCornerRadius = Motion.Dp(ctx, 3), TrackStopIndicatorSize = 0, IndicatorTrackGapSize = 0 };
                bar.TrackColor = Motion.Attr(ctx, Resource.Attribute.colorSurfaceContainerHighest);
                bar.ImportantForAccessibility = ImportantForAccessibility.No;
                var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = i == 0 ? 0 : Motion.Dp(ctx, 6) };
                _segments.AddView(bar, lp);
            }

            var done = _fractions[i] >= 1f;
            bar.SetIndicatorColor(done ? ctx.GetColor(Resource.Color.state_done) : Motion.Attr(ctx, Resource.Attribute.colorPrimary));
            bar.SetProgressCompat((int)(_fractions[i] * 1000), true);
        }
    }

    // One request a second while the page is shown, the download runs and the Host can be asked; nothing runs otherwise.
    private bool MayPoll => !_closed && _visible && _row.Group == RowGroup.Active && _canAsk();

    private void StartPolling()
    {
        if (_polling || !MayPoll) return;
        _polling = true;
        void Tick()
        {
            if (!MayPoll) { _polling = false; return; }
            _ = Task.Run(async () =>
            {
                IReadOnlyList<SegmentDto> segments;
                try { segments = await _model.SegmentsAsync(_row.Id).ConfigureAwait(false); }
                catch (Exception) { segments = Array.Empty<SegmentDto>(); }
                _activity.RunOnUiThread(() =>
                {
                    if (_closed) return;
                    _fractions = DetailsInfo.SegmentFractions(segments);
                    BindSegments();
                    _ui.PostDelayed(Tick, 1000);
                });
            });
        }

        Tick();
    }

    private bool MayRefresh => !_row.Job.IsRemote && _row.Group is RowGroup.Failed or RowGroup.Paused or RowGroup.Queued;
    private bool SuggestRefresh => _row.Job.Failure?.Kind.Equals("expiredUrl", StringComparison.OrdinalIgnoreCase) == true
        || _row.Job.Failure?.HttpStatus is 401 or 403 or 410;

    private void OnMain()
    {
        if (MayRefresh && SuggestRefresh) { _activity.ShowRefreshLink(_row); return; }
        switch (_row.Group)
        {
            case RowGroup.Active: Fire(_model.PauseAsync(_row.Id), null); break;
            case RowGroup.Failed: Fire(_model.RetryAsync(_row.Id), null); break;
            case RowGroup.Finished:
                if (!_activity.Capabilities.CanOpenFiles) break;
                if (!Outside.OpenFile(_activity, _row.Job.FinalFilePath)) _activity.ShowMessage("The file could not be opened. It may have been moved or deleted.");
                break;
            default: Fire(_model.ResumeAsync(_row.Id), null); break;
        }
    }

    private void Fire(Task<CommandResult> command, string? okMessage) =>
        _ = Task.Run(async () =>
        {
            var r = await command.ConfigureAwait(false);
            if (!r.Ok) _activity.ShowMessage(r.Error ?? "That did not work.");
            else if (okMessage is not null) _activity.ShowMessage(okMessage);
        });

    private void ChooseQueue()
    {
        if (_closed || _queueDialog is not null) return;
        if (_activity.Capabilities.IsRemote && _model.Hello?.Capabilities.Contains("queueIdentity") != true)
        { _activity.ShowMessage("Update this Host before moving downloads between queues safely."); return; }
        var queues = _model.Settings.Queues.OrderBy(q => q.Order).ThenBy(q => q.Id).ToArray();
        var sourceId = _row.Job.IsQueued ? (int?)_row.Job.QueueId : null;
        var sourceGeneration = queues.FirstOrDefault(q => q.Id == sourceId)?.Generation;
        var names = new[] { "No queue" }.Concat(queues.Select(q => q.Name)).ToArray();
        int selected = _row.Job.IsQueued ? Array.FindIndex(queues, q => q.Id == _row.Job.QueueId) + 1 : 0;
        _queueDialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Queue on " + _activity.HostName)!
            .SetSingleChoiceItems(names, selected, (dialog, e) =>
            {
                ((Android.App.Dialog)dialog!).Dismiss(); _queueDialog = null;
                if (_closed) return;
                var id = e.Which == 0 ? (int?)null : queues[e.Which - 1].Id;
                var destinationGeneration = e.Which == 0 ? (Guid?)null : queues[e.Which - 1].Generation;
                if ((sourceId is not null && (sourceGeneration is null || sourceGeneration == Guid.Empty)) ||
                    (id is not null && (destinationGeneration is null || destinationGeneration == Guid.Empty)))
                { _activity.ShowMessage("Queue identity is unavailable. Reopen this page after updating the Host."); return; }
                Fire(_model.MoveToQueueAsync(_row.Id, id, expectedQueueId: sourceId,
                    expectedSourceQueueGeneration: sourceGeneration, expectedDestinationQueueGeneration: destinationGeneration), "Queue changed");
            })!.SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!.Create();
        _queueDialog!.DismissEvent += (_, _) => _queueDialog = null;
        _queueDialog.Show();
    }

    private void ShowMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        if (!_row.Job.AddressHidden) menu.Menu!.Add(0, 1, 0, "Copy address");
        if (_row.Group == RowGroup.Finished && _activity.Capabilities.CanShowInFolder) menu.Menu!.Add(0, 2, 1, "Show in folder");
        if (MayRefresh) menu.Menu!.Add(0, 3, 2, "Refresh link");
        menu.Menu!.Add(0, 4, 3, "Move to queue");
        if (menu.Menu!.Size() == 0) return;
        menu.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) { Outside.CopyText(_activity, "Address", _row.Job.Url); _activity.ShowMessage("Address copied"); }
            else if (e.Item.ItemId == 3) _activity.ShowRefreshLink(_row);
            else if (e.Item.ItemId == 4) ChooseQueue();
            else if (!Outside.ShowDownloadFolder(_activity)) _activity.ShowMessage("No app on this phone can show the folder.");
        };
        menu.Show();
    }
}
