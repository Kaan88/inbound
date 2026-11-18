using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.BottomSheet;
using Inbound.App.Screens;

namespace Inbound.App;

/// <summary>
/// The Add sheet (A4): a modal bottom sheet. It offers the link on the clipboard (only offered, never added by itself) and the way to add
/// one or several links. Torrents, sites, TorBox and list import are not built, so they are not listed. Motion: 400 ms emphasized (theme).
/// </summary>
[Register("app.inbound.android.AddSheet")]
public sealed class AddSheet : BottomSheetDialogFragment
{
    private View? _card;
    private TextView? _text;
    private string? _link;

    public AddSheet() { }

    private AddSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var v = inflater.Inflate(Resource.Layout.add_sheet, container, false)!;
        _card = v.FindViewById<View>(Resource.Id.clip_card);
        _text = v.FindViewById<TextView>(Resource.Id.clip_text);
        v.FindViewById<View>(Resource.Id.entry_link)!.Click += (_, _) => Go(null);
        v.FindViewById<View>(Resource.Id.entry_browser)!.Click += (_, _) => { var activity = Activity as MainActivity; Dismiss(); activity?.OpenBrowser(); };
        v.FindViewById<View>(Resource.Id.clip_use)!.Click += (_, _) => Go(_link);
        // The clipboard can only be read while this window has the focus (Android 10 and later), so it is read when the focus arrives.
        v.ViewTreeObserver!.WindowFocusChange += (_, e) => { if (e.HasFocus) ReadClipboard(); };
        return v;
    }

    private void ReadClipboard()
    {
        if (Context is null) return;
        _link = ClipboardOffer.Find(Outside.ClipboardText(Context));
        if (_card is null) return;
        _card.Visibility = _link is null ? ViewStates.Gone : ViewStates.Visible;
        if (_link is not null && _text is not null) _text.Text = ClipboardOffer.Shorten(_link);
    }

    // The link goes to the activity the sheet is attached to, never to one captured earlier (a restored sheet gets a new activity).
    private void Go(string? link)
    {
        var activity = Activity as MainActivity;
        Dismiss();
        activity?.OpenNewDownload(link);
    }
}
