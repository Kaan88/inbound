using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Slider;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>New download (A5): the address and what the Host says about it, the name, when to start, connections and More options.</summary>
internal sealed class NewDownloadScreen
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _host;
    private readonly NewDownloadModel _model;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextInputEditText _address, _name, _referrer, _description, _sha;
    private readonly TextInputLayout _addressLayout, _shaLayout;
    private readonly View _nameLayout, _manyNote, _checkCard, _moreBody, _connectionsBlock;
    private readonly TextView _checkTitle, _checkSub, _connectionsValue, _submitError;
    private readonly ImageView _checkIcon, _chevron;
    private readonly Slider _slider;
    private readonly MaterialButton _download;
    private Java.Lang.IRunnable? _pendingCheck;
    private bool _setting;
    private bool _busy;
    private int _collisionInputVersion;
    private TextInputEditText _queuePicker = null!;
    private ListPopupWindow? _queuePopup;
    private EventHandler<AdapterView.ItemClickEventArgs>? _queuePopupClick;
    private readonly List<string> _queueLabels = new();
    private int _queueChoicesVersion;
    private TextInputLayout _queueLayout = null!;
    private TextView _queueError = null!;
    private readonly List<(int? Id, Guid? Generation)> _queueChoices = new();
    private int _queueWhenId;

    /// <param name="saved">What the page held before the activity was rebuilt (rotation, process death), or null.</param>
    public NewDownloadScreen(MainActivity activity, DownloadsModel host, string? address, Bundle? saved = null, bool transient = false)
    {
        Transient = transient;
        _activity = activity;
        _host = host;
        _model = new NewDownloadModel(host, requireQueueIdentity: activity.Capabilities.IsRemote);
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.new_download, null, false)!;
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.new_top)!, top: true, bottom: false);
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.new_bottom)!, top: false, bottom: true);
        _address = Root.FindViewById<TextInputEditText>(Resource.Id.address)!;
        _addressLayout = Root.FindViewById<TextInputLayout>(Resource.Id.address_layout)!;
        _name = Root.FindViewById<TextInputEditText>(Resource.Id.file_name)!;
        _nameLayout = Root.FindViewById<View>(Resource.Id.name_layout)!;
        _manyNote = Root.FindViewById<View>(Resource.Id.many_note)!;
        _checkCard = Root.FindViewById<View>(Resource.Id.check_card)!;
        _checkTitle = Root.FindViewById<TextView>(Resource.Id.check_title)!;
        _checkSub = Root.FindViewById<TextView>(Resource.Id.check_sub)!;
        _checkIcon = Root.FindViewById<ImageView>(Resource.Id.check_icon)!;
        _connectionsBlock = Root.FindViewById<View>(Resource.Id.connections_block)!;
        _connectionsValue = Root.FindViewById<TextView>(Resource.Id.connections_value)!;
        _slider = Root.FindViewById<Slider>(Resource.Id.connections_slider)!;
        _moreBody = Root.FindViewById<View>(Resource.Id.more_body)!;
        _chevron = Root.FindViewById<ImageView>(Resource.Id.more_chevron)!;
        _referrer = Root.FindViewById<TextInputEditText>(Resource.Id.referrer)!;
        _description = Root.FindViewById<TextInputEditText>(Resource.Id.description)!;
        _sha = Root.FindViewById<TextInputEditText>(Resource.Id.sha)!;
        _shaLayout = Root.FindViewById<TextInputLayout>(Resource.Id.sha_layout)!;
        _download = Root.FindViewById<MaterialButton>(Resource.Id.new_download_btn)!;
        _submitError = Root.FindViewById<TextView>(Resource.Id.new_submit_error)!;
        if (Transient)
        {
            Root.SaveFromParentEnabled = false;
            foreach (var input in new[] { _address, _name, _referrer, _description, _sha }) input.SaveEnabled = false;
        }

        SetUpSaveTo(host);
        SetUpQueue();
        // The slider's label formatter is called whenever the value changes, which is the one hook the generated binding allows (its change listener cannot be implemented in C#).
        _slider.SetLabelFormatter(new ValueFormatter(v => { _model.Connections = (int)v; _connectionsValue.Text = ((int)v).ToString();
            if (_model.Check == CheckState.Ready) _checkSub.Text = $"{(int)v} {((int)v == 1 ? "connection" : "connections")} requested";
        }));
        ShowSettings();
        // The Host's settings can come later (a page restored before the snapshot) or change (finding 13).
        _host.SettingsChanged += OnSettings;
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.exists_group)!.Check(Resource.Id.exists_rename);
        var whenGroup = Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!;
        whenGroup.AddOnButtonCheckedListener(new ToggleListener((id, on) =>
        {
            if (id is Resource.Id.when_now or Resource.Id.when_later)
                whenGroup.FindViewById<MaterialButton>(id)?.SetIconResource(on ? Resource.Drawable.ic_check : 0);
            if (!on) return;
            _model.StartLater = id == Resource.Id.when_later;
            _model.WaitForQueue = id == _queueWhenId;
        }));
        whenGroup.Check(Resource.Id.when_now);
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.exists_group)!.AddOnButtonCheckedListener(new ToggleListener((id, on) =>
        {
            if (!on) return;
            ClearSubmitError();
            _model.IfExists = id == Resource.Id.exists_overwrite ? ConflictChoice.Overwrite : id == Resource.Id.exists_fail ? ConflictChoice.Fail : ConflictChoice.Rename;
        }));

        _addressLayout.SetEndIconOnClickListener(new ClickListener(() =>
        {
            if (Outside.ClipboardText(_activity) is { Length: > 0 } text) _address.Text = text.Trim();
            else _activity.ShowMessage("Nothing on the clipboard");
        }));
        _address.TextChanged += (_, _) => { ClearSubmitError(); OnAddress(); };
        _name.TextChanged += (_, _) =>
        {
            ClearSubmitError();
            if (!_setting) _model.FileName = _name.Text ?? "";
        };
        _referrer.TextChanged += (_, _) => _model.Referrer = _referrer.Text;
        _description.TextChanged += (_, _) => _model.Description = _description.Text;
        _sha.TextChanged += (_, _) => { _model.Sha256 = _sha.Text; _shaLayout.Error = _model.ChecksumError; UpdateButton(); };
        Root.FindViewById<View>(Resource.Id.more_header)!.Click += (_, _) => ToggleMore();
        Root.FindViewById<View>(Resource.Id.new_close)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.new_cancel)!.Click += (_, _) => _activity.CloseRowPage(Root);
        _download.Click += (_, _) => Submit();

        if (saved is not null) Restore(saved);
        else if (!string.IsNullOrEmpty(address)) _address.Text = address;
        UpdateButton();
    }

    public View Root { get; }

    /// <summary>The page has gone: it no longer follows the Host's settings.</summary>
    public void Dispose() { _host.SettingsChanged -= OnSettings; CloseQueuePopup(); }

    private void OnSettings()
    {
        ShowSettings();
        UpdateButton();
    }

    // A connection count restored before the Host's settings came: kept until they come, then clamped to their limit, not
    // to the default limit of settings not yet known (Fable finding 14).
    private int? _restoredConnections;

    // The folder and the connection limit come from the Host's settings; a value the user chose stays, within the new limit.
    private void ShowSettings()
    {
        _showSaveTo?.Invoke();
        ShowQueues();
        _model.Connections = Math.Clamp(_restoredConnections ?? _model.Connections, 1, _model.MaxConnections);
        if (_host.SnapshotShown) _restoredConnections = null;
        if (_model.MaxConnections <= 1) _connectionsBlock.Visibility = ViewStates.Gone;
        else
        {
            _connectionsBlock.Visibility = ViewStates.Visible;
            _slider.ValueTo = _model.MaxConnections;
            _slider.Value = _model.Connections;
        }

        _connectionsValue.Text = _model.Connections.ToString();
    }

    /// <summary>What the page holds, for a rebuilt activity (finding 16). A name the Host found is not kept: the check runs again.</summary>
    public bool Transient { get; }

    public void Save(Bundle state)
    {
        if (Transient) return;
        state.PutString("address", _address.Text ?? "");
        if (_model.FileNameEdited) state.PutString("name", _model.FileName);
        state.PutString("referrer", _referrer.Text ?? "");
        state.PutString("description", _description.Text ?? "");
        state.PutString("sha", _sha.Text ?? "");
        state.PutInt("connections", _model.Connections);
        if (_model.Destination is { } destination) state.PutString("destination", destination);
        state.PutInt("exists", (int)_model.IfExists);
        state.PutBoolean("later", _model.StartLater);
        state.PutBoolean("wait_queue", _model.WaitForQueue);
        if (_model.QueueId is { } queueId) state.PutInt("queue_id", queueId);
        if (_model.QueueGeneration is { } generation) state.PutString("queue_generation", generation.ToString("D"));
        state.PutBoolean("more", _moreBody.Visibility == ViewStates.Visible);
    }

    private void Restore(Bundle state)
    {
        _address.Text = state.GetString("address") ?? "";
        if (state.GetString("name") is { Length: > 0 } name) _name.Text = name;
        _referrer.Text = state.GetString("referrer");
        _description.Text = state.GetString("description");
        _sha.Text = state.GetString("sha");
        _restoredConnections = Math.Max(1, state.GetInt("connections", _model.Connections));
        int? restoredQueue = state.ContainsKey("queue_id") ? state.GetInt("queue_id") : null;
        Guid? restoredGeneration = Guid.TryParse(state.GetString("queue_generation"), out var generation) ? generation : null;
        _model.SetQueueSelection(restoredQueue, restoredGeneration);
        ShowSettings();
        if (state.GetString("destination") is { Length: > 0 } destination) { _model.Destination = destination; _showSaveTo?.Invoke(); }
        var exists = (ConflictChoice)state.GetInt("exists", (int)ConflictChoice.Rename);
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.exists_group)!.Check(exists switch
        {
            ConflictChoice.Overwrite => Resource.Id.exists_overwrite,
            ConflictChoice.Fail => Resource.Id.exists_fail,
            _ => Resource.Id.exists_rename,
        });
        if (state.GetBoolean("later")) Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!.Check(Resource.Id.when_later);
        else if (state.GetBoolean("wait_queue")) Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!.Check(_queueWhenId);
        if (state.GetBoolean("more")) { _moreBody.Visibility = ViewStates.Visible; _chevron.Rotation = 180f; }
    }

    /// <summary>The field is focused and the keyboard comes up when the page opens empty.</summary>
    public void FocusAddress()
    {
        _address.RequestFocus();
        var imm = (Android.Views.InputMethods.InputMethodManager)_activity.GetSystemService(Android.Content.Context.InputMethodService)!;
        _ui.PostDelayed(() => imm.ShowSoftInput(_address, 0), 350);
    }

    // Browse the selected Host's allowed folders, including shared Downloads on the phone.
    private Action? _showSaveTo;

    private void SetUpSaveTo(DownloadsModel host)
    {
        var field = Root.FindViewById<TextInputEditText>(Resource.Id.save_to)!;
        var layout = Root.FindViewById<TextInputLayout>(Resource.Id.save_layout)!;
        var remote = _activity.Capabilities.IsRemote;
        if (remote) layout.Hint = "Save to, on " + _activity.HostName;
        void Show()
        {
            var path = _model.SaveTo;
            var full = SavePathWords.ForField(path);
            // An EditText never cuts its text with an ellipsis, so the field is measured and cut in the middle here; TalkBack reads all of it.
            void Fit()
            {
                var room = field.Width - field.CompoundPaddingLeft - field.CompoundPaddingRight;
                if (room <= 0) { field.Text = full; return; }
                var paint = field.Paint!;
                var cut = MiddleEllipsis.Cut(full, s => paint.MeasureText(s) <= room);
                if (field.Text != cut) field.Text = cut;
            }

            field.Text = full;
            field.ContentDescription = SavePathWords.Label(remote ? _activity.HostName : null, path);
            field.Post(Fit);
        }

        void ShowDestination()
        {
            Show();
            var offered = FolderPicker.Offered(host);
            field.Enabled = offered;
            field.Focusable = false;
            field.Clickable = offered;
            field.LongClickable = false;
            layout.EndIconContentDescription = offered ? "Choose a folder on " + _activity.HostName : null;
        }
        _showSaveTo = ShowDestination;
        ShowDestination();
        void Choose()
        {
            if (FolderPicker.Offered(host)) FolderSheet.Show(_activity, host, _model.SaveTo, folder =>
            {
                if (_model.SaveTo != folder) ClearSubmitError();
                _model.Destination = folder;
                Show();
            });
        }
        field.Click += (_, _) => Choose();
        layout.SetEndIconOnClickListener(new ClickListener(Choose));
    }

    private void SetUpQueue()
    {
        var group = Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!;
        group.SaveEnabled = false; // Save/Restore below owns this choice, including the generated queue button ID.
        var queueButton = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Id = View.GenerateViewId(), Text = "Queue", ContentDescription = "Wait for queue"
        };
        _queueWhenId = queueButton.Id;
        queueButton.SetMinWidth(0); queueButton.SetMinimumWidth(0);
        group.AddView(queueButton, 1, new LinearLayout.LayoutParams(0, -2, 1));
        var parent = (LinearLayout)group.Parent!;
        var box = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var queueLayout = _queueLayout = new TextInputLayout(_activity, null, Resource.Attribute.textInputOutlinedStyle) { Hint = "Queue" };
        var cornerRadius = _activity.Resources!.GetDimension(Resource.Dimension.new_download_field_radius);
        queueLayout.SetBoxCornerRadii(cornerRadius, cornerRadius, cornerRadius, cornerRadius);
        _queuePicker = new TextInputEditText(queueLayout.Context!) { ContentDescription = "Choose queue", SaveEnabled = false };
        _queuePicker.KeyListener = null;
        _queuePicker.Focusable = true;
        _queuePicker.Clickable = true;
        _queuePicker.LongClickable = false;
        _queuePicker.SetSingleLine(true);
        _queuePicker.SetMinimumHeight(Motion.Dp(_activity, 48));
        _queuePicker.Click += (_, _) => OpenQueuePopup();
        _queuePicker.KeyPress += (_, e) =>
        {
            e.Handled = false;
            if (e.KeyCode is not (Keycode.Enter or Keycode.DpadCenter)) return;
            e.Handled = true;
            if (e.Event?.Action == KeyEventActions.Up) OpenQueuePopup();
        };
        queueLayout.AddView(_queuePicker, new LinearLayout.LayoutParams(-1, -2));
        queueLayout.EndIconMode = TextInputLayout.EndIconCustom;
        queueLayout.SetEndIconDrawable(Resource.Drawable.ic_expand_more);
        queueLayout.EndIconContentDescription = "Choose queue";
        queueLayout.SetEndIconOnClickListener(new ClickListener(OpenQueuePopup));
        box.AddView(queueLayout, new LinearLayout.LayoutParams(-1, -2));
        _queueError = new TextView(_activity) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _queueError.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorError)));
        box.AddView(_queueError);
        var note = new TextView(_activity) { Text = "Downloads follow the queue's running state and conditions. Later waits until you resume it.", TextSize = 14 };
        note.SetTextColor(new Android.Graphics.Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        box.AddView(note);
        parent.AddView(box, parent.IndexOfChild(group) + 1, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 16) });
    }

    private void CloseQueuePopup()
    {
        var popup = _queuePopup;
        var click = _queuePopupClick;
        _queuePopup = null;
        _queuePopupClick = null;
        if (popup is null) return;
        if (click is not null) popup.ItemClick -= click;
        popup.Dismiss();
        popup.Dispose();
    }

    private void OpenQueuePopup()
    {
        if (_busy || !_host.SnapshotShown || !_queuePicker.Enabled) return;
        CloseQueuePopup();
        var choices = _queueChoices.ToArray();
        var labels = _queueLabels.ToArray();
        var version = _queueChoicesVersion;
        var popup = new ListPopupWindow(_activity)
        {
            AnchorView = _queueLayout,
            Modal = true,
            Width = _queueLayout.Width,
            Height = -2,
            InputMethodMode = ListPopupWindowInputMethodMode.NotNeeded
        };
        popup.SetAdapter(new ArrayAdapter<string>(_activity, Android.Resource.Layout.SimpleSpinnerDropDownItem, labels));
        _queuePopup = popup;
        _queuePopupClick = (_, e) =>
        {
            if (!ReferenceEquals(_queuePopup, popup) || version != _queueChoicesVersion || _busy ||
                !_host.SnapshotShown || e.Position < 0 || e.Position >= choices.Length) return;
            var selection = choices[e.Position];
            _model.SetQueueSelection(selection.Id, selection.Generation);
            ShowQueues();
            UpdateButton();
        };
        popup.ItemClick += _queuePopupClick;
        popup.Show();
        var selected = Array.IndexOf(choices, (_model.QueueId, _model.QueueGeneration));
        if (selected >= 0) popup.SetSelection(selected);
    }

    private void ShowQueues()
    {
        CloseQueuePopup();
        _queueChoicesVersion++;
        var queues = _host.Settings.Queues.OrderBy(q => q.Order).ThenBy(q => q.Id).ToArray();
        var defaultName = queues.FirstOrDefault(q => q.Id == _host.Settings.DefaultQueueId)?.Name;
        var labels = new List<string> { defaultName is null ? "Host default" : "Host default (" + defaultName + ")" };
        _queueChoices.Clear(); _queueChoices.Add((null, null));
        foreach (var queue in queues) { _queueChoices.Add((queue.Id, queue.Generation)); labels.Add(queue.Name); }
        if (_model.QueueId is { } selected && !_queueChoices.Contains((selected, _model.QueueGeneration)))
        {
            _queueChoices.Add((selected, _model.QueueGeneration));
            labels.Add(_host.SnapshotShown ? "Unavailable queue (" + selected + ")" : "Waiting for selected queue");
        }
        var index = _queueChoices.IndexOf((_model.QueueId, _model.QueueGeneration));
        _queueLabels.Clear();
        _queueLabels.AddRange(labels);
        _queuePicker.Text = labels[index < 0 ? 0 : index];
    }
    private void OnAddress()
    {
        _model.Address = _address.Text ?? "";
        _addressLayout.Error = _model.AddressError;
        if (_pendingCheck is not null) { _ui.RemoveCallbacks(_pendingCheck); _pendingCheck = null; }
        ShowName(); // a name found for the previous address goes with it (finding 9)
        if (_model.IsMany)
        {
            _manyNote.Visibility = ViewStates.Visible;
            ((TextView)_manyNote).Text = $"{_model.Urls.Count} links will be added";
        }
        else _manyNote.Visibility = ViewStates.Gone;
        _nameLayout.Visibility = _model.ShowFileName || _model.Urls.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        if (_model.Urls.Count == 1)
        {
            // Asked a moment after the last keystroke, so typing and pasting do not send a request per letter.
            _pendingCheck = new Java.Lang.Runnable(RunCheck);
            _ui.PostDelayed(_pendingCheck, 600);
        }

        RenderCheck();
        UpdateButton();
    }

    private void RunCheck()
    {
        _pendingCheck = null;
        _ = Task.Run(async () =>
        {
            try { await _model.CheckAsync().ConfigureAwait(false); }
            catch (System.OperationCanceledException) { }
            _activity.RunOnUiThread(() => { RenderCheck(); UpdateButton(); });
        });
        RenderCheck();
    }

    private void RenderCheck()
    {
        var ctx = _activity;
        var card = (MaterialCardView)_checkCard;
        switch (_model.Check)
        {
            case CheckState.Checking:
                _checkCard.Visibility = ViewStates.Visible;
                card.SetCardBackgroundColor(Motion.Attr(ctx, Resource.Attribute.colorSurfaceContainer));
                _checkIcon.SetImageResource(Resource.Drawable.ic_refresh);
                _checkIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnSurfaceVariant)));
                _checkTitle.Text = "Checking the address";
                _checkSub.Text = "Asking the server for the name and size";
                break;
            case CheckState.Ready:
                _checkCard.Visibility = ViewStates.Visible;
                card.SetCardBackgroundColor(ctx.GetColor(Resource.Color.state_done_container));
                _checkIcon.SetImageResource(Resource.Drawable.ic_check);
                _checkIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(ctx.GetColor(Resource.Color.state_done)));
                _checkTitle.Text = _model.CheckText + (_model.AdvertisesByteRanges == true ? " · Supports byte ranges" : "");
                _checkSub.Text = $"{_model.Connections} {(_model.Connections == 1 ? "connection" : "connections")} requested";
                ShowName();
                break;
            case CheckState.Failed:
            case CheckState.Refused:
                _checkCard.Visibility = ViewStates.Visible;
                card.SetCardBackgroundColor(ctx.GetColor(Resource.Color.state_failed_container));
                _checkIcon.SetImageResource(Resource.Drawable.ic_close);
                _checkIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(ctx.GetColor(Resource.Color.state_failed)));
                _checkTitle.Text = _model.CheckText;
                _checkSub.Text = _model.Check == CheckState.Refused ? "This address can't be added" : "You can still try to download it";
                break;
            default:
                _checkCard.Visibility = ViewStates.Gone;
                break;
        }
    }

    private void UpdateButton()
    {
        _download.Enabled = _model.CanDownload && !_busy;
        _queueError.Text = _model.QueueError ?? "";
        _queueLayout.Enabled = _queuePicker.Enabled = !_busy && _host.SnapshotShown;
        var group = Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!;
        for (var i = 0; i < group.ChildCount; i++) group.GetChildAt(i)!.Enabled = !_busy;
    }

    // The field shows the model's name unless the user typed one (then it is already what the field holds).
    private void ShowName()
    {
        if (_model.FileNameEdited || (_name.Text ?? "") == _model.FileName) return;
        _setting = true;
        _name.Text = _model.FileName;
        _setting = false;
    }

    private void ToggleMore()
    {
        var open = _moreBody.Visibility != ViewStates.Visible;
        AndroidX.Transitions.TransitionManager.BeginDelayedTransition((ViewGroup)Root.FindViewById<View>(Resource.Id.new_scroll)!, new AndroidX.Transitions.AutoTransition());
        _moreBody.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        _chevron.Rotation = open ? 180f : 0f;
    }

    private void ClearSubmitError()
    {
        _collisionInputVersion++;
        _submitError.Visibility = ViewStates.Gone;
    }

    private void Submit()
    {
        if (_busy || !_model.CanDownload) return;
        if (_connectionsBlock.Visibility == ViewStates.Visible) _model.Connections = (int)_slider.Value;
        _submitError.Visibility = ViewStates.Gone;
        var collisionInputVersion = _collisionInputVersion;
        var saveTo = _model.SaveTo;
        var conflictPolicy = _model.IfExists;
        _busy = true;
        UpdateButton();
        _ = Task.Run(async () =>
        {
            NewDownloadModel.SubmitResult result;
            try { result = await _model.SubmitAsync().ConfigureAwait(false); }
            catch (Exception e) { result = new NewDownloadModel.SubmitResult(0, 0, "The download was not added: " + e.Message); }
            _activity.RunOnUiThread(() =>
            {
                _busy = false;
                UpdateButton();
                if (result.Error is { } error)
                {
                    if (!Root.IsAttachedToWindow) return;
                    var collision = conflictPolicy == ConflictChoice.Fail &&
                        (error.StartsWith("The destination file already exists: ", StringComparison.Ordinal) ||
                         (error.StartsWith("Cannot use destination '", StringComparison.Ordinal) &&
                          error.Contains("': The destination file already exists: ", StringComparison.Ordinal)));
                    if (collision && (collisionInputVersion != _collisionInputVersion || saveTo != _model.SaveTo)) return;
                    _submitError.Text = collision
                        ? "A file with this name already exists. Choose Rename or Overwrite, or change the file name."
                        : error;
                    _submitError.Visibility = ViewStates.Visible;
                    return;
                }
                // Only this page: if it was closed meanwhile and another one opened, that one stays (finding 18).
                _activity.CloseRowPage(Root);
                _activity.ShowMessage(NewDownloadModel.Describe(result));
            });
        });
    }

    private sealed class ValueFormatter(Action<float> changed) : Java.Lang.Object, Google.Android.Material.Slider.ILabelFormatter
    {
        public string GetFormattedValue(float value)
        {
            changed(value);
            return ((int)value).ToString();
        }
    }

    private sealed class ToggleListener(Action<int, bool> changed) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => changed(checkedId, isChecked);
    }
}
