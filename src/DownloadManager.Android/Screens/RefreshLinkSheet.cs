using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

/// <summary>Transient UI only; the caller owns refresh authorization and Host commands.</summary>
[Register("app.inbound.android.RefreshLinkSheet")]
internal sealed class RefreshLinkSheet : BottomSheetDialogFragment
{
    private const string SheetTag = "refresh_link";
    private string? _fileName;
    private long _savedBytes;
    private long? _totalBytes;
    private bool _knownExpired, _alive, _busy;
    private Action? _openPage;
    private Func<string, Task<bool>>? _paste;
    private View? _open, _reveal, _entry, _clipboard;
    private EditText? _address;
    private MaterialButton? _refresh;
    private TextView? _error;

    public RefreshLinkSheet() { }
    private RefreshLinkSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public static void Show(MainActivity activity, string fileName, long savedBytes, long? totalBytes, bool knownExpired,
        Action openPage, Func<string, Task<bool>> paste)
    {
        if (activity.SupportFragmentManager.FindFragmentByTag(SheetTag) is not null) return;
        new RefreshLinkSheet { _fileName = fileName, _savedBytes = savedBytes, _totalBytes = totalBytes, _knownExpired = knownExpired,
            _openPage = openPage, _paste = paste }.Show(activity.SupportFragmentManager, SheetTag);
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        if (_openPage is null || _paste is null || _fileName is null) { DismissAllowingStateLoss(); return null; }
        var view = inflater.Inflate(Resource.Layout.refresh_link_sheet, container, false)!;
        view.SaveEnabled = false;
        _alive = true;
        view.FindViewById<TextView>(Resource.Id.refresh_title)!.Text = _knownExpired ? "This link has expired" : "Try a fresh link";
        var saved = DownloadManager.Mobile.Fmt.Bytes(Math.Max(0, _savedBytes));
        var description = _totalBytes is > 0
            ? $"{saved} of {DownloadManager.Mobile.Fmt.Bytes(_totalBytes.Value)} is saved."
            : $"{saved} saved.";
        view.FindViewById<TextView>(Resource.Id.refresh_description)!.Text = description
            + $" Open the page again and start the download there; Inbound uses the new address and continues from {saved}.";
        _open = view.FindViewById<View>(Resource.Id.refresh_open_page)!;
        _reveal = view.FindViewById<View>(Resource.Id.refresh_reveal)!;
        _entry = view.FindViewById<View>(Resource.Id.refresh_entry)!;
        _clipboard = view.FindViewById<View>(Resource.Id.refresh_clipboard)!;
        _address = view.FindViewById<EditText>(Resource.Id.refresh_address)!;
        _address.SaveEnabled = false;
        _refresh = view.FindViewById<MaterialButton>(Resource.Id.refresh_submit)!;
        _error = view.FindViewById<TextView>(Resource.Id.refresh_error)!;
        _open.Click += OnOpen; _reveal.Click += OnReveal; _clipboard.Click += OnClipboard; _refresh.Click += OnRefresh;
        return view;
    }

    public override void OnStart()
    {
        base.OnStart();
        var window = Dialog?.Window;
        window?.AddFlags(WindowManagerFlags.DimBehind);
        window?.SetDimAmount(0.6f);
    }

    private void OnOpen(object? sender, EventArgs e)
    {
        if (_busy || !_alive || _openPage is null) return;
        var callback = _openPage;
        try { callback(); if (_alive && IsAdded) DismissAllowingStateLoss(); }
        catch (Exception) { ShowError("Could not open the page. Try again."); }
    }
    private void OnReveal(object? sender, EventArgs e)
    { if (!_busy && _alive && _entry is not null) { _entry.Visibility = ViewStates.Visible; _address?.RequestFocus(); } }
    private void OnClipboard(object? sender, EventArgs e)
    {
        if (_busy || !_alive || _address is null || Context is null) return;
        var value = Outside.ClipboardText(Context);
        if (value is not null) _address.Text = value.Trim();
    }
    private async void OnRefresh(object? sender, EventArgs e)
    {
        if (_busy || !_alive || _paste is null || _address is null) return;
        var value = _address.Text?.Trim() ?? "";
        if (value.Length == 0) { ShowError("Enter a new address first."); return; }
        var callback = _paste;
        _busy = true; Cancelable = false; SetEnabled(false);
        try
        {
            var accepted = await callback(value);
            if (!_alive || !IsAdded) return;
            if (accepted) { DismissAllowingStateLoss(); return; }
            ShowError("Could not refresh this link. Try again.");
        }
        catch (Exception) { if (_alive && IsAdded) ShowError("Could not refresh this link. Try again."); }
        finally { _busy = false; if (_alive && IsAdded) { Cancelable = true; SetEnabled(true); } }
    }
    private void ShowError(string text) { if (_error is not null) { _error.Text = text; _error.Visibility = ViewStates.Visible; } }
    private void SetEnabled(bool enabled)
    {
        foreach (var v in new View?[] { _open, _reveal, _clipboard, _address, _refresh }) if (v is not null) v.Enabled = enabled;
    }
    public override void OnDestroyView()
    {
        _alive = false;
        if (_open is not null) _open.Click -= OnOpen;
        if (_reveal is not null) _reveal.Click -= OnReveal;
        if (_clipboard is not null) _clipboard.Click -= OnClipboard;
        if (_refresh is not null) _refresh.Click -= OnRefresh;
        _open = _reveal = _entry = _clipboard = null; _address = null; _refresh = null; _error = null;
        _openPage = null; _paste = null; _fileName = null; _totalBytes = null;
        base.OnDestroyView();
    }
}
