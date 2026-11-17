using Android.Content.Res;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Remote;
using Google.Android.Material.Chip;

namespace Inbound.App.Hosts;

/// <summary>
/// ANDROID.7, C5 and C6 on a screen: the host chip (the shown host's name; tap opens "Where downloads run") and, while
/// another Inbound is shown, the strip that says where files land, or why that host can't be reached. A switch of host
/// recreates the activity, so the screen is built again on the new host's connection.
/// <para>
/// Step 6 puts the chip at the end of the toolbar. Step 3's search bar is where C5 and C6 draw it: after the merge, call
/// <see cref="CreateChip"/> into that bar and <see cref="CreateStrip"/> under it instead of <see cref="Install"/>.
/// </para>
/// </summary>
internal static class HostBar
{
    /// <summary>Adds the chip to <paramref name="toolbar"/> and the strip to the bottom of <paramref name="appbar"/>.</summary>
    public static void Install(AppCompatActivity activity, AndroidX.AppCompat.Widget.Toolbar toolbar, ViewGroup appbar, IHostConnection connection)
    {
        toolbar.AddView(CreateChip(activity), new AndroidX.AppCompat.Widget.Toolbar.LayoutParams(ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent, (int)GravityFlags.End) { MarginEnd = Dp(activity, 8) });
        if (CreateStrip(activity, connection) is { } strip) appbar.AddView(strip);
        RecreateOnSwitch(activity);
    }

    /// <summary>The chip with the shown host's icon and name and a drop-down mark; tap opens the switcher (C5).</summary>
    public static Chip CreateChip(AppCompatActivity activity)
    {
        var host = HostChoice.Hosts(activity).Current;
        var chip = new Chip(activity)
        {
            Text = host.Name,
            ChipIcon = activity.GetDrawable(IconFor(host)),
            ChipIconVisible = true,
            CloseIcon = activity.GetDrawable(Resource.Drawable.ic_expand_more),
            CloseIconVisible = true,
            ContentDescription = $"Downloads shown from {host.Name}. Change host",
        };
        chip.SetEnsureMinTouchTargetSize(true);
        chip.Click += (_, _) => HostSwitcherSheet.Show(activity);
        chip.SetOnCloseIconClickListener(new ClickListener(() => HostSwitcherSheet.Show(activity)));
        return chip;
    }

    /// <summary>
    /// The strip of C6 for a remote host ("Files are saved on Home server"); while it can't be reached, the reason. Null for
    /// this phone. Tapping it opens the switcher, where a host whose pairing ended can be removed and connected again.
    /// </summary>
    public static View? CreateStrip(AppCompatActivity activity, IHostConnection connection)
    {
        if (connection is not IRemoteStatus remote) return null;
        var fg = activity.GetColor(Resource.Color.host_strip_fg);
        var strip = new LinearLayout(activity) { Orientation = Android.Widget.Orientation.Horizontal, Clickable = true, Focusable = true };
        strip.SetGravity(GravityFlags.CenterVertical);
        strip.SetPadding(Dp(activity, 16), Dp(activity, 12), Dp(activity, 16), Dp(activity, 12));
        strip.Background = new GradientDrawable();
        ((GradientDrawable)strip.Background).SetCornerRadius(Dp(activity, 16));
        ((GradientDrawable)strip.Background).SetColor(activity.GetColor(Resource.Color.host_strip_bg));
        var icon = new ImageView(activity) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(Resource.Drawable.ic_host_server);
        icon.ImageTintList = ColorStateList.ValueOf(new Android.Graphics.Color(fg));
        strip.AddView(icon, new LinearLayout.LayoutParams(Dp(activity, 20), Dp(activity, 20)));
        var text = new TextView(activity) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        text.SetTextColor(new Android.Graphics.Color(fg));
        text.SetTextSize(ComplexUnitType.Sp, 15);
        strip.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginStart = Dp(activity, 12) });
        strip.LayoutParameters = new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = Dp(activity, 16), RightMargin = Dp(activity, 16), TopMargin = Dp(activity, 4), BottomMargin = Dp(activity, 8),
        };
        strip.Click += (_, _) => HostSwitcherSheet.Show(activity);

        void Show(RemoteConnectionState state)
        {
            text.Text = Words(remote.HostName, state);
        }

        Show(remote.State);
        Action<RemoteConnectionState> changed = state => activity.RunOnUiThread(() =>
        {
            if (!activity.IsDestroyed) Show(state);
        });
        remote.StateChanged += changed;
        OnDestroyed(activity, () => remote.StateChanged -= changed);
        return strip;
    }

    /// <summary>What the strip says: where files land while it works, and why not otherwise.</summary>
    internal static string Words(string name, RemoteConnectionState state) => state.Status switch
    {
        RemoteConnectionStatus.Connecting when state.Message is null => $"Connecting to {name}…",
        RemoteConnectionStatus.Retrying or RemoteConnectionStatus.Stopped when state.Message is not null => state.Message,
        _ => HostWords.SavedOn(name),
    };

    /// <summary>A different host chosen (here or in another screen) builds this activity again on that host.</summary>
    private static void RecreateOnSwitch(AppCompatActivity activity)
    {
        var hosts = HostChoice.Hosts(activity);
        var shown = hosts.Current.Id;
        Action changed = () => activity.RunOnUiThread(() =>
        {
            if (activity.IsFinishing || activity.IsDestroyed || hosts.Current.Id == shown) return;
            activity.Recreate();
        });
        hosts.Changed += changed;
        OnDestroyed(activity, () => hosts.Changed -= changed);
    }

    internal static int IconFor(HostEntry host) =>
        host.IsPhone ? Resource.Drawable.ic_host_phone
        : host.HostKind == DownloadManager.Contracts.HostKinds.Windows ? Resource.Drawable.ic_host_desktop
        : Resource.Drawable.ic_host_server;

    internal static int Dp(Android.Content.Context context, float dp) =>
        (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

    private static void OnDestroyed(AppCompatActivity activity, Action run) =>
        activity.Lifecycle.AddObserver(new DestroyObserver(run));

    private sealed class DestroyObserver(Action run) : Java.Lang.Object, AndroidX.Lifecycle.IDefaultLifecycleObserver
    {
        public void OnDestroy(AndroidX.Lifecycle.ILifecycleOwner owner) => run();
        public void OnCreate(AndroidX.Lifecycle.ILifecycleOwner owner) { }
        public void OnPause(AndroidX.Lifecycle.ILifecycleOwner owner) { }
        public void OnResume(AndroidX.Lifecycle.ILifecycleOwner owner) { }
        public void OnStart(AndroidX.Lifecycle.ILifecycleOwner owner) { }
        public void OnStop(AndroidX.Lifecycle.ILifecycleOwner owner) { }
    }

    private sealed class ClickListener(Action run) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => run();
    }
}
