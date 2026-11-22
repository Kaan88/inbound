using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// Add torrent (t1, t2): a magnet or a .torrent file, "Download with" This phone (the Host's own torrent engine: a file list found through
/// peers, the files to take, Download) or TorBox (what TorBox says about it, Send to TorBox), and where to save. With "This phone" a pasted
/// magnet is handed to the Host at once only to find its file list (nothing downloads, and Cancel takes it back); the files are named with
/// Download.
/// </summary>
internal sealed class AddTorrentScreen
{
    private const int DebounceMs = 600;
    private const int WaitTickMs = 1000;
    private const int MaxTorrentFileBytes = 8 * 1024 * 1024;
    private readonly MainActivity _activity;
    private readonly DownloadsModel _host;
    private readonly TorBoxClient? _client;
    private readonly TorrentClient? _torrents;
    private readonly TorBoxAddModel? _model;
    private readonly TorrentAddModel? _phone;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextInputEditText _magnet, _saveTo;
    private readonly TextInputLayout _magnetLayout;
    private readonly MaterialCardView _statusCard, _missCard;
    private readonly View _filesCard;
    private readonly TextView _statusTitle, _statusSub, _filesSummary, _error, _withHint;
    private readonly ImageView _statusIcon;
    private readonly LinearLayout _filesList;
    private readonly MaterialButton _send, _withPhone, _withTorBox;
    private readonly MaterialButtonToggleGroup _withGroup;
    private readonly CancellationTokenSource _life = new();
    private Java.Lang.IRunnable? _pending, _waitTick;
    private string? _destination;
    private Action? _showSave;
    private bool _busy, _checking, _disposed, _phoneMode, _settingText;

    /// <param name="client">Null when the Host does not offer TorBox: its segment is off.</param>
    /// <param name="torrents">Null when the Host does not answer torrent.*: the This phone segment is off.</param>
    public AddTorrentScreen(MainActivity activity, DownloadsModel host, TorBoxClient? client, TorrentClient? torrents, TorBoxChoice choice)
    {
        _activity = activity;
        _host = host;
        _client = client;
        _torrents = torrents;
        if (client is not null) _model = new TorBoxAddModel(client);
        if (torrents is not null) _phone = new TorrentAddModel(torrents);
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
        _withHint = Root.FindViewById<TextView>(Resource.Id.with_hint)!;
        Root.FindViewById<TextView>(Resource.Id.miss_title)!.Text = TorBoxText.MissTitle;
        Root.FindViewById<TextView>(Resource.Id.miss_body)!.Text = TorBoxText.MissBody;

        _withGroup = Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.with_group)!;
        _withPhone = Root.FindViewById<MaterialButton>(Resource.Id.with_phone)!;
        _withTorBox = Root.FindViewById<MaterialButton>(Resource.Id.with_torbox)!;
        var remote = activity.Capabilities.IsRemote;
        // The segment names say which Host does the work. A segment the Host can't serve stays drawn but can't be chosen, with its reason.
        // t1: "This phone" on the phone; on a paired PC or server, its name.
        var with = remote ? activity.HostName : "This phone";
        _withPhone.Text = with;
        _withPhone.SetMaxLines(1);
        _withPhone.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _withPhone.Enabled = torrents is not null;
        _withPhone.ContentDescription = torrents is null ? with + ". " + TorrentText.NotOffered : null;
        _withTorBox.Enabled = client is not null;
        _withTorBox.ContentDescription = client is null ? "TorBox. This Host does not offer TorBox." : null;
        // Which segment opens chosen: the switch in Settings (Send magnets to TorBox first), and whatever the Host offers.
        var phoneOffered = torrents is not null && host.Settings.TorrentsEnabled;
        _phoneMode = !choice.OpensWithTorBox(client is not null, phoneOffered) && torrents is not null;
        _withGroup.Check(_phoneMode ? Resource.Id.with_phone : Resource.Id.with_torbox);
        _withGroup.AddOnButtonCheckedListener(new ToggleListener((id, on) =>
        {
            if (!on || _disposed) return;
            var phone = id == Resource.Id.with_phone;
            if (phone == _phoneMode) return;
            SetMode(phone);
        }));
        if (torrents is null)
        {
            // Only TorBox: the field is a magnet field with a paste button, as before.
            _magnetLayout.Hint = "Magnet link";
            _magnetLayout.SetEndIconDrawable(Resource.Drawable.ic_paste);
            _magnetLayout.EndIconContentDescription = "Paste from the clipboard";
        }

