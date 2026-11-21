using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// Add torrent (t1, t2): a magnet, "Download with" This phone or TorBox, what TorBox says about it (ready with its files, or not yet),
/// where to save and Send to TorBox. Torrents on the phone itself come in a later step, so that segment is shown but off.
/// </summary>
internal sealed class AddTorrentScreen
{
    private const int DebounceMs = 600;
    private readonly MainActivity _activity;
    private readonly DownloadsModel _host;
    private readonly TorBoxClient _client;
    private readonly TorBoxAddModel _model;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextInputEditText _magnet, _saveTo;
    private readonly TextInputLayout _magnetLayout;
    private readonly MaterialCardView _statusCard, _missCard;
    private readonly View _filesCard;
    private readonly TextView _statusTitle, _statusSub, _filesSummary, _error;
    private readonly ImageView _statusIcon;
    private readonly LinearLayout _filesList;
    private readonly MaterialButton _send;
    private readonly CancellationTokenSource _life = new();
    private Java.Lang.IRunnable? _pending;
    private string? _destination;
    private Action? _showSave;
    private bool _busy, _checking, _disposed;

    public AddTorrentScreen(MainActivity activity, DownloadsModel host, TorBoxClient client)
    {
        _activity = activity;
        _host = host;
        _client = client;
        _model = new TorBoxAddModel(client);
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.add_torrent, null, false)!;
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.torrent_top)!, top: true, bottom: false);
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.torrent_bottom)!, top: false, bottom: true);
        _magnet = Root.FindViewById<TextInputEditText>(Resource.Id.magnet)!;
        _magnetLayout = Root.FindViewById<TextInputLayout>(Resource.Id.magnet_layout)!;
        _statusCard = Root.FindViewById<MaterialCardView>(Resource.Id.status_card)!;
        _statusTitle = Root.FindViewById<TextView>(Resource.Id.status_title)!;
        _statusSub = Root.FindViewById<TextView>(Resource.Id.status_sub)!;
        _statusIcon = Root.FindViewById<ImageView>(Resource.Id.status_icon)!;
        _filesSummary = Root.FindViewById<TextView>(Resource.Id.files_summary)!;
        _filesCard = Root.FindViewById<View>(Resource.Id.files_card)!;
        _filesList = Root.FindViewById<LinearLayout>(Resource.Id.files_list)!;
        _missCard = Root.FindViewById<MaterialCardView>(Resource.Id.miss_card)!;
        _saveTo = Root.FindViewById<TextInputEditText>(Resource.Id.torrent_save_to)!;
        _send = Root.FindViewById<MaterialButton>(Resource.Id.torrent_send)!;
        _error = Root.FindViewById<TextView>(Resource.Id.torrent_error)!;
        Root.FindViewById<TextView>(Resource.Id.miss_title)!.Text = TorBoxText.MissTitle;
        Root.FindViewById<TextView>(Resource.Id.miss_body)!.Text = TorBoxText.MissBody;
        Root.FindViewById<TextView>(Resource.Id.with_hint)!.Text = TorBoxText.TorBoxHint(!activity.Capabilities.IsRemote, activity.HostName);

        var group = Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.with_group)!;
        var phone = Root.FindViewById<MaterialButton>(Resource.Id.with_phone)!;
        var torbox = Root.FindViewById<MaterialButton>(Resource.Id.with_torbox)!;
        // The phone's own torrents are a later step: the segment is drawn as designed but cannot be chosen.
        phone.Enabled = false;
        phone.ContentDescription = "This phone. " + TorBoxText.ThisPhoneLater;
        torbox.SetIconResource(Resource.Drawable.ic_check);
        group.Check(Resource.Id.with_torbox);

        _magnetLayout.SetEndIconOnClickListener(new ClickListener(() =>
        {
            if (Outside.ClipboardText(_activity) is { Length: > 0 } text) _magnet.Text = text.Trim();
            else _activity.ShowMessage("Nothing on the clipboard");
        }));
        _magnet.TextChanged += (_, _) => OnMagnet();
        Root.FindViewById<View>(Resource.Id.torrent_close)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.torrent_cancel)!.Click += (_, _) => _activity.CloseRowPage(Root);
        _send.Click += (_, _) => Send();
        _statusCard.Click += (_, _) => { if (_model.Check == TorBoxCheck.NoKey) ChangeKey(); };
        SetUpSaveTo();
        Render();
    }

    public View Root { get; }

    public void FocusMagnet()
    {
        _magnet.RequestFocus();
        _magnet.Post(() => (_activity.GetSystemService(Android.Content.Context.InputMethodService) as Android.Views.InputMethods.InputMethodManager)
            ?.ShowSoftInput(_magnet, 0));
    }

    public void Dispose()
    {
        _disposed = true;
        if (_pending is not null) _ui.RemoveCallbacks(_pending);
        _life.Cancel();
        if (_showSave is not null) _host.SettingsChanged -= _showSave;
    }

    private void OnMagnet()
    {
        _error.Visibility = ViewStates.Gone;
        _magnetLayout.Error = null;
        if (_pending is not null) _ui.RemoveCallbacks(_pending);
        var text = _magnet.Text ?? "";
        _checking = TorBoxAddModel.IsMagnet(text);
        // The old answer is about other text: nothing of it stays on screen or can be sent meanwhile.
        _model.Reset();
        Render();
        _pending = new Java.Lang.Runnable(() => { _pending = null; _ = CheckAsync(text); });
        _ui.PostDelayed(_pending, DebounceMs);
    }

    private async Task CheckAsync(string text)
    {
        if (_disposed) return;
        bool applied;
        try { applied = await Task.Run(() => _model.CheckAsync(text, _life.Token)); }
        catch (System.OperationCanceledException) { return; }
        if (!applied || _disposed) return;
        _checking = false;
        Render();
    }

    private void ChangeKey() => TorBoxKeySheet.Show(_activity, _client, false, _activity.Capabilities.IsRemote ? _activity.HostName : "", () =>
    {
        _model.ForgetKeyState();
        OnMagnet();
    });

    private void Render()
    {
        if (_disposed) return;
        var check = _checking ? TorBoxCheck.Checking : _model.Check;
        var tree = check == TorBoxCheck.Hit ? _model.Tree : null;
        _magnetLayout.Error = check == TorBoxCheck.NotMagnet && (_magnet.Text?.Length ?? 0) > 0 ? _model.Message : null;
        (int Container, string? Title, string? Sub, int Icon) status = check switch
        {
            TorBoxCheck.Checking => (Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer), "Checking TorBox…", "Looking for it in TorBox's cache", Resource.Drawable.ic_flow_bolt),
            TorBoxCheck.Hit => (_activity.GetColor(Resource.Color.state_done_container), _model.ReadyTitle, "Downloads start at once", Resource.Drawable.ic_flow_bolt),
            TorBoxCheck.Miss => (_activity.GetColor(Resource.Color.state_waiting_container), "Not on TorBox yet", "No file list until TorBox has fetched it", Resource.Drawable.ic_flow_info),
            TorBoxCheck.Unknown => (Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer), "Couldn't check TorBox's cache", "You can still send it", Resource.Drawable.ic_flow_info),
            TorBoxCheck.NoKey => (_activity.GetColor(Resource.Color.state_waiting_container), "TorBox key needed", _model.Message + " Tap to add it.", Resource.Drawable.ic_settings_site_key),
            TorBoxCheck.Failed => (_activity.GetColor(Resource.Color.state_failed_container), "TorBox could not check it", _model.Message, Resource.Drawable.ic_flow_info),
            _ => (0, null, null, 0),
        };
        _statusCard.Visibility = status.Title is null ? ViewStates.Gone : ViewStates.Visible;
        if (status.Title is not null)
        {
            _statusCard.SetCardBackgroundColor(status.Container);
            _statusTitle.Text = status.Title;
            _statusSub.Text = status.Sub;
            _statusIcon.SetImageResource(status.Icon);
            _statusIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(check switch
            {
                TorBoxCheck.Hit => _activity.GetColor(Resource.Color.state_done),
                TorBoxCheck.Miss or TorBoxCheck.NoKey => _activity.GetColor(Resource.Color.state_waiting),
                TorBoxCheck.Failed => _activity.GetColor(Resource.Color.state_failed),
                _ => Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant),
            }));
            _statusCard.Clickable = check == TorBoxCheck.NoKey;
            _statusCard.ContentDescription = status.Title + ". " + status.Sub;
        }

        _missCard.Visibility = check is TorBoxCheck.Hit or TorBoxCheck.Miss or TorBoxCheck.Unknown ? ViewStates.Visible : ViewStates.Gone;
        _filesSummary.Visibility = _filesCard.Visibility = tree is null ? ViewStates.Gone : ViewStates.Visible;
        if (tree is not null)
        {
            _filesSummary.Text = tree.Summary;
            TorBoxTreeView.Render(_activity, _filesList, tree, false, () => { _filesSummary.Text = tree.Summary; UpdateSend(); });
        }
        else _filesList.RemoveAllViews();
        UpdateSend();
    }

    private void UpdateSend()
    {
        _send.Enabled = !_busy && !_checking && _model.CanSend;
        _send.Text = _busy ? "Sending…" : "Send to TorBox";
    }

    private void Send()
    {
        if (_busy || !_model.CanSend) return;
        _busy = true;
        _error.Visibility = ViewStates.Gone;
        UpdateSend();
        var hit = _model.Check == TorBoxCheck.Hit;
        TorBoxUi.Run(_activity, () => _model.SendAsync(_destination, _life.Token), result =>
        {
            _busy = false;
            UpdateSend();
            if (result.Done)
            {
                _activity.CloseRowPage(Root);
                _activity.ShowMessage(hit ? "Added to Downloads from TorBox" : "Sent to TorBox. It waits on the TorBox list and starts when it's ready.");
                return;
            }

            _error.Text = result.Error ?? result.Note;
            _error.Visibility = ViewStates.Visible;
        }, words => { _busy = false; UpdateSend(); _error.Text = words; _error.Visibility = ViewStates.Visible; });
    }

    private void SetUpSaveTo()
    {
        var remote = _activity.Capabilities.IsRemote;
        var layout = Root.FindViewById<TextInputLayout>(Resource.Id.torrent_save_layout)!;
        if (remote) layout.Hint = "Save to, on " + _activity.HostName;
        void Show()
        {
            var path = _destination ?? _host.DefaultDownloadDirectory;
            var full = SavePathWords.ForField(path);
            void Fit()
            {
                var room = _saveTo.Width - _saveTo.CompoundPaddingLeft - _saveTo.CompoundPaddingRight;
                var cut = room <= 0 ? full : MiddleEllipsis.Cut(full, s => _saveTo.Paint!.MeasureText(s) <= room);
                if (_saveTo.Text != cut) _saveTo.Text = cut;
            }

            _saveTo.Text = full;
            _saveTo.ContentDescription = SavePathWords.Label(remote ? _activity.HostName : null, path);
            var offered = FolderPicker.Offered(_host);
            _saveTo.Enabled = offered; _saveTo.Focusable = false; _saveTo.Clickable = offered; _saveTo.LongClickable = false;
            layout.EndIconContentDescription = offered ? "Choose a folder on " + _activity.HostName : null;
            _saveTo.Post(Fit);
        }

        void Choose()
        {
            if (FolderPicker.Offered(_host))
                FolderSheet.Show(_activity, _host, _destination ?? _host.DefaultDownloadDirectory, folder => { _destination = folder; Show(); }, _life.Token);
        }

        Show();
        _showSave = Show;
        _host.SettingsChanged += Show;
        _saveTo.Click += (_, _) => Choose();
        layout.SetEndIconOnClickListener(new ClickListener(Choose));
    }
}
