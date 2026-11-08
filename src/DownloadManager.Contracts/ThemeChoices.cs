namespace DownloadManager.Contracts;

/// <summary>
/// The settings' Theme value, which every window follows (UI.R23; owner, 2026-11-08: "Make it the source").
/// System follows Windows' app theme; Light and Dark hold whatever Windows uses.
/// </summary>
public static class ThemeChoices
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static IReadOnlyList<string> All { get; } = [System, Light, Dark];

    /// <summary>One of <see cref="All"/>; anything else, or nothing, is System.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => Light,
        "dark" => Dark,
        _ => System,
    };

    /// <summary>The theme to migrate to from the window's old View > Dark Mode value, or null when it was never
    /// set, so the window already followed Windows and System keeps it so.</summary>
    public static string? FromDarkMode(bool? darkMode) => darkMode switch
    {
        true => Dark,
        false => Light,
        null => null,
    };

    /// <summary>The theme the windows use: a View > Dark Mode choice still kept (not yet saved to the Host) wins over
    /// the Host's <paramref name="setting"/>, as FollowThemeSetting does. Options starts its Theme box from this, so
    /// OK saves the kept choice instead of undoing it.</summary>
    public static string InUse(bool? keptDarkMode, string? setting) => Normalize(FromDarkMode(keptDarkMode) ?? setting);

    /// <summary>The kept View > Dark Mode value for <paramref name="theme"/>: true for Dark, false for Light, null for
    /// System, which the menu toggle never sets.</summary>
    public static bool? ToDarkMode(string? theme) => Normalize(theme) switch
    {
        Dark => true,
        Light => false,
        _ => null,
    };

    /// <summary>Whether a window is dark: <paramref name="theme"/> itself, or Windows' app theme for System.</summary>
    public static bool IsDark(string? theme, bool windowsUsesDark) => Normalize(theme) switch
    {
        Dark => true,
        Light => false,
        _ => windowsUsesDark,
    };
}
