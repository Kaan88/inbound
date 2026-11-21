namespace DownloadManager.Mobile.TorBox;

/// <summary>The words the TorBox screens show. Nothing here is a local progress figure, and the Host has already scrubbed its own words.</summary>
public static class TorBoxText
{
    public const string HostRefused = "The Host did not accept the TorBox request.";
    public const string NoKey = "Add your TorBox API key in Settings to send a magnet to TorBox.";
    public const string KeyUnreadable = "The saved TorBox key can't be read any more. Enter it again in Settings.";
    public const string NotMagnet = "Paste a magnet link (it starts with magnet:?).";
    public const string FootHint = "Get picks the files and adds them to Downloads. Tap an item for its files; long-press for Delete from TorBox.";
    public const string MissTitle = "If TorBox doesn't have it yet";
    public const string MissBody = "It fetches it first; the download waits on the list as \"On TorBox\" and starts by itself when it's ready.";
    private const string Fallback = "That could not be done.";

    /// <summary>The Host's own words when it sent some, otherwise plain words for the kind of failure.</summary>
    public static string Failure(string? error, string? failureKind = null)
    {
        if (!string.IsNullOrWhiteSpace(error)) return error.Trim();
        return failureKind switch
        {
            "authentication" => "TorBox refused the API key. Check it, then save it again.",
            "accountLimit" => "TorBox says your account doesn't allow this.",
            "accountBusy" => "TorBox says your account is at its limit for now.",
            "rateLimited" => "TorBox is asking for fewer requests. Try again in a little while.",
            "notFound" => "TorBox has no such item.",
            "rejected" => "TorBox refused this.",
            "serviceError" => "TorBox is having problems. Try again later.",
            "network" => "TorBox could not be reached. Check your connection.",
            "badResponse" => "TorBox sent an answer this app can't read.",
            "alreadyExists" => "TorBox says this is already on your account.",
            _ => Fallback,
        };
    }

    /// <summary>The words under Account on the settings page.</summary>
    public static string KeyStatus(bool hasKey, bool unreadable, bool phoneHost, string hostName) => unreadable
        ? "The saved key can't be read. Enter it again."
        : !hasKey ? "No key saved"
        : phoneHost ? "Key saved on this phone, encrypted" : "Key saved on " + hostName;

    /// <summary>The hint under "Download with" while TorBox is chosen.</summary>
    public static string TorBoxHint(bool phoneHost, string hostName) =>
        $"TorBox fetches it with your account; {(phoneHost ? "this phone" : hostName)} then downloads the files over HTTPS and uploads nothing.";
}
