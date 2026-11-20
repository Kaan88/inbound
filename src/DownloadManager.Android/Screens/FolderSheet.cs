using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

/// <summary>
/// "Save to" on a server: its folders, one level at a time, read from the server through folders.browse. "Save here" takes the folder shown.
/// A dialog (not a fragment): turning the phone closes it and the field keeps what was chosen before.
/// </summary>
internal static class FolderSheet
{
    public static BottomSheetDialog Show(MainActivity activity, DownloadsModel host, string? start, Action<string> chosen,
        CancellationToken routeLifetime = default)
    {
        var picker = new FolderPicker(host);
        var dialog = new BottomSheetDialog(activity);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(routeLifetime);
        var token = lifetime.Token;
        bool loading = false;
        string? displayedChoice = null;
        var dp = (int d) => Motion.Dp(activity, d);
        int Attr(int a) => Motion.Attr(activity, a);

        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(dp(16), dp(8), dp(16), dp(16));

        var title = new TextView(activity) { Text = "Save to, on " + activity.HostName };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
        title.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
        title.SetPadding(dp(8), 0, dp(8), dp(8));
        root.AddView(title);

        var path = new TextView(activity);
        path.SetSingleLine(true);
        var pathText = "";
        void FitPath()
        {
            var room = root.Width - root.PaddingLeft - root.PaddingRight - path.PaddingLeft - path.PaddingRight - dp(16);
            path.Text = room > 0 ? MiddleEllipsis.Cut(pathText, s => path.Paint!.MeasureText(s) <= room) : pathText;
        }
        path.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        path.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
        path.SetPadding(dp(8), 0, dp(8), dp(8));
        root.AddView(path);

        var note = new TextView(activity);
        note.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
        note.SetTextColor(new Color(activity.GetColor(Resource.Color.state_failed)));
        note.SetPadding(dp(8), 0, dp(8), dp(8));
        note.Visibility = ViewStates.Gone;
        root.AddView(note);

        var scroll = new ScrollView(activity);
        var rows = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        scroll.AddView(rows);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, dp(320)));

        var buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.End);
        buttons.SetPadding(0, dp(12), 0, 0);
        var cancel = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Cancel" };
        var save = new MaterialButton(activity) { Text = "Save here", Enabled = false };
        buttons.AddView(cancel);
        buttons.AddView(save, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = dp(8) });
        root.AddView(buttons);
        dialog.SetContentView(root);

        View Row(string text, int icon, Action click, string? label = null)
        {
            var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(dp(56));
            row.SetPadding(dp(8), 0, dp(8), 0);
            var tv = new TypedValueHolder(activity).SelectableBackground();
            if (tv is not null) row.Background = tv;
            var img = new ImageView(activity);
            img.SetImageResource(icon);
            img.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
            img.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(img, new LinearLayout.LayoutParams(dp(24), dp(24)));
            var label1 = new TextView(activity) { Text = text, Ellipsize = Android.Text.TextUtils.TruncateAt.End };
            label1.SetSingleLine(true);
            label1.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyLarge);
            label1.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
            row.AddView(label1, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = dp(16) });
            row.ContentDescription = label ?? text;
            row.Click += (_, _) => click();
            return row;
        }

        void Render()
        {
            rows.RemoveAllViews();
            var shown = picker.Path;
            pathText = shown ?? "Folders you can use";
            FitPath();
            path.Post(FitPath);
            path.ContentDescription = shown is null ? "Folders you can use" : "Folder " + shown;
            displayedChoice = picker.Error is null && picker.CanChoose ? shown : null;
            save.Enabled = displayedChoice is not null;
            note.Visibility = picker.Error is null ? ViewStates.Gone : ViewStates.Visible;
            note.Text = picker.Error;
            if (picker.Path is not null)
                rows.AddView(Row("Up", Resource.Drawable.ic_arrow_back, () => Go(picker.UpAsync), "Up one folder"));
            var list = picker.Path is null ? picker.Roots.Select(r => new DownloadManager.Contracts.FolderEntryDto { Name = r, Path = r }).ToList() : picker.Folders.ToList();
            foreach (var f in list)
            {
                var p = f.Path;
                rows.AddView(Row(f.Name, Resource.Drawable.ic_folder, () => Go(ct => picker.OpenAsync(p, ct))));
            }

            if (list.Count == 0 && picker.Error is null)
            {
                var empty = new TextView(activity) { Text = "No folders inside this one" };
                empty.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
                empty.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant)));
                empty.SetPadding(dp(8), dp(16), dp(8), dp(16));
                rows.AddView(empty);
            }

            if (picker.Truncated)
            {
                var more = new TextView(activity) { Text = "Only the first folders are listed" };
                more.SetPadding(dp(8), dp(8), dp(8), dp(8));
                rows.AddView(more);
            }
        }

        async void Go(Func<CancellationToken, Task> step)
        {
            if (loading || token.IsCancellationRequested || !dialog.IsShowing) return;
            loading = true;
            displayedChoice = null;
            save.Enabled = false;
            rows.RemoveAllViews();
            note.Text = "Reading folders…";
            note.Visibility = ViewStates.Visible;
            try
            {
                await step(token);
                if (!token.IsCancellationRequested && dialog.IsShowing) Render();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!token.IsCancellationRequested && dialog.IsShowing)
                {
                    note.Text = "Could not read folders. Close and try again.";
                    note.Visibility = ViewStates.Visible;
                }
            }
            finally { loading = false; }
        }

        dialog.DismissEvent += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
        };
        cancel.Click += (_, _) => dialog.Dismiss();
        save.Click += (_, _) =>
        {
            if (loading || token.IsCancellationRequested || displayedChoice is not { } p) return;
            chosen(p);
            dialog.Dismiss();
        };
        dialog.Show();
        Go(ct => picker.OpenAsync(start, ct));
        return dialog;
    }

    private sealed class TypedValueHolder(Context ctx)
    {
        public Android.Graphics.Drawables.Drawable? SelectableBackground()
        {
            var tv = new Android.Util.TypedValue();
            if (!ctx.Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, tv, true)) return null;
            return ctx.GetDrawable(tv.ResourceId);
        }
    }
}
