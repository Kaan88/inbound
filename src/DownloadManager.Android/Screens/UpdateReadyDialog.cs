using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Updates;
using Google.Android.Material.Dialog;

namespace Inbound.App.Screens;

/// <summary>The verified offer's plain-text release notes, bounded independently from the dialog's action buttons.</summary>
internal static class UpdateReadyDialog
{
    private sealed record ScrollViews(View Outer, View Notes);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AndroidX.AppCompat.App.AlertDialog, ScrollViews> Scrolls = new();
    public const string NoNotes = "Release notes aren’t available for this update.";
    public static string Notes(AndroidUpdateManifest manifest) => string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? NoNotes : manifest.ReleaseNotes;
    public static string Metadata(AndroidUpdateManifest manifest) => $"Version {manifest.VersionName} \u00b7 {Fmt.Bytes(manifest.Size)}";

    /// <summary>Creates but does not show a prompt; the caller owns dismissal and binds Install to this exact offer.</summary>
    public static AndroidX.AppCompat.App.AlertDialog Create(MainActivity activity, AndroidUpdateManifest manifest, Action install)
    {
        var body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var horizontal = Motion.Dp(activity, 24); body.SetPadding(horizontal, Motion.Dp(activity, 20), horizontal, Motion.Dp(activity, 8));
        var icon = new ImageView(activity); icon.SetImageResource(Resource.Mipmap.ic_launcher);
        icon.ContentDescription = "Inbound";
        body.AddView(icon, new LinearLayout.LayoutParams(Motion.Dp(activity, 48), Motion.Dp(activity, 48)) { Gravity = GravityFlags.CenterHorizontal });
        var heading = Label(activity, $"Inbound {manifest.VersionName} is ready", 23, Resource.Attribute.colorOnSurface);
        heading.AccessibilityHeading = true; body.AddView(heading, Margin(activity, 14));
        body.AddView(Label(activity, Metadata(manifest), 14, Resource.Attribute.colorOnSurfaceVariant), Margin(activity, 4));
        var title = Label(activity, "What’s new", 15, Resource.Attribute.colorOnSurface); title.AccessibilityHeading = true; title.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        body.AddView(title, Margin(activity, 16));
        var notes = CreateNotes(activity, out var text); BindNotes(text, manifest); body.AddView(notes, Margin(activity, 8));
        // The whole body may scroll on a short or large-font display; the native dialog button panel remains visible.
        var outer = new NestedScrollView(activity) { FillViewport = false }; outer.AddView(body);
        var dialog = new MaterialAlertDialogBuilder(activity).SetView(outer)!
            .SetNegativeButton("Later", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Install", (_, _) => install())!.Create()!;
        Scrolls.Add(dialog, new ScrollViews(outer, notes));
        return dialog;
    }

    public static (int Outer, int Notes) CaptureScroll(AndroidX.AppCompat.App.AlertDialog dialog) =>
        Scrolls.TryGetValue(dialog, out var views) ? (views.Outer.ScrollY, views.Notes.ScrollY) : (0, 0);
    public static void RestoreScroll(AndroidX.AppCompat.App.AlertDialog dialog, int outer, int notes)
    {
        if (!Scrolls.TryGetValue(dialog, out var views)) return;
        views.Outer.Post(() =>
        {
            if (!dialog.IsShowing) return;
            views.Outer.ScrollTo(0, Math.Max(0, outer));
            views.Notes.ScrollTo(0, Math.Max(0, notes));
        });
    }

    /// <summary>Shared by the prompt and Settings so both render exactly the same verified text.</summary>
    public static View CreateNotes(Context context, out TextView text)
    {
        var scroll = new NotesScrollView(context) { FillViewport = false };
        text = Label(context, "", 15, Resource.Attribute.colorOnSurface);
        text.SetLineSpacing(Motion.Dp(context, 2), 1f);
        text.SetTextIsSelectable(false);
        scroll.AddView(text, new Android.Views.ViewGroup.LayoutParams(-1, -2));
        return scroll;
    }
    public static void BindNotes(TextView text, AndroidUpdateManifest manifest)
    {
        var value = Notes(manifest);
        if (!ReferenceEquals(text.Tag, null) && text.Tag.ToString() == value) return;
        text.Tag = value;
        text.TextFormatted = Formatted(text.Context!, value, string.IsNullOrWhiteSpace(manifest.ReleaseNotes));
        var colour = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
            ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorOnSurface;
        text.SetTextColor(new Android.Graphics.Color(Motion.Attr(text.Context!, colour)));
    }
    /// <summary>One bullet per line of the notes, with room between them (owner, 2026-11-22: the notes were one block).</summary>
    private static Java.Lang.ICharSequence Formatted(Context context, string value, bool placeholder)
    {
        var builder = new Android.Text.SpannableStringBuilder();
        if (placeholder) { builder.Append(value); return builder; }
        var lines = value.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bullet = Motion.Attr(context, Resource.Attribute.colorPrimary);
        for (int i = 0; i < lines.Length; i++)
        {
            int start = builder.Length();
            builder.Append(lines[i].TrimStart('•', '-', '*', ' ', '\t'));
            if (i < lines.Length - 1) builder.Append("\n");
            builder.SetSpan(new Android.Text.Style.BulletSpan(Motion.Dp(context, 10), new Android.Graphics.Color(bullet), Motion.Dp(context, 3)),
                start, builder.Length(), Android.Text.SpanTypes.ExclusiveExclusive);
            if (i < lines.Length - 1)
            {
                // A short blank line between items keeps each bullet centred on its first line.
                int gap = builder.Length();
                builder.Append("\n");
                builder.SetSpan(new Android.Text.Style.AbsoluteSizeSpan(Motion.Dp(context, 8)), gap, builder.Length(), Android.Text.SpanTypes.ExclusiveExclusive);
            }
        }
        return builder;
    }

    private static TextView Label(Context context, string value, float size, int colour)
    {
        var text = new TextView(context) { Text = value, TextSize = size };
        text.SetTextColor(new Android.Graphics.Color(Motion.Attr(context, colour))); return text;
    }
    private static LinearLayout.LayoutParams Margin(Context context, int top) => new(-1, -2) { TopMargin = Motion.Dp(context, top) };

    private sealed class NotesScrollView(Context context) : NestedScrollView(context)
    {
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            // Up to 45 % of the screen (at least 160 dp): the notes were cut to a few lines (owner, 2026-11-22).
            int max = Math.Max(Motion.Dp(Context!, 160), (int)(Context!.Resources!.DisplayMetrics!.HeightPixels * 0.45));
            var mode = MeasureSpec.GetMode(heightMeasureSpec);
            int size = MeasureSpec.GetSize(heightMeasureSpec);
            if (mode == MeasureSpecMode.Unspecified || size > max)
                heightMeasureSpec = MeasureSpec.MakeMeasureSpec(max, MeasureSpecMode.AtMost);
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        }
    }
}
