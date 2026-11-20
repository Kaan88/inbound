using Android.Content.Res;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using DownloadManager.Mobile.Remote;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Dialog;

namespace Inbound.App.Hosts;

/// <summary>
/// C5 "Where downloads run": this phone and the paired Inbound hosts, the shown one ticked. Tap shows that host on every
/// screen; touch and hold a server to remove it (this phone and the server both forget the pairing); "Connect to another
/// Inbound" opens the pairing screen.
/// </summary>
[Register("app.inbound.android.HostSwitcherSheet")]
public sealed class HostSwitcherSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "hosts";

    public HostSwitcherSheet() { }

    private HostSwitcherSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    internal static void Show(AppCompatActivity activity)
    {
        if (activity.SupportFragmentManager.FindFragmentByTag(SheetTag) is not null) return;
        new HostSwitcherSheet().Show(activity.SupportFragmentManager, SheetTag);
    }

    private HostDirectory Hosts => HostChoice.Hosts(RequireContext());

    // Owner, 2026-11-17, "one-shot figures": each host is asked once when the sheet opens (a short deadline, no retry);
    // nothing is asked again, and nothing runs after the sheet closes.
    private readonly Dictionary<string, HostFigures?> _figures = new();
    private long? _phoneFreeBytes;
    private readonly Dictionary<string, (TextView Detail, View Row, HostEntry Host, bool Shown)> _rows = new();
    private CancellationTokenSource? _asking;

    public override void OnStart()
    {
        base.OnStart();
        Dialog?.Window?.SetDimAmount(0.56f);
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var v = inflater.Inflate(Resource.Layout.host_switcher, container, false)!;
        Fill(inflater, v.FindViewById<LinearLayout>(Resource.Id.host_rows)!);
        AskOnce();
        v.FindViewById<View>(Resource.Id.host_connect)!.Click += (_, _) =>
        {
            var activity = (AppCompatActivity)RequireActivity();
            Dismiss();
            PairingSheet.Show(activity);
        };
        return v;
    }

    private void Fill(LayoutInflater inflater, LinearLayout rows)
    {
        rows.RemoveAllViews();
        _rows.Clear();
        var ctx = rows.Context!;
        var current = Hosts.Current.Id;
        var now = DateTimeOffset.UtcNow;
        foreach (var host in Hosts.Hosts)
        {
            var row = inflater.Inflate(Resource.Layout.host_row, rows, false)!;
            var shown = host.Id == current;
            var nameView = row.FindViewById<TextView>(Resource.Id.host_name)!;
            nameView.Text = host.Name;
            var detail = _figures.TryGetValue(host.Id, out var known) ? SubtitleWithPhoneSpace(ctx, host, known, now) : HostWords.Subtitle(host, now);
            var detailView = row.FindViewById<TextView>(Resource.Id.host_detail)!;
            detailView.Text = detail;
            if (shown)
            {
                var onSelected = ToColor(Attr(ctx, Resource.Attribute.colorOnSecondaryContainer));
                nameView.SetTextColor(onSelected);
                detailView.SetTextColor(onSelected);
            }
            _rows[host.Id] = (detailView, row, host, shown);
            row.FindViewById<View>(Resource.Id.host_tick)!.Visibility = shown ? ViewStates.Visible : ViewStates.Invisible;
            var surface = Attr(ctx, Resource.Attribute.colorSurface);
            row.Background = Rounded(ctx, shown
                ? Attr(ctx, Resource.Attribute.colorSecondaryContainer)
                : Blend(Attr(ctx, Resource.Attribute.colorSurfaceContainerHigh), surface, .62f), 20);
            row.Foreground = RowForeground(ctx);
            var iconBg = row.FindViewById<View>(Resource.Id.host_icon_bg)!;
            iconBg.Background = Rounded(ctx, Attr(ctx, shown ? Resource.Attribute.colorPrimary : Resource.Attribute.colorSurfaceVariant), 20);
            var icon = row.FindViewById<ImageView>(Resource.Id.host_icon)!;
            icon.SetImageResource(HostBar.IconFor(host));
            icon.ImageTintList = ColorStateList.ValueOf(new Android.Graphics.Color(Attr(ctx, shown ? Resource.Attribute.colorOnPrimary : Resource.Attribute.colorOnSurfaceVariant)));
            row.ContentDescription = $"{host.Name}, {detail}" + (shown ? ", shown now" : "") + (host.IsPhone ? "" : ". Touch and hold to remove");
            var id = host.Id;
            row.Click += (_, _) =>
            {
                var screen = Activity as MainActivity;
                Dismiss();
                // Every screen built on the old host is recreated on this one (HostBar). The shown host whose connection gave
                // up is built again, so it tries again (finding 7).
                if (shown && screen is { ConnectionStopped: true }) screen.Recreate();
                else Hosts.Select(id);
            };
            if (!host.IsPhone) row.LongClick += (_, _) => AskRemove(host, rows, inflater);
            rows.AddView(row);
        }
    }

    private void AskRemove(HostEntry host, LinearLayout rows, LayoutInflater inflater)
    {
        var app = RequireContext().ApplicationContext!;
        new MaterialAlertDialogBuilder(RequireContext())
            .SetTitle($"Remove {host.Name}?")!
            .SetMessage("This phone forgets it, and it forgets this phone. Its downloads stay there. To use it again, connect to it again.")!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Remove", (_, _) =>
            {
                // The list changes before the first await (a small write); telling the server goes on in the background.
                var removing = Hosts.RemoveAsync(host.Id);
                _ = Task.Run(async () =>
                {
                    if (await removing.ConfigureAwait(false)) return;
                    new Handler(Looper.MainLooper!).Post(() => Toast.MakeText(app,
                        $"Removed here. {host.Name} couldn't be reached, so remove this phone in its web UI too.", ToastLength.Long)!.Show());
                });
                if (IsAdded) Fill(inflater, rows);
            })!
            .Show();
    }

    /// <summary>Asks every host once for its figures; each row is filled as its answer comes, or says it wasn't reached.</summary>
    private void AskOnce()
    {
        _asking?.Cancel();
        var cancel = _asking = new CancellationTokenSource();
        _phoneFreeBytes = null;
        var hosts = Hosts;
        var app = RequireContext().ApplicationContext!;
        var ui = new Handler(Looper.MainLooper!);
        foreach (var host in hosts.Hosts)
        {
            _ = Task.Run(async () =>
            {
                HostFigures? figures;
                long? phoneFreeBytes = null;
                if (host.IsPhone)
                {
                    var phone = await PhoneFiguresAsync(app, cancel.Token).ConfigureAwait(false);
                    figures = phone.Figures;
                    phoneFreeBytes = phone.FreeBytes;
                }
                else
                {
                    figures = await hosts.FiguresAsync(host, cancel.Token).ConfigureAwait(false);
                }
                ui.Post(() =>
                {
                    if (cancel.IsCancellationRequested || !IsAdded) return;
                    _figures[host.Id] = figures;
                    if (host.IsPhone) _phoneFreeBytes = phoneFreeBytes;
                    if (!_rows.TryGetValue(host.Id, out var row)) return;
                    var text = SubtitleWithPhoneSpace(row.Row.Context!, row.Host, figures, DateTimeOffset.UtcNow);
                    row.Detail.Text = text;
                    row.Row.ContentDescription = $"{row.Host.Name}, {text}" + (row.Shown ? ", shown now" : "") + (row.Host.IsPhone ? "" : ". Touch and hold to remove");
                });
            });
        }
    }

    /// <summary>This phone's figures: the in-process Host's snapshot, read once on a session that is closed right after.</summary>
    private string SubtitleWithPhoneSpace(Android.Content.Context ctx, HostEntry host, HostFigures? figures, DateTimeOffset now)
    {
        var text = HostWords.WithFigures(host, figures, now);
        if (!host.IsPhone || _phoneFreeBytes is not { } bytes) return text;
        return $"{text} · {Android.Text.Format.Formatter.FormatFileSize(ctx, bytes)} free";
    }

    private static async Task<(HostFigures? Figures, long? FreeBytes)> PhoneFiguresAsync(Android.Content.Context app, CancellationToken cancellationToken)
    {
        long? freeBytes = null;
        try
        {
            var downloads = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(downloads))
            {
                var stats = new StatFs(downloads);
                if (stats.AvailableBytes >= 0) freeBytes = stats.AvailableBytes;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }

        try
        {
            var host = await InboundApplication.GetHostAsync(app).WaitAsync(HostDirectory.FiguresTimeout, cancellationToken).ConfigureAwait(false);
            HostFigures? figures = null;
            await using var session = new Inbound.App.Host.InProcessHostConnection(host.Dispatcher);
            await session.OpenAsync(m =>
            {
                if (m.Type == DownloadManager.Contracts.MessageTypes.JobsSnapshot)
                    figures = HostFigures.From(m.GetPayload<DownloadManager.Contracts.SnapshotDto>());
            }, cancellationToken).ConfigureAwait(false);
            return (figures, freeBytes);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, freeBytes);
        }
    }

    public override void OnDestroyView()
    {
        _asking?.Cancel();
        _asking = null;
        _rows.Clear();
        base.OnDestroyView();
    }

    private static GradientDrawable Rounded(Android.Content.Context ctx, int color, float radiusDp)
    {
        var d = new GradientDrawable();
        d.SetCornerRadius(HostBar.Dp(ctx, radiusDp));
        d.SetColor(color);
        return d;
    }

    private static StateListDrawable RowForeground(Android.Content.Context ctx)
    {
        var states = new StateListDrawable();
        var focus = new GradientDrawable();
        focus.SetColor(Android.Graphics.Color.Transparent);
        focus.SetCornerRadius(HostBar.Dp(ctx, 20));
        focus.SetStroke((int)HostBar.Dp(ctx, 2), ColorStateList.ValueOf(ToColor(Attr(ctx, Resource.Attribute.colorPrimary))));
        states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focus);

        var mask = Rounded(ctx, Android.Graphics.Color.White, 20);
        var rippleColor = unchecked((int)0x20000000) | (Attr(ctx, Resource.Attribute.colorOnSurface) & 0x00ffffff);
        var ripple = new RippleDrawable(ColorStateList.ValueOf(ToColor(rippleColor)), null, mask);
        states.AddState(Array.Empty<int>(), ripple);
        return states;
    }

    private static int Blend(int foreground, int background, float amount)
    {
        static int Channel(int a, int b, float t) => (int)(a * t + b * (1 - t));
        var red = Channel((foreground >> 16) & 0xff, (background >> 16) & 0xff, amount);
        var green = Channel((foreground >> 8) & 0xff, (background >> 8) & 0xff, amount);
        var blue = Channel(foreground & 0xff, background & 0xff, amount);
        return unchecked((int)0xff000000) | (red << 16) | (green << 8) | blue;
    }

    private static Android.Graphics.Color ToColor(int value) =>
        Android.Graphics.Color.Argb((value >> 24) & 0xff, (value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff);

    private static int Attr(Android.Content.Context ctx, int attr)
    {
        var tv = new Android.Util.TypedValue();
        ctx.Theme!.ResolveAttribute(attr, tv, true);
        return tv.Data;
    }
}
