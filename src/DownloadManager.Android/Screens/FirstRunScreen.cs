using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.Button;

namespace Inbound.App.Screens;

/// <summary>
/// First run (A0): where downloads go, and the two permissions worth asking for. "Start downloading" is always available;
/// "Connect to another Inbound" opens the pairing sheet instead.
/// </summary>
internal sealed class FirstRunScreen
{
    private readonly MainActivity _activity;
    private readonly string _folder;
    private readonly MaterialButton _notif, _batt;
    private readonly ImageView _notifIcon, _battIcon;
    private readonly View _notifTile, _battTile;

    public FirstRunScreen(MainActivity activity, string folder, Action connect, Action start)
    {
        _activity = activity;
        _folder = folder;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.first_run, null, false)!;
        InsetsListener.Pad(Root, top: true, bottom: true);
        _notif = Root.FindViewById<MaterialButton>(Resource.Id.fr_notif_allow)!;
        _batt = Root.FindViewById<MaterialButton>(Resource.Id.fr_batt_allow)!;
        _notifIcon = Root.FindViewById<ImageView>(Resource.Id.fr_notif_icon)!;
        _battIcon = Root.FindViewById<ImageView>(Resource.Id.fr_batt_icon)!;
        _notifTile = Root.FindViewById<View>(Resource.Id.fr_notif_tile)!;
        _battTile = Root.FindViewById<View>(Resource.Id.fr_batt_tile)!;
        _notif.Click += (_, _) => AskNotifications();
        _batt.Click += (_, _) => AskBattery();
        Root.FindViewById<View>(Resource.Id.fr_start)!.Click += (_, _) => start();
        Root.FindViewById<View>(Resource.Id.fr_connect)!.Click += (_, _) => connect();
        Refresh();
    }

    public View Root { get; }

    private Permission NotificationState() =>
        !OperatingSystem.IsAndroidVersionAtLeast(33) || _activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Android.Content.PM.Permission.Granted
            ? Permission.Granted : Permission.NotGranted;

    private bool BatteryUnrestricted() =>
        ((PowerManager)_activity.GetSystemService(Context.PowerService)!).IsIgnoringBatteryOptimizations(_activity.PackageName);

    /// <summary>Called when the screen is shown and again when it is back from a permission dialog.</summary>
    public void Refresh()
    {
        var state = new FirstRunState(_folder, NotificationState(), BatteryUnrestricted());
        Root.FindViewById<TextView>(Resource.Id.fr_folder)!.Text = state.FolderLine;
        Mark(_notif, _notifTile, _notifIcon, state.NotificationsOn, Resource.Drawable.ic_notifications);
        Mark(_batt, _battTile, _battIcon, state.BatteryUnrestricted, Resource.Drawable.ic_battery);
    }

    // A permission that is on has no button: the line is ticked instead.
    private void Mark(MaterialButton button, View tile, ImageView icon, bool on, int normalIcon)
    {
        button.Visibility = on ? ViewStates.Gone : ViewStates.Visible;
        tile.SetBackgroundResource(on ? Resource.Drawable.bg_circle_done : Resource.Drawable.bg_circle_dark);
        icon.SetImageResource(on ? Resource.Drawable.ic_check : normalIcon);
        icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(on ? _activity.GetColor(Resource.Color.state_done) : Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
    }

    private const string AskedKey = "notifications_asked";

    private void AskNotifications()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        const string permission = "android.permission.POST_NOTIFICATIONS";
        var prefs = new PrefsStore(_activity);
        // Once the system stops showing its dialog (denied before, no rationale to show), asking again does nothing: the app's
        // notification settings are opened instead, where they can still be turned on (finding 19).
        if (prefs.GetBool(AskedKey) && !_activity.ShouldShowRequestPermissionRationale(permission))
        {
            try
            {
                var settings = new Intent(Settings.ActionAppNotificationSettings);
                settings.PutExtra(Settings.ExtraAppPackage, _activity.PackageName);
                _activity.StartActivity(settings);
                return;
            }
            catch (ActivityNotFoundException)
            {
                // No such screen on this phone: the dialog is asked for, as the first time.
            }
        }

        prefs.SetBool(AskedKey, true);
        _activity.RequestPermissions([permission], 1);
    }

    private void AskBattery()
    {
        try
        {
            var i = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
            i.SetData(Android.Net.Uri.Parse("package:" + _activity.PackageName));
            _activity.StartActivity(i);
        }
        catch (ActivityNotFoundException)
        {
            _activity.StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
        }
    }
}
