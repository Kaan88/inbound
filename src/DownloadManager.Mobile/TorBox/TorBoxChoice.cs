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
    /// True when Add torrent opens with TorBox chosen: when only TorBox can take the torrent, or when both can and the switch says TorBox
    /// first. <paramref name="phoneTorrentsOffered"/> is true when the Host has torrents and they are turned on ("Download torrents here"),
    /// so with them off (the default) TorBox is chosen, as the setting's own words say: "Off: magnets go to TorBox".
    /// </summary>
    public bool OpensWithTorBox(bool torBoxOffered, bool phoneTorrentsOffered) =>
        torBoxOffered && (SendFirst || !phoneTorrentsOffered);
}
