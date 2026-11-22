using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Archives;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.CheckBox;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// "Archive password" (t6), a bottom sheet over the list. The password is masked while typed, goes only to the Host, is wiped from the
/// field as soon as it is sent or the sheet closes, and is never logged or shown again. A dialog, not a fragment, like the other
/// programmatic sheets: turning the phone closes it.
/// </summary>
internal static class ArchivePasswordSheet
{
    /// <param name="applied">Runs on the UI thread with the set as the Host has it after it took the password.</param>
    public static BottomSheetDialog Show(MainActivity activity, ArchiveClient client, ArchiveSetDto set, Action<ArchiveSetDto?> applied)
    {
        var dp = (int d) => Motion.Dp(activity, d);
        int Attr(int a) => Motion.Attr(activity, a);
        var dialog = new BottomSheetDialog(activity);
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(dp(24), dp(8), dp(24), dp(20));

        var title = new TextView(activity) { Text = "Archive password" };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
        title.SetTextColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorOnSurface)));
        title.AccessibilityHeading = true;
        root.AddView(title);

        var line = new TextView(activity) { Text = ArchiveRowInfo.PasswordSheetLine(set) };
        line.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        line.SetTextColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
        root.AddView(line, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });

        var layout = (TextInputLayout)Android.Views.LayoutInflater.From(activity)!.Inflate(Resource.Layout.password_field, null, false)!;
        layout.Hint = "Password";
        var input = layout.FindViewById<TextInputEditText>(Resource.Id.password_input)!;
        root.AddView(layout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(16) });
        var secure = activity.SecureHostConnection;
        if (!secure)
        {
            input.Enabled = false;
            var note = new TextView(activity) { Text = MainActivity.SecretNeedsHttps };
            note.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
            note.SetTextColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
            root.AddView(note, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });
        }

        // t6: the password joins the Host's saved list (on by default, as drawn), and this set's own delete choice (the setting's by default).
        // An older paired Host ignores both and doesn't send the set's choice: the boxes would show a wrong state (Fable F1).
        var extras = activity.HasArchiveSavedPasswords;
        var save = new MaterialCheckBox(activity) { Text = "Save it for other archives", Checked = true };
        var delete = new MaterialCheckBox(activity) { Text = "Delete the archive after extracting", Checked = set.DeleteArchives };
        if (extras)
        {
            root.AddView(save, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });
            root.AddView(delete, new LinearLayout.LayoutParams(-1, -2));
        }

        if (!string.IsNullOrWhiteSpace(set.Destination))
        {
            // t6: the folder as Save to writes it ("Download/project-files"), one line cut in the middle for a PC's long path.
            var destination = new TextView(activity) { Text = "Extracts to " + SavePathWords.ForField(set.Destination.Trim()) };
            destination.SetSingleLine(true);
            destination.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
            destination.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
            destination.SetTextColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
            root.AddView(destination, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(14) });
        }

        var error = new TextView(activity) { Visibility = ViewStates.Gone, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        error.SetTextColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorError)));
        root.AddView(error, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });

        var buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        var later = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Not now", CornerRadius = dp(28) };
        buttons.AddView(later, new LinearLayout.LayoutParams(0, dp(56), 1));
        var extract = new MaterialButton(activity) { Text = "Extract", CornerRadius = dp(28) };
        buttons.AddView(extract, new LinearLayout.LayoutParams(0, dp(56), 1) { MarginStart = dp(12) });
        root.AddView(buttons, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(18) });
        dialog.SetContentView(root);
        dialog.DismissEvent += (_, _) => input.Text = "";
        MainActivity.KeepSecret(dialog);
        extract.Enabled = secure;

        var busy = false;
        void Fail(string words)
        {
            busy = false; later.Enabled = true; extract.Enabled = secure; extract.Text = "Extract";
            error.Text = words; error.Visibility = ViewStates.Visible;
        }

        later.Click += (_, _) => dialog.Dismiss();
        void Send()
        {
            if (busy || !secure) return;
            var password = input.Text ?? "";
            if (password.Length == 0) { Fail("Enter the password first."); return; }
            busy = true; extract.Enabled = later.Enabled = false; extract.Text = "Extracting…"; error.Visibility = ViewStates.Gone;
            // The text leaves the field now: only the request holds it, and nothing is logged.
            input.Text = "";
            var saveForOthers = extras && save.Checked;
            bool? deleteArchives = extras ? delete.Checked : null;
            ArchiveUi.Run(activity, () => client.SetPasswordAsync(set.Id, password, saveForOthers, deleteArchives), result =>
            {
                if (result.Ok) { dialog.Dismiss(); applied(result.Set); }
                else Fail(string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept the password." : result.Error.Trim());
            }, Fail);
        }

        extract.Click += (_, _) => Send();
        input.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Done) { Send(); e.Handled = true; }
        };

        dialog.Show();
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        input.RequestFocus();
        return dialog;
    }
}

/// <summary>Runs an archive request off the UI thread and hands its result back on it, with failures as plain words.</summary>
internal static class ArchiveUi
{
    public static void Run<T>(MainActivity activity, Func<Task<T>> work, Action<T> done, Action<string> failed) =>
        TorBoxUi.Run(activity, work, done, failed);
}
