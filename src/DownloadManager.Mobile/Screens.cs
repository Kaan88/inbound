namespace DownloadManager.Mobile;

/// <summary>Whether the app may post notifications.</summary>
public enum Permission { Granted, NotGranted }

/// <summary>
/// The first-run screen (A0): what the phone is set up for, and the two permissions that matter. Nothing blocks: "Start downloading"
/// is always there, because the downloader works without either (notifications are a courtesy, battery matters for long downloads).
/// </summary>
public sealed class FirstRunState(string folder, Permission notifications, bool batteryUnrestricted)
{
    /// <summary>The folder downloads go to, as the line shows it: its last name ("Download").</summary>
    public string FolderLine { get; } = DetailsInfo.FolderName(folder);

    public bool NotificationsOn { get; } = notifications == Permission.Granted;

    public bool BatteryUnrestricted { get; } = batteryUnrestricted;

    /// <summary>The first-run screen is shown once; after "Start downloading" it does not come back.</summary>
    public static bool ShouldShow(bool doneBefore) => !doneBefore;
}

/// <summary>The app's theme: follow the system or force a side.</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>Somewhere to keep a small value for the app (SharedPreferences on the phone).</summary>
public interface IKeyValueStore
{
    string? GetString(string key);

    void SetString(string key, string value);
}

/// <summary>The two appearance settings: the theme, and dynamic (wallpaper) colour, which is off unless the person turns it on.</summary>
public sealed class Appearance(IKeyValueStore store)
{
    private const string ThemeKey = "theme";
    private const string DynamicKey = "dynamic_colour";

    public ThemeChoice Theme
    {
        get => Enum.TryParse<ThemeChoice>(store.GetString(ThemeKey), out var t) && Enum.IsDefined(t) ? t : ThemeChoice.System;
        set => store.SetString(ThemeKey, value.ToString());
    }

    public bool DynamicColour
    {
        get => store.GetString(DynamicKey) == "on";
        set => store.SetString(DynamicKey, value ? "on" : "off");
    }

    /// <summary>The words under "Theme" on the settings page.</summary>
    public string ThemeText => Theme switch { ThemeChoice.Light => "Light", ThemeChoice.Dark => "Dark", _ => "Follows the system" };

    /// <summary>The words under "Dynamic colour".</summary>
    public string DynamicText(bool available) =>
        !available ? "Needs Android 12 or newer" : DynamicColour ? "On: colours from your wallpaper" : "Off: Inbound blue";
}

/// <summary>What the back gesture does now. The app steps in only when it has something to undo; otherwise the system leaves the app (and shows its own preview).</summary>
public enum BackStep { System, EndSelection, CloseSearch, ClosePage, BackToDownloads }

public static class BackPlan
{
    /// <param name="selecting">Rows are selected.</param>
    /// <param name="searching">The search field has text or focus.</param>
    /// <param name="pageOpen">A page (Details, New download) is open over Downloads.</param>
    /// <param name="onSettings">The Settings tab is showing.</param>
    public static BackStep Next(bool pageOpen, bool selecting, bool searching, bool onSettings) =>
        pageOpen ? BackStep.ClosePage
        : selecting ? BackStep.EndSelection
        : searching ? BackStep.CloseSearch
        : onSettings ? BackStep.BackToDownloads
        : BackStep.System;
}

/// <summary>The first address on the clipboard worth offering in the Add sheet.</summary>
public static class ClipboardOffer
{
    /// <summary>The link when the text is one http(s) address (or the first line of several); null otherwise. Only offered, never added by itself.</summary>
    public static string? Find(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText)) return null;
        var first = clipboardText.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && Uri.TryCreate(first, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host) ? first : null;
    }

    /// <summary>"releases.example.org/.../ubuntu.iso": the line under "Link on the clipboard", short enough for one line.</summary>
    public static string Shorten(string url, int max = 38)
    {
        var text = url.Replace("https://", "").Replace("http://", "");
        if (text.Length <= max) return text;
        var first = text.IndexOf('/');
        var last = text.LastIndexOf('/');
        if (first < 0 || last <= first) return text[..(max - 3)] + "...";
        var shortened = text[..first] + "/..." + text[last..];
        return shortened.Length <= max ? shortened : text[..(max - 3)] + "...";
    }
}
