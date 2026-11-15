using Android.App;
using Google.Android.Material.Color;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;

namespace AndroidTrial.MauiTk;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Unspecified;
        Apply();
        RequestedThemeChanged += (_, _) => Apply();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell());

    /// <summary>The app's own schemes (webui tokens), or, with dynamic=true, the roles the wallpaper gives the activity's theme.</summary>
    public void Apply()
    {
        var dark = RequestedTheme == AppTheme.Dark;
        var r = Resources;
        void S(string k, string hex) => r[k] = Color.FromArgb(hex);
        if (dark)
        {
            S("Bg", "#141414"); S("Card", "#1C1C1C"); S("Text", "#F2F2F2"); S("Muted", "#A3A3A3"); S("Accent", "#4AA3F0"); S("OnAccent", "#0E1B2A");
            S("Track", "#383838"); S("Done", "#5DC879"); S("Failed", "#F58B8B"); S("Waiting", "#E0A93E"); S("Container", "#2B3A4F");
        }
        else
        {
            S("Bg", "#F3F3F3"); S("Card", "#FFFFFF"); S("Text", "#1A1A1A"); S("Muted", "#666666"); S("Accent", "#1F6FC5"); S("OnAccent", "#FFFFFF");
            S("Track", "#E2E2E2"); S("Done", "#1B7130"); S("Failed", "#B3261E"); S("Waiting", "#8A5A00"); S("Container", "#DCE9F7");
        }

        if (Dynamic.Enabled && Platform.CurrentActivity is { } act)
        {
            // Material's DynamicColors was applied to the activity theme (MainActivity); MAUI controls do not read that theme, so each role is copied by hand.
            Color C(int attr) { var tv = new Android.Util.TypedValue(); act.Theme!.ResolveAttribute(attr, tv, true); return Color.FromInt(tv.Data); }
            r["Bg"] = C(Resource.Attribute.colorSurface); r["Card"] = C(Resource.Attribute.colorSurfaceContainer); r["Text"] = C(Resource.Attribute.colorOnSurface);
            r["Muted"] = C(Resource.Attribute.colorOnSurfaceVariant); r["Accent"] = C(Resource.Attribute.colorPrimary); r["OnAccent"] = C(Resource.Attribute.colorOnPrimary);
            r["Container"] = C(Resource.Attribute.colorSecondaryContainer); r["Track"] = C(Resource.Attribute.colorSurfaceVariant);
        }
    }
}

internal static class Dynamic { public static bool Enabled; }
