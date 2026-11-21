using DownloadManager.Contracts;
using DownloadManager.Mobile.TorBox;

namespace DownloadManager.Mobile.Torrents;

/// <summary>A torrent's files as the check-box tree of TorBox's (a file's id is its index in the torrent).</summary>
public static class TorrentFileTrees
{
    /// <param name="wantedOnly">Start with only the files the Host wants chosen (the Files tab); otherwise every file is chosen (Add torrent).</param>
    public static TorBoxFileTree Build(IEnumerable<TorrentFileDto> files, bool wantedOnly = false)
    {
        var list = files.ToList();
        var remote = list.Select(f => new RemoteFileDto
        {
            FileId = f.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RelativePath = f.Path, Size = Math.Max(0, f.Size), Selectable = true,
        }).ToList();
        var chosen = wantedOnly
            ? list.Where(f => f.Wanted).Select(f => f.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal)
            : null;
        return new TorBoxFileTree(remote, 50, chosen);
    }

    /// <summary>The chosen files' indexes.</summary>
    public static List<int> Indexes(TorBoxFileTree tree) =>
        tree.SelectedFileIds.Select(id => int.Parse(id, System.Globalization.CultureInfo.InvariantCulture)).ToList();
}
