using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

internal sealed record CaughtSheetSpec(string FileName, string Detail, string Destination, string Privacy,
    int DefaultConnections, int MaxConnections, bool LocalSignedIn, bool HasCookies, QueueDto[] Queues);

internal sealed record CaughtFolderChoice(string? InitialPath, Func<bool> StillCurrent,
    Func<string?, Action<string>, BottomSheetDialog> Open);

/// <summary>Transient confirmation only. The caller owns the request and durable submission.</summary>
[Register("app.inbound.android.CaughtDownloadSheet")]
internal sealed class CaughtDownloadSheet : BottomSheetDialogFragment
{
    private const string SheetTag = "caught_download";
    private CaughtSheetSpec? _spec;
    private Func<int, bool, string?, QueueDto?, Task<bool>>? _submit;
    private CaughtFolderChoice? _folderChoice;
    private BottomSheetDialog? _folderDialog;
    private string? _selectedFolder;
    private MaterialButton? _button;
    private MaterialButton? _more;
    private MaterialButton? _change;
    private Spinner? _connections;
    private DownloadManager.Mobile.DownloadsModel? _host;
    private TextView? _network;
    private RadioGroup? _when;
    private QueueDto? _queue;
    private PopupMenu? _queueMenu;
    private TextView? _error;
    private int[] _choices = [];
    private bool _busy, _viewAlive;

    public CaughtDownloadSheet() { }
    private CaughtDownloadSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    internal static void Show(MainActivity activity, DownloadManager.Mobile.DownloadsModel host, CaughtSheetSpec spec, CaughtFolderChoice? folderChoice,
        Func<int, bool, string?, QueueDto?, Task<bool>> submit)
    {
        if (activity.SupportFragmentManager.FindFragmentByTag(SheetTag) is not null) return;
        new CaughtDownloadSheet { _host = host, _spec = spec, _submit = submit, _folderChoice = folderChoice }
            .Show(activity.SupportFragmentManager, SheetTag);
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        // A restored fragment never restores a callback, request or consent.
        if (_spec is null || _submit is null)
        {
            DismissAllowingStateLoss();
            return null;
        }
        var view = inflater.Inflate(_spec.LocalSignedIn ? Resource.Layout.caught_signed_in_sheet
            : Resource.Layout.caught_download_sheet, container, false)!;
        view.SaveEnabled = false;
        _viewAlive = true;
        view.FindViewById<TextView>(Resource.Id.caught_name)!.Text = _spec.FileName;
        var detail = view.FindViewById<TextView>(Resource.Id.caught_detail)!;
        detail.Text =
            _spec.LocalSignedIn ? _spec.Detail + " · uses your sign-in" : _spec.Detail;
        var privacy = view.FindViewById<TextView>(Resource.Id.caught_privacy)!;
        privacy.Text = _spec.Privacy;
        var hasCookies = _spec.HasCookies;
        privacy.Visibility = hasCookies ? ViewStates.Visible : ViewStates.Gone;
        _connections = view.FindViewById<Spinner>(Resource.Id.caught_connections)!;
        _when = view.FindViewById<RadioGroup>(Resource.Id.caught_when)!;
        _button = view.FindViewById<MaterialButton>(Resource.Id.caught_submit)!;
        _error = view.FindViewById<TextView>(Resource.Id.caught_error)!;
        var max = Math.Max(1, _spec.MaxConnections);
        var preferred = Math.Clamp(_spec.DefaultConnections, 1, max);
        if (_spec.LocalSignedIn)
        {
            var icon = view.FindViewById<ImageView>(Resource.Id.caught_signed_in_icon)!;
            var fileIcon = FileIcons.For(DownloadManager.Mobile.FileKinds.Of(_spec.FileName));
            icon.SetImageResource(fileIcon.Drawable);
            icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(
                new Android.Graphics.Color(FileIcons.Color(RequireContext(), fileIcon)));
            _choices = [preferred];
            _button.Click += OnSubmit;
            return view;
        }
        var destination = view.FindViewById<TextView>(Resource.Id.caught_destination)!;
        destination.Text = _spec.Destination;
        _change = view.FindViewById<MaterialButton>(Resource.Id.caught_change_destination)!;
        if (_folderChoice is { } folder)
        {
            _change.Visibility = ViewStates.Visible;
            _change.Click += (_, _) =>
            {
                if (_busy || !_viewAlive || _folderDialog is { IsShowing: true } || !folder.StillCurrent()) return;
                _folderDialog = folder.Open(_selectedFolder ?? folder.InitialPath, selected =>
                {
                    if (!_viewAlive || !folder.StillCurrent()) return;
                    _selectedFolder = selected;
                    destination.Text = selected;
                });
            };
        }
        _network = view.FindViewById<TextView>(Resource.Id.caught_network);
        if (_host is not null)
        {
            _host.SettingsChanged += UpdateNetwork;
            UpdateNetwork();
        }
        var queue = view.FindViewById<RadioButton>(Resource.Id.caught_queue)!;
        var queues = _spec.Queues;
        if (queues.Length > 0)
        {
            int committedTiming = _when!.CheckedRadioButtonId;
            _when.CheckedChange += (_, e) =>
            {
                if (e.CheckedId != Resource.Id.caught_queue) committedTiming = e.CheckedId;
            };
            queue.Visibility = ViewStates.Visible;
            queue.Text = queues[0].Name + " queue";
            queue.Click += (_, _) =>
            {
                if (_busy || _queueMenu is not null) return;
                var menu = new PopupMenu(RequireContext(), queue);
                _queueMenu = menu;
                for (var i = 0; i < queues.Length; i++) menu.Menu!.Add(0, i + 1, i, queues[i].Name);
                menu.MenuItemClick += (_, e) =>
                {
                    if (!_viewAlive || e.Item is null) return;
                    committedTiming = Resource.Id.caught_queue;
                    _queue = queues[e.Item.ItemId - 1];
                    queue.Text = _queue.Name + " queue";
                };
                menu.DismissEvent += (_, _) =>
                {
                    _queueMenu = null;
                    if (_viewAlive) _when?.Check(committedTiming);
                };
                menu.Show();
            };
        }
        var options = view.FindViewById<View>(Resource.Id.caught_options)!;
        _more = view.FindViewById<MaterialButton>(Resource.Id.caught_more)!;
        _more.Click += (_, _) =>
        {
            bool reveal = options.Visibility != ViewStates.Visible;
            options.Visibility = reveal ? ViewStates.Visible : ViewStates.Gone;
            privacy.Visibility = hasCookies || reveal ? ViewStates.Visible : ViewStates.Gone;
            _more.Text = reveal ? "Fewer options" : "More options";
        };
        var choices = new SortedSet<int> { 1, preferred, max };
        for (var n = 2; n < max && n > 0; n *= 2) choices.Add(n);
        _choices = choices.ToArray();
        var adapter = new ArrayAdapter<string>(RequireContext(), Android.Resource.Layout.SimpleSpinnerItem, _choices.Select(n => n.ToString()).ToArray());
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        var details = _spec.Detail;
        void Describe(int n) => detail.Text = $"{details} · {n} {(n == 1 ? "connection" : "connections")}";
        _connections.ItemSelected += (_, e) =>
        {
            if (e.Position >= 0 && e.Position < _choices.Length) Describe(_choices[e.Position]);
        };
        _connections.Adapter = adapter;
        _connections.SetSelection(Array.IndexOf(_choices, preferred));
        Describe(preferred);
        _button.Click += OnSubmit;
        return view;
    }

