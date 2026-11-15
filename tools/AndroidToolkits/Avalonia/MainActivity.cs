using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace AndroidTrial.AvaloniaTk;

[Activity(Name = "app.inbound.tk.avalonia.MainActivity", Label = "Inbound (Avalonia)", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity
{
    public static ScreenOptions Options = new(0, false, 3, "http://127.0.0.1:8765", 0);
    public static Activity? Current;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        Options = ScreenOptions.From(Intent);
        Current = this;
        base.OnCreate(savedInstanceState);
    }

}

[Android.App.Application]
public class AndroidApp(IntPtr handle, Android.Runtime.JniHandleOwnership ownership) : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}
