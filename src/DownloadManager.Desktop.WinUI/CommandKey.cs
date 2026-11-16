namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// MAC.6: Ctrl shortcuts as ⌘ on a Mac: the text a menu shows says ⌘ (Shift ⇧). The keys themselves are in
/// MainWindow's <c>Shortcuts</c>. No WinUI types here, so the tests can link it.
/// </summary>
internal static class CommandKey
{

    /// <summary>"Ctrl+Shift+O" as "⇧⌘O" on a Mac, the order Mac menus use.</summary>
    public static string Text(string windowsText) => OperatingSystem.IsMacOS() ? MacText(windowsText) : windowsText;

    public static string MacText(string windowsText)
    {
        var parts = windowsText.Split('+');
        if (parts.Length < 2) return windowsText;
        var modifiers = parts[..^1];
        var mac = string.Empty;
        if (modifiers.Contains("Shift")) mac += "⇧";
        if (modifiers.Contains("Alt")) mac += "⌥";
        if (modifiers.Contains("Ctrl")) mac += "⌘";
        return mac + parts[^1];
    }
}
