using Android.Content;

namespace AndroidTrial;

/// <summary>The colour roles the Downloads screen uses, as #RRGGBB. Own = webui/src/tokens.css; Dynamic = Android 12+ wallpaper palette (system_accent/neutral resources) mapped like Material 3 does.</summary>
public sealed record Scheme(string Bg, string Card, string Text, string Muted, string Accent, string OnAccent, string Track, string Done, string Failed, string Waiting, string Container)
{
    public static Scheme Own(bool dark) => dark
        ? new("#141414", "#1C1C1C", "#F2F2F2", "#A3A3A3", "#4AA3F0", "#0E1B2A", "#383838", "#5DC879", "#F58B8B", "#E0A93E", "#2B3A4F")
        : new("#F3F3F3", "#FFFFFF", "#1A1A1A", "#666666", "#1F6FC5", "#FFFFFF", "#E2E2E2", "#1B7130", "#B3261E", "#8A5A00", "#DCE9F7");

    public static Scheme Dynamic(Context c, bool dark)
    {
        string H(int res) { var v = c.GetColor(res); return $"#{(v >> 16) & 0xFF:X2}{(v >> 8) & 0xFF:X2}{v & 0xFF:X2}"; }
        var own = Own(dark);
        return dark
            ? own with { Bg = H(Android.Resource.Color.SystemNeutral1900), Card = H(Android.Resource.Color.SystemNeutral1800), Text = H(Android.Resource.Color.SystemNeutral1100),
                Muted = H(Android.Resource.Color.SystemNeutral2200), Accent = H(Android.Resource.Color.SystemAccent1200), OnAccent = H(Android.Resource.Color.SystemAccent1800),
                Track = H(Android.Resource.Color.SystemNeutral2700), Container = H(Android.Resource.Color.SystemAccent2700) }
            : own with { Bg = H(Android.Resource.Color.SystemNeutral110), Card = H(Android.Resource.Color.SystemNeutral150), Text = H(Android.Resource.Color.SystemNeutral1900),
                Muted = H(Android.Resource.Color.SystemNeutral2700), Accent = H(Android.Resource.Color.SystemAccent1600), OnAccent = H(Android.Resource.Color.SystemAccent10),
                Track = H(Android.Resource.Color.SystemNeutral2100), Container = H(Android.Resource.Color.SystemAccent2100) };
    }
}
