using Android.App;
using Android.Content.PM;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidTrial.MauiTk;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<App>().Build();
}

[Application]
public class MainApplication(IntPtr handle, Android.Runtime.JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

[Activity(Name = "app.inbound.tk.maui.MainActivity", Label = "Inbound (MAUI)", Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public static ScreenOptions Options = new(0, false, 3, "http://127.0.0.1:8765", 0);

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        Options = ScreenOptions.From(Intent);
        if (Options.Dynamic) { Google.Android.Material.Color.DynamicColors.ApplyToActivityIfAvailable(this); Dynamic.Enabled = true; }
        base.OnCreate(savedInstanceState);
    }
}
