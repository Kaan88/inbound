using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Util;
using Android.Views;
using AndroidX.Core.View;
using DownloadManager.Mobile;
using Google.Android.Material.Dialog;
using Inbound.App.Background;

namespace Inbound.App.Screens;

/// <summary>The app's small settings (theme, dynamic colour, first run done) in SharedPreferences.</summary>
internal sealed class PrefsStore(Context context) : IKeyValueStore
{
    private readonly ISharedPreferences _prefs = context.GetSharedPreferences("inbound", FileCreationMode.Private)!;

    public string? GetString(string key) => _prefs.GetString(key, null);

    public void SetString(string key, string value) => _prefs.Edit()!.PutString(key, value)!.Apply();

    public bool GetBool(string key) => _prefs.GetBoolean(key, false);

    public void SetBool(string key, bool value) => _prefs.Edit()!.PutBoolean(key, value)!.Apply();
}

internal static class Motion
{
    public const int ContainerMs = 450;
    public const int NavigationMs = 200;

    /// <summary>"Remove animations" (or an animator scale of 0): the system plays no animator, so a screen change is a cross-fade done by hand.</summary>
    public static bool Reduced(Context c) =>
        Settings.Global.GetFloat(c.ContentResolver, Settings.Global.AnimatorDurationScale, 1f) == 0f;

    /// <summary>
    /// A cross-fade that runs even when animators are turned off (a TimeAnimator ticks in real time and is not scaled). Used instead of
    /// the container transform when the system asks for no animation, so a change is still gentle, and progress still updates.
    /// </summary>
    public static void CrossFade(View from, View to, int ms = 150, Action? done = null)
    {
        to.Alpha = 0f;
        to.Visibility = ViewStates.Visible;
        var animator = new Android.Animation.TimeAnimator();
        animator.Time += (_, e) =>
        {
            var t = Math.Min(1f, e.TotalTime / (float)ms);
            to.Alpha = t;
            from.Alpha = 1f - t;
            if (t < 1f) return;
            animator.Cancel();
            from.Alpha = 1f;
            to.Alpha = 1f;
            done?.Invoke();
        };
        animator.Start();
    }

    /// <summary>Fades <paramref name="view"/> out in real time (as <see cref="CrossFade"/>, for when animators are off), then runs <paramref name="done"/>.</summary>
    public static void FadeOut(View view, int ms, Action done)
    {
        var animator = new Android.Animation.TimeAnimator();
        animator.Time += (_, e) =>
        {
            var t = Math.Min(1f, e.TotalTime / (float)ms);
            view.Alpha = 1f - t;
            if (t < 1f) return;
            animator.Cancel();
            done();
        };
        animator.Start();
    }

    /// <summary>Navigation: 200 ms fade through.</summary>
    public static Google.Android.Material.Transition.MaterialFadeThrough Fade()
    {
        var t = new Google.Android.Material.Transition.MaterialFadeThrough();
        t.SetDuration(NavigationMs);
        return t;
    }

    public static int Dp(Context c, float dp) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, c.Resources!.DisplayMetrics);

    public static int Attr(Context c, int attr)
    {
        var tv = new TypedValue();
        c.Theme!.ResolveAttribute(attr, tv, true);
        return tv.Data;
    }
}

internal sealed class InsetsListener(Func<View?, WindowInsetsCompat?, WindowInsetsCompat?> f) : Java.Lang.Object, IOnApplyWindowInsetsListener
{
    public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets) => f(v, insets);

    /// <summary>Pads <paramref name="view"/> by the system bars on the chosen sides, on top of the padding it has. Does not consume the insets.</summary>
    public static void Pad(View view, bool top, bool bottom)
    {
        int l = view.PaddingLeft, t = view.PaddingTop, r = view.PaddingRight, b = view.PaddingBottom;
        ViewCompat.SetOnApplyWindowInsetsListener(view, new InsetsListener((v, insets) =>
        {
            if (insets?.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout()) is { } bars)
            {
                // The keyboard counts as a bottom bar, so a page with a bottom button keeps it above the keyboard.
                var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime())?.Bottom ?? 0;
                v!.SetPadding(l + bars.Left, top ? t + bars.Top : t, r + bars.Right, bottom ? b + Math.Max(bars.Bottom, ime) : b);
            }
            return insets;
        }));
        ViewCompat.RequestApplyInsets(view);
    }
}

internal sealed class ClickListener(Action run) : Java.Lang.Object, View.IOnClickListener
{
    public void OnClick(View? v) => run();
}

/// <summary>The icon of a row and the colour its kind of file has (the colours of the mockup's rows).</summary>
internal static class FileIcons
{
    public static (int Drawable, int ColorAttr, int ColorRes) For(FileKind kind) => kind switch
    {
        FileKind.Disc => (Resource.Drawable.ic_disc, Resource.Attribute.colorPrimary, 0),
        FileKind.Archive => (Resource.Drawable.ic_archive, Resource.Attribute.colorTertiary, 0),
        FileKind.Video => (Resource.Drawable.ic_video, 0, Resource.Color.state_media),
        FileKind.Audio => (Resource.Drawable.ic_audio, 0, Resource.Color.state_media),
        FileKind.Image => (Resource.Drawable.ic_image, 0, Resource.Color.state_done),
        FileKind.Document => (Resource.Drawable.ic_file, Resource.Attribute.colorPrimary, 0),
        _ => (Resource.Drawable.ic_file, Resource.Attribute.colorOnSurfaceVariant, 0),
    };

