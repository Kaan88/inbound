using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

internal sealed record CaughtSheetSpec(string FileName, string Detail, string Destination, string Privacy, int DefaultConnections, int MaxConnections);

/// <summary>Transient confirmation only. The caller owns the request and durable submission.</summary>
[Register("app.inbound.android.CaughtDownloadSheet")]
internal sealed class CaughtDownloadSheet : BottomSheetDialogFragment
{
    private const string SheetTag = "caught_download";
    private CaughtSheetSpec? _spec;
    private Func<int, bool, Task<bool>>? _submit;
    private MaterialButton? _button;
    private Spinner? _connections;
    private RadioGroup? _when;
    private TextView? _error;
    private int[] _choices = [];
    private bool _busy, _viewAlive;

    public CaughtDownloadSheet() { }
    private CaughtDownloadSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    internal static void Show(MainActivity activity, CaughtSheetSpec spec, Func<int, bool, Task<bool>> submit)
    {
        if (activity.SupportFragmentManager.FindFragmentByTag(SheetTag) is not null) return;
        new CaughtDownloadSheet { _spec = spec, _submit = submit }.Show(activity.SupportFragmentManager, SheetTag);
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        // A restored fragment never restores a callback, request or consent.
        if (_spec is null || _submit is null)
        {
            DismissAllowingStateLoss();
            return null;
        }
        var view = inflater.Inflate(Resource.Layout.caught_download_sheet, container, false)!;
        view.SaveEnabled = false;
        _viewAlive = true;
        view.FindViewById<TextView>(Resource.Id.caught_name)!.Text = _spec.FileName;
        view.FindViewById<TextView>(Resource.Id.caught_detail)!.Text = _spec.Detail;
        view.FindViewById<TextView>(Resource.Id.caught_destination)!.Text = _spec.Destination;
        view.FindViewById<TextView>(Resource.Id.caught_privacy)!.Text = _spec.Privacy;
        _connections = view.FindViewById<Spinner>(Resource.Id.caught_connections)!;
        _when = view.FindViewById<RadioGroup>(Resource.Id.caught_when)!;
        _button = view.FindViewById<MaterialButton>(Resource.Id.caught_submit)!;
        _error = view.FindViewById<TextView>(Resource.Id.caught_error)!;
        var max = Math.Max(1, _spec.MaxConnections);
        var preferred = Math.Clamp(_spec.DefaultConnections, 1, max);
        var choices = new SortedSet<int> { 1, preferred, max };
        for (var n = 2; n < max && n > 0; n *= 2) choices.Add(n);
        _choices = choices.ToArray();
        var adapter = new ArrayAdapter<string>(RequireContext(), Android.Resource.Layout.SimpleSpinnerItem, _choices.Select(n => n.ToString()).ToArray());
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _connections.Adapter = adapter;
        _connections.SetSelection(Array.IndexOf(_choices, preferred));
        _button.Click += OnSubmit;
        return view;
    }

    private async void OnSubmit(object? sender, EventArgs e)
    {
        if (_busy || !_viewAlive || _submit is null || _connections is null || _when is null) return;
        var index = _connections.SelectedItemPosition;
        if (index < 0 || index >= _choices.Length) return;
        var callback = _submit;
        _busy = true; Cancelable = false;
        SetEnabled(false);
        try
        {
            var accepted = await callback(_choices[index], _when.CheckedRadioButtonId == Resource.Id.caught_later);
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
        if (_connections is not null) _connections.Enabled = enabled;
        if (_when is not null) for (var i = 0; i < _when.ChildCount; i++) _when.GetChildAt(i)!.Enabled = enabled;
    }

    public override void OnDestroyView()
    {
        _viewAlive = false;
        if (_button is not null) _button.Click -= OnSubmit;
        _button = null; _connections = null; _when = null; _error = null;
        // An activity recreation cannot reuse an in-flight request or its consent.
        _spec = null; _submit = null;
        base.OnDestroyView();
    }
}
