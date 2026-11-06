namespace DownloadManager.Providers.TorBox;

/// <summary>
/// Where the API calls go, which is also where the key goes. A sandbox profile may point this at a
/// mock on this computer. Nothing else is accepted: a mistaken or planted setting must not be able
/// to send the key anywhere but this computer or the real provider.
/// </summary>
public static class TorBoxApiAddress
{
    /// <param name="value">The override, as configured.</param>
    /// <param name="sandboxProfile">Whether this process runs in an isolated test profile. Without one, no override counts.</param>
    /// <param name="problem">Why a value that was set is being ignored. Doesn't quote it.</param>
    /// <returns>The address to use, or null for the default.</returns>
    public static Uri? FromOverride(string? value, bool sandboxProfile, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!sandboxProfile)
        {
            problem = "it only counts in a sandbox profile";
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
        {
            problem = "it isn't a plain http or https address";
            return null;
        }

        var realProvider = uri.Scheme == "https" &&
            uri.Host.Equals(TorBoxClient.DefaultBaseAddress.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort;
        if (!uri.IsLoopback && !realProvider)
        {
            problem = "it must be an address on this computer or the provider's own";
            return null;
        }

        // Paths are added to this, so without the slash the last segment would be lost.
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
