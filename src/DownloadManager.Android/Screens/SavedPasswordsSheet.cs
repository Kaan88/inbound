using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Archives;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// t7's "Saved passwords": the Host's passwords for any archive, tried in this order before anyone is asked. Only their place and date
/// are shown (the Host never sends a value back); one can be added (masked, cleared once sent, never logged) or removed.
/// A sheet rather than a page, because the settings page it opens from is already the one open page.
/// </summary>
internal static class SavedPasswordsSheet
{
    /// <param name="counted">Runs on the UI thread with the number saved, whenever the Host answers.</param>
    public static void Show(MainActivity activity, ArchiveClient client, Action<int> counted)
    {
        var dp = (int d) => Motion.Dp(activity, d);
        Color Attr(int a) => new(Motion.Attr(activity, a));
        var dialog = new BottomSheetDialog(activity);
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(dp(24), dp(8), dp(24), dp(20));

        var title = new TextView(activity) { Text = "Saved passwords", AccessibilityHeading = true };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
        title.SetTextColor(Attr(Resource.Attribute.colorOnSurface));
        root.AddView(title);
        var about = new TextView(activity) { Text = "Tried in this order on an archive that needs a password, before you are asked. They can't be shown again." };
        about.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        about.SetTextColor(Attr(Resource.Attribute.colorOnSurfaceVariant));
        root.AddView(about, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });

        var list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.AddView(list, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(12) });

        var layout = (TextInputLayout)Android.Views.LayoutInflater.From(activity)!.Inflate(Resource.Layout.password_field, null, false)!;
        layout.Hint = "Add a password";
        var input = layout.FindViewById<TextInputEditText>(Resource.Id.password_input)!;
        root.AddView(layout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(12) });

        var error = new TextView(activity) { Visibility = ViewStates.Gone, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        error.SetTextColor(Attr(Resource.Attribute.colorError));
        root.AddView(error, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8) });

        var buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        var close = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Done", CornerRadius = dp(28) };
        buttons.AddView(close, new LinearLayout.LayoutParams(0, dp(56), 1));
        var add = new MaterialButton(activity) { Text = "Add", CornerRadius = dp(28) };
        buttons.AddView(add, new LinearLayout.LayoutParams(0, dp(56), 1) { MarginStart = dp(12) });
        root.AddView(buttons, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(18) });

        var scroll = new ScrollView(activity);
        scroll.AddView(root);
        dialog.SetContentView(scroll);
        dialog.DismissEvent += (_, _) => input.Text = "";

        var busy = false;
        void Fail(string words)
        {
            busy = false; add.Enabled = true;
            error.Text = words; error.Visibility = ViewStates.Visible;
        }

        void Shown(ArchiveSavedPasswordsResult result)
        {
            busy = false; add.Enabled = true;
            if (!result.Ok) Fail(string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept that." : result.Error.Trim());
            else error.Visibility = ViewStates.Gone;
            Fill(result.Passwords);
            counted(result.Passwords.Count);
        }

        void Fill(List<ArchiveSavedPasswordDto> passwords)
        {
            list.RemoveAllViews();
            if (passwords.Count == 0)
            {
                var none = new TextView(activity) { Text = "None saved yet." };
                none.SetTextColor(Attr(Resource.Attribute.colorOnSurfaceVariant));
                list.AddView(none);
                return;
            }

            for (var i = 0; i < passwords.Count; i++)
            {
                var saved = passwords[i];
                var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetMinimumHeight(dp(56));
                var text = new LinearLayout(activity) { Orientation = Orientation.Vertical };
                var name = new TextView(activity) { Text = $"Password {i + 1}", TextSize = 17 };
                name.SetTextColor(Attr(Resource.Attribute.colorOnSurface));
                var date = new TextView(activity) { Text = "Added " + saved.AddedAt.ToLocalTime().ToString("d MMM yyyy"), TextSize = 14 };
                date.SetTextColor(Attr(Resource.Attribute.colorOnSurfaceVariant));
                text.AddView(name);
                text.AddView(date);
                row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
                var remove = new ImageButton(activity) { ContentDescription = $"Remove password {i + 1}" };
                remove.SetImageResource(Resource.Drawable.ic_delete);
                remove.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(Attr(Resource.Attribute.colorOnSurfaceVariant));
                remove.Background = new TypedValueHolder(activity).SelectableBackground();
                row.AddView(remove, new LinearLayout.LayoutParams(dp(48), dp(48)));
                var place = i + 1;
                remove.Click += (_, _) =>
                {
                    if (busy) return;
                    new MaterialAlertDialogBuilder(activity).SetTitle($"Remove password {place}?")!
                        .SetMessage("It is no longer tried on archives. Archives it already opened stay extracted.")!
                        .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
                        .SetPositiveButton("Remove", (_, _) =>
                        {
                            busy = true;
                            ArchiveUi.Run(activity, () => client.RemoveSavedPasswordAsync(saved.Id), Shown, Fail);
                        })!
                        .Show();
                };
                list.AddView(row);
            }
        }

        void Add()
        {
            if (busy) return;
            var password = input.Text ?? "";
            if (password.Length == 0) { Fail("Enter the password first."); return; }
            busy = true; add.Enabled = false; error.Visibility = ViewStates.Gone;
            // The text leaves the field now: only the request holds it, and nothing is logged.
            input.Text = "";
            ArchiveUi.Run(activity, () => client.AddSavedPasswordAsync(password), Shown, Fail);
        }

        add.Click += (_, _) => Add();
        close.Click += (_, _) => dialog.Dismiss();
        input.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Done) { Add(); e.Handled = true; }
        };

        dialog.Show();
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        busy = true;
        ArchiveUi.Run(activity, () => client.SavedPasswordsAsync(), Shown, Fail);
    }
}
