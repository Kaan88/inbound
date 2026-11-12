using System.Collections.ObjectModel;
using System.ComponentModel;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T2: one peer in Progress Details' Peers tab. Public because the list's bindings read it by reflection; updated in
/// place, so a refresh every two seconds redraws only what changed.
/// </summary>
public sealed class TorrentPeerRow : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _address = string.Empty;
    private string _client = string.Empty;
    private string _flags = string.Empty;
    private string _has = string.Empty;
    private string _down = string.Empty;
    private string _up = string.Empty;
    private string _received = string.Empty;
    private string _sent = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>UI.R42: what came from this peer since it connected.</summary>
    public string Received { get => _received; private set => Set(ref _received, value, nameof(Received)); }

    /// <summary>UI.R42: what went to this peer since it connected.</summary>
    public string Sent { get => _sent; private set => Set(ref _sent, value, nameof(Sent)); }

    internal string Key { get; private set; } = string.Empty;

    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name)); }

    /// <summary>The peer's address and port, shown on hover.</summary>
    public string Address { get => _address; private set => Set(ref _address, value, nameof(Address)); }

    public string Client { get => _client; private set => Set(ref _client, value, nameof(Client)); }

    public string Flags { get => _flags; private set => Set(ref _flags, value, nameof(Flags)); }

    public string Has { get => _has; private set => Set(ref _has, value, nameof(Has)); }

    public string Down { get => _down; private set => Set(ref _down, value, nameof(Down)); }

    public string Up { get => _up; private set => Set(ref _up, value, nameof(Up)); }

    internal void Apply(string key, int number, TorrentPeerDto peer)
    {
        Key = key;
        Name = TorrentDetailsRules.PeerName(number, peer.Address);
        Address = TorrentDetailsRules.PeerAddress(peer.Address, peer.Port);
        Client = string.IsNullOrWhiteSpace(peer.Client) ? "unknown" : peer.Client.Trim();
        Flags = TorrentDetailsRules.PeerFlags(peer.Flags);
        Has = TorrentDetailsRules.Has(peer.Progress);
        Down = TorrentDetailsRules.Rate(peer.DownloadRate);
        Up = TorrentDetailsRules.Rate(peer.UploadRate);
        Received = UiFormat.Bytes(Math.Max(0, peer.Downloaded));
        Sent = UiFormat.Bytes(Math.Max(0, peer.Uploaded));
    }

    private void Set(ref string field, string value, string name)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Name}, {Client}, has {Has}, down {Down}, up {Up}";
}

/// <summary>T2: one tracker in Progress Details' Trackers tab, updated in place.</summary>
public sealed class TorrentTrackerRow : INotifyPropertyChanged
{
    private string _tracker = string.Empty;
    private string _status = string.Empty;
    private string _seeds = string.Empty;
    private string _peers = string.Empty;
    private string _message = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string Key { get; private set; } = string.Empty;

    public string Tracker { get => _tracker; private set => Set(ref _tracker, value, nameof(Tracker)); }

    public string Status { get => _status; private set => Set(ref _status, value, nameof(Status)); }

    public string Seeds { get => _seeds; private set => Set(ref _seeds, value, nameof(Seeds)); }

    public string Peers { get => _peers; private set => Set(ref _peers, value, nameof(Peers)); }

    /// <summary>What the tracker said last, shown on hover; the status when it said nothing.</summary>
    public string Message { get => _message; private set => Set(ref _message, value, nameof(Message)); }

    private string _nextAnnounce = string.Empty;
    private string _tier = string.Empty;
    private string _said = string.Empty;

    /// <summary>T3: "24 min", "now" or "—". The column shows only where the Host says it (the header's, UI.R42).</summary>
    public string NextAnnounce { get => _nextAnnounce; private set => Set(ref _nextAnnounce, value, nameof(NextAnnounce)); }

    /// <summary>UI.R42: the tracker's tier, as the Host numbers them.</summary>
    public string Tier { get => _tier; private set => Set(ref _tier, value, nameof(Tier)); }

    /// <summary>UI.R42: the Message column: what the tracker said last, without addresses; empty when it said nothing.</summary>
    public string Said { get => _said; private set => Set(ref _said, value, nameof(Said)); }

    internal void Apply(string key, TorrentTrackerDto tracker)
    {
        Key = key;
        NextAnnounce = TorrentControlRules.AnnounceIn(tracker.NextAnnounce);
        Tracker = TorrentDetailsRules.TrackerShown(tracker.Url);
        Status = TorrentDetailsRules.TrackerStatus(tracker.Status);
        Seeds = TorrentDetailsRules.Count(tracker.Seeds);
        Peers = TorrentDetailsRules.Count(tracker.Peers);
        Tier = Math.Max(0, tracker.Tier).ToString(System.Globalization.CultureInfo.CurrentCulture);
        // A tracker's own words can hold its address; addresses are taken out.
        Said = string.IsNullOrWhiteSpace(tracker.Message) ? string.Empty : RemoteText.Failure(tracker.Message);
        Message = Said.Length == 0 ? Status : Said;
    }

    private void Set(ref string field, string value, string name)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString() => $"{Tracker}, {Status}, seeds {Seeds}, peers {Peers}";
}

/// <summary>Keeps a shown list's rows in step with a new reading: the same key at the same place is updated in place.</summary>
internal static class DetailsRowSync
{
    public static void Sync<TRow, TItem>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> items, Func<TItem, string> key,
        Func<TRow, string> rowKey, Func<TRow> make, Action<TRow, TItem, int> apply)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index < rows.Count && rowKey(rows[index]) == key(item))
            {
                apply(rows[index], item, index);
                continue;
            }

            var row = make();
            apply(row, item, index);
            if (index < rows.Count) rows[index] = row;
            else rows.Add(row);
        }

        while (rows.Count > items.Count) rows.RemoveAt(rows.Count - 1);
    }
}
