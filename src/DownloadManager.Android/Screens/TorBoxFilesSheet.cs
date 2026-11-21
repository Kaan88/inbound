using Android.Views;
using Android.Widget;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

/// <summary>
/// The files of one TorBox job. With <c>choose</c> (Get) the person picks files and Add to Downloads hands them to the Host through
/// remote.select; otherwise it is a read-only list. A dialog, not a fragment, like the folder sheet: turning the phone closes it.
/// </summary>
internal static class TorBoxFilesSheet
{
    public static BottomSheetDialog Show(MainActivity activity, TorBoxClient client, TorBoxRow row, bool choose, Action added)
    {
        var dp = (int d) => Motion.Dp(activity, d);
        var dialog = new BottomSheetDialog(activity);
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(dp(16), dp(8), dp(16), dp(16));

        var title = new TextView(activity) { Text = row.Name };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
        title.SetTextColor(new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorOnSurface)));
        title.SetPadding(dp(8), 0, dp(8), dp(8));
        title.AccessibilityHeading = true;
        root.AddView(title);

        var summary = new TextView(activity) { Text = "Reading the file list…", AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        summary.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        summary.SetTextColor(new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorOnSurfaceVariant)));
        summary.SetPadding(dp(8), 0, dp(8), dp(8));
        root.AddView(summary);

        var scroll = new ScrollView(activity);
        var list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        scroll.AddView(list);
        root.AddView(scroll, new LinearLayout.LayoutParams(-1, dp(360)));

        var error = new TextView(activity) { Visibility = ViewStates.Gone, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        error.SetTextColor(new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorError)));
        error.SetPadding(dp(8), dp(8), dp(8), 0);
        root.AddView(error);

        var buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.CenterVertical);
        var cancel = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = choose ? "Cancel" : "Close", CornerRadius = dp(28) };
        cancel.SetMinimumWidth(dp(96));
        buttons.AddView(cancel, new LinearLayout.LayoutParams(-2, dp(56)));
        var add = new MaterialButton(activity) { Text = "Add to Downloads", Enabled = false, CornerRadius = dp(28), Visibility = choose ? ViewStates.Visible : ViewStates.Gone };
        add.SetIconResource(Resource.Drawable.ic_remote_download);
        add.IconGravity = MaterialButton.IconGravityTextStart;
        buttons.AddView(add, new LinearLayout.LayoutParams(0, dp(56), 1) { MarginStart = dp(12) });
        root.AddView(buttons, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(12) });
        dialog.SetContentView(root);

        TorBoxFileTree? tree = null;
        var busy = false;
        void Changed()
        {
            if (tree is null) return;
            summary.Text = choose ? tree.Summary : $"{tree.TotalCount} {(tree.TotalCount == 1 ? "file" : "files")} · {DownloadManager.Mobile.Fmt.Bytes(tree.TotalBytes)}";
            add.Enabled = !busy && tree.SelectedCount > 0;
        }

        cancel.Click += (_, _) => dialog.Dismiss();
        add.Click += (_, _) =>
        {
            if (tree is null || busy || tree.SelectedCount == 0) return;
            busy = true; add.Enabled = false; add.Text = "Adding…"; error.Visibility = ViewStates.Gone;
            var ids = tree.SelectedFileIds;
            TorBoxUi.Run(activity, () => client.SelectAsync(row.Id, ids, false), r =>
            {
                busy = false; add.Text = "Add to Downloads";
                if (r.Ok) { dialog.Dismiss(); added(); return; }
                Changed();
                error.Text = TorBoxText.Failure(r.Error); error.Visibility = ViewStates.Visible;
            }, words => { busy = false; add.Text = "Add to Downloads"; Changed(); error.Text = words; error.Visibility = ViewStates.Visible; });
        };

        TorBoxUi.Run(activity, () => client.FilesAsync(row.Id), files =>
        {
            if (!files.Ok || files.Pending || files.Files.Count == 0)
            {
                summary.Text = !files.Ok ? TorBoxText.Failure(files.Error)
                    : files.Pending ? "TorBox hasn't listed the files yet. Try again in a moment." : "TorBox lists no files for this item.";
                return;
            }

            tree = new TorBoxFileTree(files.Files);
            TorBoxTreeView.Render(activity, list, tree, !choose, Changed);
            Changed();
        }, words => summary.Text = words);

        dialog.Show();
        // Opened at full height so a long list has room.
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        return dialog;
    }
}
