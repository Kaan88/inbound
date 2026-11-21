namespace DownloadManager.Mobile.TorBox;

/// <summary>Which "Download with" segment Add torrent opens with, and the per-device switch that decides it (Settings, "Send magnets to TorBox first").</summary>
public sealed class TorBoxChoice(IKeyValueStore store)
{
    private const string Key = "torbox_first";

    public bool SendFirst
    {
        get => store.GetString(Key) == "on";
        set => store.SetString(Key, value ? "on" : "off");
    }

    /// <summary>
    /// True when Add torrent opens with TorBox chosen. Torrents on the phone are not there yet, so TorBox is chosen whenever it is
    /// offered; the switch matters once both are.
    /// </summary>
    public bool OpensWithTorBox(bool torBoxOffered, bool phoneTorrentsOffered) =>
        torBoxOffered && (SendFirst || !phoneTorrentsOffered);
}
