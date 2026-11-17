using Android.Content;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;

namespace Inbound.App.Screens;

/// <summary>Settings, for now the appearance part of C7: Theme and Dynamic colour. The rest of the page comes with the settings step.</summary>
internal sealed class SettingsScreen
{
    private readonly MainActivity _activity;
    private readonly Appearance _appearance;
    private readonly TextView _themeText, _dynamicText;
    private readonly MaterialSwitch _switch;
    private readonly bool _dynamicAvailable;
    private bool _setting;

    public SettingsScreen(MainActivity activity, View page, Appearance appearance)
    {
        _activity = activity;
        _appearance = appearance;
        _dynamicAvailable = Google.Android.Material.Color.DynamicColors.IsDynamicColorAvailable;
        InsetsListener.Pad(page, top: true, bottom: false);
        _themeText = page.FindViewById<TextView>(Resource.Id.theme_text)!;
        _dynamicText = page.FindViewById<TextView>(Resource.Id.dynamic_text)!;
        _switch = page.FindViewById<MaterialSwitch>(Resource.Id.dynamic_switch)!;
        page.FindViewById<View>(Resource.Id.setting_theme)!.Click += (_, _) => PickTheme();
        var row = page.FindViewById<View>(Resource.Id.setting_dynamic)!;
        _switch.Enabled = _dynamicAvailable;
        row.Click += (_, _) => { if (_dynamicAvailable) _switch.Toggle(); };
        _switch.CheckedChange += (_, e) =>
        {
            if (_setting || !_dynamicAvailable) return;
            _appearance.DynamicColour = e.IsChecked;
            // The colours of the screens are fixed when they are built: build them again with the new roles.
            _activity.Recreate();
        };
        Refresh();
    }

    public void Refresh()
    {
        _themeText.Text = _appearance.ThemeText;
        _dynamicText.Text = _appearance.DynamicText(_dynamicAvailable);
        _setting = true;
        _switch.Checked = _appearance.DynamicColour && _dynamicAvailable;
        _setting = false;
    }

    private void PickTheme()
    {
        string[] names = ["Follows the system", "Light", "Dark"];
        new MaterialAlertDialogBuilder(_activity).SetTitle("Theme")!
            .SetSingleChoiceItems(names, (int)_appearance.Theme, (d, e) =>
            {
                _appearance.Theme = (ThemeChoice)e.Which;
                ((Android.App.Dialog)d!).Dismiss();
                Refresh();
                ThemeSupport.ApplyNightMode(_appearance);
            })!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!.Show();
    }
}

internal static class ThemeSupport
{
    /// <summary>The theme choice as the night mode of this app (the system rebuilds the screen when it changes).</summary>
    public static void ApplyNightMode(Appearance a) =>
        AndroidX.AppCompat.App.AppCompatDelegate.DefaultNightMode = a.Theme switch
        {
            ThemeChoice.Light => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightNo,
            ThemeChoice.Dark => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightYes,
            _ => AndroidX.AppCompat.App.AppCompatDelegate.ModeNightFollowSystem,
        };
}
