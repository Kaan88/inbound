namespace DownloadManager.Core;

/// <summary>
/// FTP downloading was cut in 0.7.2. An ftp:// (or ftps://) address is refused where it is added, with
/// <see cref="NotSupported"/>, and an FTP download an older version left in the list fails with it when it would start.
/// Also compiled into the desktop window (WinUI and Uno), which doesn't reference Core, so it uses nothing else of Core.
/// </summary>
public static class FtpAddresses
{
    public const string NotSupported = "FTP addresses aren't supported.";

    public static bool IsFtp(string? address)
        => Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && IsFtp(uri);

    public static bool IsFtp(Uri uri) => uri.Scheme is "ftp" or "ftps";

    /// <summary>
    /// An FTP address without the password an older version kept in it (Review 1 L11); any other address, or one with no
    /// password, as it is. The user name stays, so the list still says whose download it was.
    /// </summary>
    public static string? WithoutPassword(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || !IsFtp(uri) ||
            !uri.UserInfo.Contains(':', StringComparison.Ordinal))
            return address;
        return new UriBuilder(uri) { Password = string.Empty }.Uri.AbsoluteUri;
    }
}
