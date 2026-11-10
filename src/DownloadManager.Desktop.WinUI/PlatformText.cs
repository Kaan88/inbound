namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Words that name the operating system. The same window runs on Windows and, through Uno, on Linux (D7), where
/// "sign in to Windows" or "protected by Windows" would be wrong.
/// </summary>
internal static class PlatformText
{
    private static bool Windows => OperatingSystem.IsWindows();

    public static string StartAtSignIn => Windows ? "Start Inbound when I sign in to Windows" : "Start Inbound when I sign in";

    public static string FollowSystemTheme => Windows ? "Same as Windows" : "Same as the system";

    public static string SystemProxy => Windows ? "Use the Windows proxy settings" : "Use the system proxy settings";

    /// <summary>How saved passwords are kept: DPAPI on Windows; on Linux, Inbound's own key, kept in the keyring when there is one.</summary>
    public static string ProtectedBy => Windows ? "protected by Windows" : "kept encrypted";

    public static string ThisAccount => Windows ? "this Windows account" : "this user account";
}
