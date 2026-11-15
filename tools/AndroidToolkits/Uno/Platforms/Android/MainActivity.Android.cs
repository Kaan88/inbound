using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Android.Views;

namespace AndroidTrial.UnoTk.Droid;

[Activity(Name = "app.inbound.tk.uno.MainActivity", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop, Theme = "@style/AppTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustPan | SoftInput.StateHidden)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
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

[Android.App.Application(Label = "Inbound (Uno)", LargeHeap = true, HardwareAccelerated = true, Theme = "@style/AppTheme")]
public class Application : Microsoft.UI.Xaml.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer) : base(() => new App(), javaReference, transfer)
    {
    }
}
