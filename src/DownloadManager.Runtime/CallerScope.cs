using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// What one caller may reach, for a caller that is not this machine's user and shares the service with one who is: a
/// browser on another device on the desktop Host (D8). Passed with each call, because the service is shared. Null
/// (the user at this PC, or the server, whose limits are the service's own <see cref="DownloadManagerService.Roots"/>)
/// changes nothing.
/// <list type="bullet">
/// <item>Folders: what it adds, moves or renames stays in <see cref="Roots"/>, every path is checked as text first
/// (<see cref="PathRoots.DescribeUnsafeText"/>), and a download whose files lie outside them is read-only for it.</item>
/// <item>Addresses: nothing it adds or points a download at reaches this PC or its local network
/// (<see cref="DownloadJob.PublicNetworkOnly"/>), and FTP isn't offered (its data connection goes wherever the server says).</item>
/// </list>
/// </summary>
public sealed class CallerScope(PathRoots roots)
{
    /// <summary>Owner names of downloads such a caller added start with this, so they can't match a local client's.</summary>
    public const string OwnerPrefix = "web:";

    public PathRoots Roots { get; } = roots;

    public const string LocalAddressRefused =
        "Downloads added from another device can't use addresses on this PC or its local network.";

    public const string FtpRefused = "FTP downloads can only be added in the app on this PC.";

    public const string OutsideJobRefused =
        "This download's file is outside the app's folders, so it can't be changed from another device.";

    public const string CompletionActionsKeepAddress =
        "This download opens its file or runs a program when it is done, so its address can only be changed in the app on this PC.";

    /// <summary>Why this address can't be used by such a caller, or null. Text only: the connection checks the rest.</summary>
    public static string? DescribeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)) return null;
        if (string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase)) return FtpRefused;
        return NetworkAddresses.IsLocalTarget(uri) ? LocalAddressRefused : null;
    }

    /// <summary>Whether every path the job has is inside the roots. Touches the file system (links are resolved).</summary>
    public bool Holds(string destination, string finalPath, string partPath)
        => !string.IsNullOrWhiteSpace(destination) && Roots.Allowed(destination) is not null &&
           (string.IsNullOrWhiteSpace(finalPath) || Roots.Allowed(finalPath) is not null) &&
           (string.IsNullOrWhiteSpace(partPath) || Roots.Allowed(partPath) is not null);
}
