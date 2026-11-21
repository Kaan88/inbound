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
/// one or several links. Unavailable sources stay visible in the approved order without opening unfinished flows.
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
        v.FindViewById<View>(Resource.Id.entry_browser)!.Click += (_, _) =>
        {
            if (Activity is not MainActivity activity) return;
            var menu = new PopupMenu(activity, v.FindViewById<View>(Resource.Id.entry_browser)!);
            menu.Menu!.Add("Browse and catch a download");
            menu.MenuItemClick += (_, _) => { Dismiss(); activity.OpenBrowser(); };
            menu.Show();
        };
        var rows = v.FindViewById<LinearLayout>(Resource.Id.add_source_rows)!;
        var torBox = Activity is MainActivity tb && tb.HasTorBox;
        Source(Resource.Drawable.ic_add_source_torrent, "Torrent file or magnet", "Choose which files to fetch",
            torBox ? () => { var a = Activity as MainActivity; Dismiss(); a?.OpenAddTorrent(); } : null);
        Source(Resource.Drawable.ic_add_source_cloud, "From a site", "Browse SFTP, WebDAV or cloud storage",
            Activity is MainActivity host && host.HasRemoteFilesSource ? () => { Dismiss(); host.OpenRemoteFilesSource(); } : null);
        Source(Resource.Drawable.ic_flow_bolt, "TorBox", "Send a link or torrent to your TorBox account",
            torBox ? () => { var a = Activity as MainActivity; Dismiss(); a?.OpenTorBox(); } : null);
        Source(Resource.Drawable.ic_add_source_import, "Import a list", "A list exported from any Inbound");
        v.FindViewById<View>(Resource.Id.clip_use)!.Click += (_, _) => Go(_link);
        // The clipboard can only be read while this window has the focus (Android 10 and later), so it is read when the focus arrives.
        v.ViewTreeObserver!.WindowFocusChange += (_, e) => { if (e.HasFocus) ReadClipboard(); };
        return v;

        void Source(int icon, string label, string summary, Action? open = null)
        {
            var row = inflater.Inflate(Resource.Layout.add_source_row, rows, false)!;
            row.FindViewById<ImageView>(Resource.Id.add_source_icon)!.SetImageResource(icon);
            row.FindViewById<TextView>(Resource.Id.add_source_title)!.Text = label;
            row.FindViewById<TextView>(Resource.Id.add_source_summary)!.Text = summary;
            row.ContentDescription = open is null ? $"{label}. {summary}. Not available." : $"{label}. {summary}.";
            row.Enabled = open is not null;
            row.Focusable = open is not null;
            if (open is not null) row.Click += (_, _) => open();
            else
            {
                row.FindViewById<View>(Resource.Id.add_source_chevron)!.Visibility = ViewStates.Gone;
                row.FindViewById<View>(Resource.Id.add_source_unavailable)!.Visibility = ViewStates.Visible;
            }
            rows.AddView(row);
        }
    }

    public override Android.App.Dialog OnCreateDialog(Bundle? savedInstanceState) => new AnimatedSheetDialog(RequireActivity(), 0.62f);

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