        _magnetLayout.SetEndIconOnClickListener(new ClickListener(() =>
        {
            if (torrents is null)
            {
                if (Outside.ClipboardText(_activity) is { Length: > 0 } text) { _magnet.Text = text.Trim(); _magnet.SetSelection(0); }
                else _activity.ShowMessage("Nothing on the clipboard");
            }
            else if (!_activity.PickTorrentFile(OnFilePicked)) _activity.ShowMessage("No file chooser is available on this phone.");
        }));
        _magnet.TextChanged += (_, _) => { if (!_settingText) OnMagnet(); };
        // t1: a long magnet shows its start ("magnet:?xt=urn:btih:…"), not its last tracker, once the field is left.
        _magnet.FocusChange += (_, e) => { if (!e.HasFocus) _magnet.SetSelection(0); };
        Root.FindViewById<View>(Resource.Id.torrent_close)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.torrent_cancel)!.Click += (_, _) => _activity.CloseRowPage(Root);
        _send.Click += (_, _) => Send();
        _statusCard.Click += (_, _) => { if (!_phoneMode && _model?.Check == TorBoxCheck.NoKey) ChangeKey(); };
        SetUpSaveTo();
        UpdateSegments();
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
        if (_waitTick is not null) _ui.RemoveCallbacks(_waitTick);
        _life.Cancel();
        // A magnet handed to the Host only to find its files, and never downloaded, is taken back.
        _phone?.Reset();
        _ = RemoveStraysAsync();
        if (_showSave is not null) _host.SettingsChanged -= _showSave;
    }

    private void SetMode(bool phone)
    {
        _phoneMode = phone;
        UpdateSegments();
        _error.Visibility = ViewStates.Gone;
        OnMagnet();
    }

    private void UpdateSegments()
    {
        _withPhone.SetIconResource(_phoneMode ? Resource.Drawable.ic_check : 0);
        _withTorBox.SetIconResource(_phoneMode ? 0 : Resource.Drawable.ic_check);
        if (_withGroup.CheckedButtonId != (_phoneMode ? Resource.Id.with_phone : Resource.Id.with_torbox))
            _withGroup.Check(_phoneMode ? Resource.Id.with_phone : Resource.Id.with_torbox);
        _withHint.Text = _phoneMode
            ? TorrentText.PhoneHint(!_activity.Capabilities.IsRemote, _activity.HostName, _activity.Capabilities.IsRemote, _host.Settings)
            : TorBoxText.TorBoxHint(!_activity.Capabilities.IsRemote, _activity.HostName);
    }

    private void OnMagnet()
    {
        _error.Visibility = ViewStates.Gone;
        _magnetLayout.Error = null;
        if (_pending is not null) _ui.RemoveCallbacks(_pending);
        if (_waitTick is not null) { _ui.RemoveCallbacks(_waitTick); _waitTick = null; }
        var text = _magnet.Text ?? "";
        _checking = TorBoxAddModel.IsMagnet(text);
        // The old answer is about other text: nothing of it stays on screen or can be sent meanwhile.
        _model?.Reset();
        _phone?.Reset();
        Render();
        _pending = new Java.Lang.Runnable(() => { _pending = null; _ = CheckAsync(text); });
        _ui.PostDelayed(_pending, DebounceMs);
    }

    private async Task CheckAsync(string text)
    {
        if (_disposed) return;
        bool applied;
        if (_phoneMode)
        {
            if (_phone is null) return;
            await RemoveStraysAsync();
            if (_disposed) return;
            if (TorBoxAddModel.IsMagnet(text) && !_host.Settings.TorrentsEnabled) { _phone.MarkOff(); _checking = false; Render(); return; }
            try { applied = await Task.Run(() => _phone.CheckMagnetAsync(text, _destination, _life.Token)); }
            catch (System.OperationCanceledException) { return; }
            if (_disposed) return;
            if (!applied) { _ = RemoveStraysAsync(); return; }
            _checking = false;
            Render();
            WaitForFileList();
            return;
        }

        if (_model is null) return;
        try { applied = await Task.Run(() => _model.CheckAsync(text, _life.Token)); }
        catch (System.OperationCanceledException) { return; }
        if (!applied || _disposed) return;
        _checking = false;
        Render();
    }

    /// <summary>
    /// A magnet the Host is looking for the file list of: its job is read once a second from the list the screen already has (no request
    /// goes out) until the list is in, then the file tree is asked for. Nothing runs once the screen closes or the wait ends.
    /// </summary>
    private void WaitForFileList(int missing = 0)
    {
        if (_disposed || _phone is not { Check: TorrentCheck.Waiting, PendingJobId: { } id } phone) return;
        _waitTick = new Java.Lang.Runnable(async () =>
        {
            _waitTick = null;
            if (_disposed || phone.Check != TorrentCheck.Waiting) return;
            // The list learns of the new job from the Host's own event; for a few seconds a job not in it yet is not a job gone.
            var job = _host.JobOf(id);
            var absent = job is null ? missing + 1 : 0;
            var changed = false;
            if (job is not null || absent > 5)
            {
                try { changed = await Task.Run(() => phone.PollAsync(job, _life.Token)); }
                catch (System.OperationCanceledException) { return; }
            }

            if (_disposed) return;
            if (changed) Render();
            if (phone.Check == TorrentCheck.Waiting) WaitForFileList(absent);
        });
        _ui.PostDelayed(_waitTick, WaitTickMs);
    }

    private Task RemoveStraysAsync()
    {
        if (_phone is null) return Task.CompletedTask;
        var strays = _phone.TakeStrays();
        if (strays.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await _host.RemoveAsync(strays, false).ConfigureAwait(false); }
            catch (Exception) { /* the job stays in Downloads, awaiting a choice of files, and can be removed there */ }
        });
    }

    private void ChangeKey() => TorBoxKeySheet.Show(_activity, _client!, false, _activity.Capabilities.IsRemote ? _activity.HostName : "", () =>
    {
        _model?.ForgetKeyState();
        OnMagnet();
    });

    // ---- .torrent file ----

    private void OnFilePicked(Android.Net.Uri? uri)
    {
        if (uri is null || _disposed || _phone is null) return;
        var resolver = _activity.ContentResolver!;
        _ = Task.Run(() =>
        {
            byte[]? bytes = null;
            string name = "torrent file";
            string? failure = null;
            try
            {
                using (var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null))
                    if (cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } shown) name = shown;
                using var stream = resolver.OpenInputStream(uri) ?? throw new IOException();
                using var memory = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > MaxTorrentFileBytes) { failure = "That file is too large to be a .torrent file."; break; }
                }

                if (failure is null) bytes = memory.ToArray();
            }
            catch (Exception e) when (e is IOException or Java.Lang.SecurityException or UnauthorizedAccessException or InvalidOperationException)
            {
                failure = TorrentText.FileNotRead;
            }

            // The Host gets the bytes (it may be another machine), never a path on this phone.
            _activity.RunOnUiThread(() => FileRead(bytes, name, failure));
        });
    }

    private void FileRead(byte[]? bytes, string name, string? failure)
    {
        if (_disposed || _phone is null) return;
        if (bytes is null)
        {
            _activity.ShowMessage(failure ?? TorrentText.FileNotRead);
            return;
        }

        if (_pending is not null) { _ui.RemoveCallbacks(_pending); _pending = null; }
        if (_waitTick is not null) { _ui.RemoveCallbacks(_waitTick); _waitTick = null; }
        _model?.Reset();
        _phone.Reset();
        _error.Visibility = ViewStates.Gone;
        _settingText = true;
        _magnet.Text = name;
        _magnet.SetSelection(0);
        _settingText = false;
        _phoneMode = true;
        UpdateSegments();
        _checking = true;
        Render();
        _ = RunFileCheckAsync(bytes, name);
    }

    private async Task RunFileCheckAsync(byte[] bytes, string name)
    {
        await RemoveStraysAsync();
        if (_disposed || _phone is null) return;
        if (!_host.Settings.TorrentsEnabled) { _phone.MarkOff(); _checking = false; Render(); return; }
        bool applied;
        try { applied = await Task.Run(() => _phone.CheckFileAsync(bytes, name, _life.Token)); }
        catch (System.OperationCanceledException) { return; }
        if (!applied || _disposed) return;
        _checking = false;
        Render();
    }

    // ---- drawing ----

    private void Render()
    {
        if (_disposed) return;
        if (_phoneMode) RenderPhone(); else RenderTorBox();
        _send.SetIconResource(_phoneMode ? Resource.Drawable.ic_download : Resource.Drawable.ic_flow_bolt);
        _magnetLayout.Hint = _torrents is null ? "Magnet link" : "Magnet or .torrent file";
        UpdateSend();
    }

    private void SetStatus(int container, string? title, string? sub, int icon, int tint, bool clickable = false)
    {
        _statusCard.Visibility = title is null ? ViewStates.Gone : ViewStates.Visible;
        if (title is null) return;
        _statusCard.SetCardBackgroundColor(container);
        _statusTitle.Text = title;
        _statusSub.Text = sub;
        _statusIcon.SetImageResource(icon);
        _statusIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(tint));
        _statusCard.Clickable = clickable;
        _statusCard.ContentDescription = title + ". " + sub;
    }

    private void ShowFiles(TorBoxFileTree? tree)
    {
        _filesSummary.Visibility = _filesCard.Visibility = tree is null ? ViewStates.Gone : ViewStates.Visible;
        if (tree is not null)
        {
            _filesSummary.Text = tree.Summary;
            TorBoxTreeView.Render(_activity, _filesList, tree, false, () => { _filesSummary.Text = tree.Summary; UpdateSend(); });
        }
        else _filesList.RemoveAllViews();
    }

    private void RenderPhone()
    {
        var phone = _phone;
        _missCard.Visibility = ViewStates.Gone;
        if (phone is null) { SetStatus(0, null, null, 0, 0); ShowFiles(null); return; }
        var check = _checking && phone.Check == TorrentCheck.None ? TorrentCheck.Checking : phone.Check;
        var surface = Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer);
        var variant = Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant);
        var done = _activity.GetColor(Resource.Color.state_done);
        var waiting = _activity.GetColor(Resource.Color.state_waiting);
        var failed = _activity.GetColor(Resource.Color.state_failed);
        _magnetLayout.Error = check == TorrentCheck.NotInput && (_magnet.Text?.Length ?? 0) > 0 ? phone.Message : null;
        switch (check)
        {
            case TorrentCheck.Checking:
                SetStatus(surface, "Checking…", "Asking " + HostWords() + " about it", Resource.Drawable.ic_flow_info, variant);
                break;
            case TorrentCheck.Waiting:
                SetStatus(surface, "Finding the file list…", phone.Peers > 0 ? $"Asking peers for it · {TorrentText.Peers(phone.Peers)} so far. Nothing downloads yet." : "Asking peers for it. Nothing downloads yet.",
                    Resource.Drawable.ic_flow_info, variant);
                break;
            case TorrentCheck.Found:
                SetStatus(_activity.GetColor(Resource.Color.state_done_container), phone.FoundTitle, phone.FoundDetail, Resource.Drawable.ic_check, done);
                break;
            case TorrentCheck.FileUnlisted:
                SetStatus(surface, "Torrent file ready", (phone.Name ?? "") + ". It is added with all its files; change them on its Files tab.", Resource.Drawable.ic_flow_info, variant);
                break;
            case TorrentCheck.Existing:
                SetStatus(_activity.GetColor(Resource.Color.state_waiting_container), "Already in Downloads", "This torrent is already on " + HostWords() + ".", Resource.Drawable.ic_flow_info, waiting);
                break;
            case TorrentCheck.Off:
                SetStatus(_activity.GetColor(Resource.Color.state_waiting_container), "Torrents are off", phone.Message, Resource.Drawable.ic_flow_info, waiting);
                break;
            case TorrentCheck.Failed:
                SetStatus(_activity.GetColor(Resource.Color.state_failed_container), "Couldn't read it", phone.Message, Resource.Drawable.ic_flow_info, failed);
                break;
            default:
                SetStatus(0, null, null, 0, 0);
                break;
        }

        ShowFiles(check == TorrentCheck.Found ? phone.Tree : null);
        if (check == TorrentCheck.Found) ShowMagnetStart();
        if (check == TorrentCheck.Found && phone.SpaceProblem is { } space) { _error.Text = space; _error.Visibility = ViewStates.Visible; }
        // The Host's own torrent folder shows in Save to until a folder is chosen.
        _showSave?.Invoke();
    }

    /// <summary>
    /// t1: once its answer is in, a long magnet shows its start ("magnet:?xt=urn:btih:…"), not its last tracker. While the field has
    /// focus only the view scrolls, so the cursor stays where the person is typing. Once per answer, so their own scrolling is left alone.
    /// </summary>
    private void ShowMagnetStart()
    {
        var text = _magnet.Text ?? "";
        if (_startShownFor == text) return;
        _startShownFor = text;
        if (_magnet.HasFocus) _magnet.Post(() => _magnet.ScrollTo(0, 0));
        else _magnet.SetSelection(0);
    }

    private string? _startShownFor;

    private string HostWords() => _activity.Capabilities.IsRemote ? _activity.HostName : "this phone";

    private void RenderTorBox()
    {
        var model = _model;
        if (model is null) { SetStatus(0, null, null, 0, 0); ShowFiles(null); _missCard.Visibility = ViewStates.Gone; return; }
        var check = _checking ? TorBoxCheck.Checking : model.Check;
        var tree = check == TorBoxCheck.Hit ? model.Tree : null;
        _magnetLayout.Error = check == TorBoxCheck.NotMagnet && (_magnet.Text?.Length ?? 0) > 0 ? model.Message : null;
        (int Container, string? Title, string? Sub, int Icon) status = check switch
        {
            TorBoxCheck.Checking => (Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer), "Checking TorBox…", "Looking for it in TorBox's cache", Resource.Drawable.ic_flow_bolt),
            TorBoxCheck.Hit => (_activity.GetColor(Resource.Color.state_done_container), model.ReadyTitle, "Downloads start at once", Resource.Drawable.ic_flow_bolt),
            TorBoxCheck.Miss => (_activity.GetColor(Resource.Color.state_waiting_container), "Not on TorBox yet", "No file list until TorBox has fetched it", Resource.Drawable.ic_flow_info),
            TorBoxCheck.Unknown => (Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainer), "Couldn't check TorBox's cache", "You can still send it", Resource.Drawable.ic_flow_info),
            TorBoxCheck.NoKey => (_activity.GetColor(Resource.Color.state_waiting_container), "TorBox key needed", model.Message + " Tap to add it.", Resource.Drawable.ic_settings_site_key),
            TorBoxCheck.Failed => (_activity.GetColor(Resource.Color.state_failed_container), "TorBox could not check it", model.Message, Resource.Drawable.ic_flow_info),
            _ => (0, null, null, 0),
        };
        var tint = check switch
        {
            TorBoxCheck.Hit => _activity.GetColor(Resource.Color.state_done),
            TorBoxCheck.Miss or TorBoxCheck.NoKey => _activity.GetColor(Resource.Color.state_waiting),
            TorBoxCheck.Failed => _activity.GetColor(Resource.Color.state_failed),
            _ => Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant),
        };
        SetStatus(status.Container, status.Title, status.Sub, status.Icon, tint, check == TorBoxCheck.NoKey);
        _missCard.Visibility = check is TorBoxCheck.Hit or TorBoxCheck.Miss or TorBoxCheck.Unknown ? ViewStates.Visible : ViewStates.Gone;
        ShowFiles(tree);
        if (check is TorBoxCheck.Hit or TorBoxCheck.Miss) ShowMagnetStart();
    }

    private void UpdateSend()
    {
        if (_phoneMode)
        {
            _send.Enabled = !_busy && !_checking && _phone is { CanDownload: true };
            _send.Text = _busy ? "Adding…" : _phone?.DownloadLabel ?? "Download";
        }
        else
        {
            _send.Enabled = !_busy && !_checking && _model is { CanSend: true };
            _send.Text = _busy ? "Sending…" : "Send to TorBox";
        }
    }

    private void Send()
    {
        if (_phoneMode) SendPhone(); else SendTorBox();
    }

    private void SendPhone()
    {
        if (_busy || _phone is not { CanDownload: true } phone) return;
        _busy = true;
        _error.Visibility = ViewStates.Gone;
        UpdateSend();
        TorBoxUi.Run(_activity, () => phone.DownloadAsync(_destination, _life.Token), result =>
        {
            _busy = false;
            UpdateSend();
            if (result.Done)
            {
                _activity.CloseRowPage(Root);
                _activity.ShowMessage("Added to Downloads");
                return;
            }

            _error.Text = result.Error;
            _error.Visibility = ViewStates.Visible;
        }, words => { _busy = false; UpdateSend(); _error.Text = words; _error.Visibility = ViewStates.Visible; });
    }

    private void SendTorBox()
    {
        if (_busy || _model is not { CanSend: true } model) return;
        _busy = true;
        _error.Visibility = ViewStates.Gone;
        UpdateSend();
        var hit = model.Check == TorBoxCheck.Hit;
        TorBoxUi.Run(_activity, () => model.SendAsync(_destination, _life.Token), result =>
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
            // With This phone, the Host's torrent folder (from torrent.inspect) until a folder is chosen; TorBox files go to the download folder.
            var path = _destination ?? (_phoneMode ? _phone?.Destination : null) ?? _host.DefaultDownloadDirectory;
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
            if (!FolderPicker.Offered(_host)) return;
            FolderSheet.Show(_activity, _host, _destination ?? (_phoneMode ? _phone?.Destination : null) ?? _host.DefaultDownloadDirectory, folder =>
            {
                _destination = folder;
                Show();
                // A magnet already handed to the Host for its file list was added to the old folder: it is added again to the chosen one.
                if (_phoneMode && _phone is { IsMagnet: true, Check: TorrentCheck.Waiting or TorrentCheck.Found }) OnMagnet();
            }, _life.Token);
        }

        Show();
        _showSave = Show;
        _host.SettingsChanged += Show;
        _saveTo.Click += (_, _) => Choose();
        layout.SetEndIconOnClickListener(new ClickListener(Choose));
    }

    private sealed class ToggleListener(Action<int, bool> changed) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => changed(checkedId, isChecked);
    }
}