    public static int Color(Context c, (int Drawable, int ColorAttr, int ColorRes) i) =>
        i.ColorAttr != 0 ? Motion.Attr(c, i.ColorAttr) : c.GetColor(i.ColorRes);
}

/// <summary>Things that leave the app: open a file, show the folder, copy an address.</summary>
internal static class Outside
{
    /// <summary>Opens a finished file with the system's choice of app, through the FileProvider. False when there is none or the file is gone.</summary>
    public static bool OpenFile(Context c, string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(c, Notifications.FileProviderAuthority, new Java.IO.File(path));
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) ?? "*/*";
            var view = new Intent(Intent.ActionView);
            view.SetDataAndType(uri, mime);
            view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            c.StartActivity(view);
            return true;
        }
        catch (Exception e) when (e is Android.Content.ActivityNotFoundException or Java.Lang.IllegalArgumentException)
        {
            return false;
        }
    }

    /// <summary>Shows the Download folder in the Files app (the system's document viewer). False when no app handles it.</summary>
    public static bool ShowDownloadFolder(Context c)
    {
        try
        {
            var i = new Intent(Intent.ActionView);
            i.SetDataAndType(Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary%3ADownload"), "vnd.android.document/directory");
            i.AddFlags(ActivityFlags.NewTask);
            c.StartActivity(i);
            return true;
        }
        catch (Android.Content.ActivityNotFoundException) { return false; }
    }

    public static void CopyText(Context c, string label, string text)
    {
        var cm = (ClipboardManager)c.GetSystemService(Context.ClipboardService)!;
        cm.PrimaryClip = ClipData.NewPlainText(label, text);
    }

    public static string? ClipboardText(Context c)
    {
        var cm = (ClipboardManager)c.GetSystemService(Context.ClipboardService)!;
        return cm.PrimaryClip is { ItemCount: > 0 } clip ? clip.GetItemAt(0)?.CoerceToText(c)?.ToString() : null;
    }
}

/// <summary>Delete with the file choice, as the desktop asks: remove the download only, or the file as well.</summary>
internal static class DeleteFlow
{
    /// <param name="canDeleteFiles">False for a host that can't delete its files; then only the list entry goes.</param>
    public static void Confirm(Context c, IReadOnlyList<JobRow> rows, bool canDeleteFiles, Action<bool> remove)
    {
        var plan = DownloadList.PlanDelete(rows);
        var b = new MaterialAlertDialogBuilder(c).SetTitle(plan.Title)!;
        if (!plan.HasFiles || !canDeleteFiles)
        {
            b.SetMessage(plan.HasFiles ? "Only the list entry goes; the files stay where they are." : plan.Message)!.SetPositiveButton("Delete", (_, _) => remove(false))!.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        }
        else
        {
            var choice = 0;
            b.SetSingleChoiceItems([DeletePlan.KeepFiles, plan.DeleteFilesLabel], 0, (_, e) => choice = e.Which)!
                .SetPositiveButton("Delete", (_, _) => remove(choice == 1))!
                .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        }

        b.Show();
    }
}

/// <summary>The speed graph of the summary card: the last minute of the total rate as a line over a soft fill, scaled to its own maximum.</summary>
internal sealed class SpeedGraphView : View
{
    private readonly Paint _line = new(PaintFlags.AntiAlias) { StrokeWidth = 2.5f * 2.6f, StrokeJoin = Paint.Join.Round };
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Android.Graphics.Path _path = new();
    private readonly Android.Graphics.Path _area = new();
    private IReadOnlyList<float> _data = Array.Empty<float>();

    public SpeedGraphView(Context c) : base(c)
    {
        _line.SetStyle(Paint.Style.Stroke);
        _fill.SetStyle(Paint.Style.Fill);
        var color = Motion.Attr(c, Resource.Attribute.colorPrimary);
        _line.Color = new Color(color);
        _fill.Color = new Color(Color.GetRedComponent(color), Color.GetGreenComponent(color), Color.GetBlueComponent(color), 70);
        _line.StrokeWidth = Motion.Dp(c, 2f);
    }

    public void SetData(IReadOnlyList<float> data)
    {
        _data = data;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_data.Count < 2) return;
        var max = Math.Max(1f, _data.Max());
        float w = Width, h = Height, pad = _line.StrokeWidth;
        var step = w / (DownloadList.MaxHistory - 1);
        // Grows from the right edge, so the newest sample is always at the edge and the line slides left as it fills.
        var x0 = w - step * (_data.Count - 1);
        _path.Reset();
        _area.Reset();
        for (var i = 0; i < _data.Count; i++)
        {
            var x = x0 + step * i;
            var y = h - pad - (h - 2 * pad) * (_data[i] / max) * 0.9f;
            if (i == 0) { _path.MoveTo(x, y); _area.MoveTo(x, h); _area.LineTo(x, y); }
            else { _path.LineTo(x, y); _area.LineTo(x, y); }
        }

        _area.LineTo(w, h);
        _area.Close();
        canvas.DrawPath(_area, _fill);
        canvas.DrawPath(_path, _line);
    }
}
