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
/// The chip goes at the right end of the Downloads search bar (A1, C6) and the strip under it, into the slots of
/// <c>downloads_page.xml</c>.
/// </para>
/// </summary>
internal static class HostBar
{
    /// <summary>Puts the chip into <paramref name="chipSlot"/> and, for a remote host, the strip into <paramref name="stripSlot"/>.</summary>
    public static void Place(AppCompatActivity activity, ViewGroup chipSlot, ViewGroup stripSlot, IHostConnection connection)
    {
        chipSlot.AddView(CreateChip(activity), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterVertical));
        if (CreateStrip(activity, connection) is { } strip) stripSlot.AddView(strip);
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
            // The drop-down mark is the chip's close icon, which TalkBack would otherwise read as "Remove <name>" (finding 23).
            CloseIconContentDescription = "Change host",
        };
        // A1/C6: a filled pill inside the search bar, the host's icon in the primary colour, no outline.
        chip.ChipBackgroundColor = ColorStateList.ValueOf(new Android.Graphics.Color(Inbound.App.Screens.Motion.Attr(activity, Resource.Attribute.colorSurfaceContainerHighest)));
        chip.ChipStrokeWidth = 0;
        chip.ChipMinHeight = Dp(activity, 40);
        chip.ShapeAppearanceModel = chip.ShapeAppearanceModel.ToBuilder().SetAllCornerSizes(Dp(activity, 20)).Build();
        chip.ChipIconTint = ColorStateList.ValueOf(new Android.Graphics.Color(Inbound.App.Screens.Motion.Attr(activity, Resource.Attribute.colorPrimary)));
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
            TopMargin = Dp(activity, 12), // the slot has the side margins; the summary card below keeps its own top margin
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

    /// <summary>
    /// A different host chosen (here or in another screen), or the shown server paired again (a new token, certificate or
    /// plain http choice), builds this activity again on that host's new connection (findings 3, 7).
    /// </summary>
    private static void RecreateOnSwitch(AppCompatActivity activity)
    {
        var hosts = HostChoice.Hosts(activity);
        var shown = hosts.Current.ConnectionStamp;
        Action changed = () => activity.RunOnUiThread(() =>
        {
            if (activity.IsFinishing || activity.IsDestroyed || hosts.Current.ConnectionStamp == shown) return;
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