    private async void OnSubmit(object? sender, EventArgs e)
    {
        if (_busy || !_viewAlive || _submit is null || _spec is null || _folderDialog is { IsShowing: true } || _queueMenu is not null) return;
        if (!_spec.LocalSignedIn && (_connections is null || _when is null)) return;
        var index = _connections?.SelectedItemPosition ?? 0;
        if (index < 0 || index >= _choices.Length) return;
        var callback = _submit;
        _busy = true; Cancelable = false;
        SetEnabled(false);
        try
        {
            var accepted = await callback(_choices[index], _when?.CheckedRadioButtonId == Resource.Id.caught_later, _selectedFolder,
                _when?.CheckedRadioButtonId == Resource.Id.caught_queue ? _queue : null);
            if (!_viewAlive || !IsAdded) return;
            if (accepted) { DismissAllowingStateLoss(); return; }
            if (_error is not null) { _error.Text = "Could not add the download. Try again."; _error.Visibility = ViewStates.Visible; }
        }
        catch (Exception)
        {
            if (_viewAlive && IsAdded && _error is not null) { _error.Text = "Could not add the download. Try again."; _error.Visibility = ViewStates.Visible; }
        }
        finally
        {
            _busy = false;
            if (_viewAlive && IsAdded) { Cancelable = true; SetEnabled(true); }
        }
    }

    private void SetEnabled(bool enabled)
    {
        if (_button is not null) _button.Enabled = enabled;
        if (_more is not null) _more.Enabled = enabled;
        if (_change is not null) _change.Enabled = enabled;
        if (_connections is not null) _connections.Enabled = enabled;
        if (_when is not null) for (var i = 0; i < _when.ChildCount; i++) _when.GetChildAt(i)!.Enabled = enabled;
    }

    private void UpdateNetwork()
    {
        if (_network is null || _host is null) return;
        _network.Visibility = _host.Hello?.Capabilities.Contains("meteredNetworkRule") == true ? ViewStates.Visible : ViewStates.Gone;
        _network.Text = _host.Settings.DownloadOnlyOnUnmeteredNetwork ? "Unmetered only" : "Any network";
        _network.ContentDescription = $"Host network rule: {_network.Text}. Change in Settings.";
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_spec?.LocalSignedIn == true) Dialog?.Window?.SetDimAmount(.28f);
    }

    public override void OnDestroyView()
    {
        _viewAlive = false;
        if (_host is not null) _host.SettingsChanged -= UpdateNetwork;
        _host = null; _network = null;
        _queueMenu?.Dismiss(); _queueMenu = null; _queue = null;
        _folderDialog?.Dismiss();
        _folderDialog = null; _folderChoice = null; _selectedFolder = null; _change = null;
        if (_button is not null) _button.Click -= OnSubmit;
        _button = null; _more = null; _connections = null; _when = null; _error = null;
        // An activity recreation cannot reuse an in-flight request or its consent.
        _spec = null; _submit = null;
        base.OnDestroyView();
    }
}
