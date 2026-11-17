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
    private readonly NewDownloadModel _model;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextInputEditText _address, _name, _referrer, _description, _sha;
    private readonly TextInputLayout _addressLayout, _shaLayout;
    private readonly View _nameLayout, _manyNote, _checkCard, _moreBody, _connectionsBlock;
    private readonly TextView _checkTitle, _checkSub, _connectionsValue;
    private readonly ImageView _checkIcon, _chevron;
    private readonly Slider _slider;
    private readonly MaterialButton _download;
    private Java.Lang.IRunnable? _pendingCheck;
    private bool _setting;
    private bool _busy;

    public NewDownloadScreen(MainActivity activity, DownloadsModel host, string? address)
    {
        _activity = activity;
        _model = new NewDownloadModel(host);
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

        Root.FindViewById<TextInputEditText>(Resource.Id.save_to)!.Text = SaveToText(_model.SaveTo);
        // Another Inbound saves into its own default folder; the field says which machine (no folder chooser yet, ANDROID.7).
        if (activity.Capabilities.IsRemote) Root.FindViewById<TextInputLayout>(Resource.Id.save_layout)!.Hint = "Save to, on " + activity.HostName;
        if (_model.MaxConnections <= 1) _connectionsBlock.Visibility = ViewStates.Gone;
        else
        {
            _slider.ValueTo = _model.MaxConnections;
            _slider.Value = _model.Connections;
            // The slider's label formatter is called whenever the value changes, which is the one hook the generated binding allows (its change listener cannot be implemented in C#).
            _slider.SetLabelFormatter(new ValueFormatter(v => { _model.Connections = (int)v; _connectionsValue.Text = ((int)v).ToString(); }));
        }

        _connectionsValue.Text = _model.Connections.ToString();
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!.Check(Resource.Id.when_now);
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.exists_group)!.Check(Resource.Id.exists_rename);
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.when_group)!.AddOnButtonCheckedListener(new ToggleListener((id, on) => { if (on) _model.StartLater = id == Resource.Id.when_later; }));
        Root.FindViewById<MaterialButtonToggleGroup>(Resource.Id.exists_group)!.AddOnButtonCheckedListener(new ToggleListener((id, on) =>
        {
            if (!on) return;
            _model.IfExists = id == Resource.Id.exists_overwrite ? ConflictChoice.Overwrite : id == Resource.Id.exists_fail ? ConflictChoice.Fail : ConflictChoice.Rename;
        }));

        _addressLayout.SetEndIconOnClickListener(new ClickListener(() =>
        {
            if (Outside.ClipboardText(_activity) is { Length: > 0 } text) _address.Text = text.Trim();
            else _activity.ShowMessage("Nothing on the clipboard");
        }));
        _address.TextChanged += (_, _) => OnAddress();
        _name.TextChanged += (_, _) => { if (!_setting) _model.FileName = _name.Text ?? ""; };
        _referrer.TextChanged += (_, _) => _model.Referrer = _referrer.Text;
        _description.TextChanged += (_, _) => _model.Description = _description.Text;
        _sha.TextChanged += (_, _) => { _model.Sha256 = _sha.Text; _shaLayout.Error = _model.ChecksumError; UpdateButton(); };
        Root.FindViewById<View>(Resource.Id.more_header)!.Click += (_, _) => ToggleMore();
        Root.FindViewById<View>(Resource.Id.new_close)!.Click += (_, _) => _activity.CloseRowPage();
        Root.FindViewById<View>(Resource.Id.new_cancel)!.Click += (_, _) => _activity.CloseRowPage();
        _download.Click += (_, _) => Submit();

        if (!string.IsNullOrEmpty(address)) _address.Text = address;
        UpdateButton();
    }

    public View Root { get; }

    /// <summary>The field is focused and the keyboard comes up when the page opens empty.</summary>
    public void FocusAddress()
    {
        _address.RequestFocus();
        var imm = (Android.Views.InputMethods.InputMethodManager)_activity.GetSystemService(Android.Content.Context.InputMethodService)!;
        _ui.PostDelayed(() => imm.ShowSoftInput(_address, 0), 350);
    }

    private static string SaveToText(string path)
    {
        const string root = "/storage/emulated/0/";
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    }

    private void OnAddress()
    {
        _model.Address = _address.Text ?? "";
        _addressLayout.Error = _model.AddressError;
        if (_pendingCheck is not null) { _ui.RemoveCallbacks(_pendingCheck); _pendingCheck = null; }
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
                _checkTitle.Text = _model.CheckText;
                _checkSub.Text = "The server answered";
                if (!string.IsNullOrEmpty(_model.FileName) && _name.Text != _model.FileName)
                {
                    _setting = true;
                    _name.Text = _model.FileName;
                    _setting = false;
                }

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

    private void UpdateButton() => _download.Enabled = _model.CanDownload && !_busy;

    private void ToggleMore()
    {
        var open = _moreBody.Visibility != ViewStates.Visible;
        AndroidX.Transitions.TransitionManager.BeginDelayedTransition((ViewGroup)Root.FindViewById<View>(Resource.Id.new_scroll)!, new AndroidX.Transitions.AutoTransition());
        _moreBody.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        _chevron.Rotation = open ? 180f : 0f;
    }

    private void Submit()
    {
        if (_busy || !_model.CanDownload) return;
        if (_connectionsBlock.Visibility == ViewStates.Visible) _model.Connections = (int)_slider.Value;
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
                if (result.Error is { } error) { _activity.ShowMessage(error); return; }
                _activity.CloseRowPage();
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
