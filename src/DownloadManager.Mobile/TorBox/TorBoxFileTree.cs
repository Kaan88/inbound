using DownloadManager.Contracts;

namespace DownloadManager.Mobile.TorBox;

public enum TorBoxCheckState { Unchecked, Partial, Checked }

public sealed class TorBoxNode
{
    internal TorBoxNode(string name, TorBoxNode? parent) { Name = name; Parent = parent; }

    public string Name { get; }
    public TorBoxNode? Parent { get; }
    public bool IsFolder => FileId is null;
    public string? FileId { get; internal set; }
    public long Size { get; internal set; }
    public bool Selectable { get; internal set; } = true;
    public string? Note { get; internal set; }
    public bool Expanded { get; set; } = true;
    public List<TorBoxNode> Children { get; } = [];
}

/// <summary>
/// The files of a cached torrent or a job as a folder tree with check marks: a folder is checked when all its choosable files are,
/// partial when some are. Files the provider says can't be chosen (no safe path, infected) are shown unchecked and stay that way.
/// </summary>
public sealed class TorBoxFileTree
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<TorBoxNode> _files = [];
    private readonly TorBoxNode _root = new("", null);

    /// <param name="collapseBelow">With more files than this, folders below the top level start closed.</param>
    /// <param name="initiallyChosen">The file ids that start chosen (a torrent's wanted files); null chooses every choosable file.</param>
    public TorBoxFileTree(IEnumerable<RemoteFileDto> files, int collapseBelow = 50, IReadOnlySet<string>? initiallyChosen = null)
    {
        foreach (var file in files)
        {
            var parts = (file.RelativePath ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var folder = _root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var child = folder.Children.FirstOrDefault(c => c.IsFolder && c.Name == parts[i]);
                if (child is null) { child = new TorBoxNode(parts[i], folder); folder.Children.Add(child); }
                folder = child;
            }

            var leaf = new TorBoxNode(parts.Length == 0 ? "Unnamed file" : parts[^1], folder)
            {
                FileId = file.FileId, Size = Math.Max(0, file.Size),
                Selectable = file.Selectable && file.RelativePath is not null && !file.Infected,
                Note = file.Infected ? "Infected" : file.Note,
            };
            folder.Children.Add(leaf);
            _files.Add(leaf);
            if (leaf.Selectable && (initiallyChosen is null || initiallyChosen.Contains(leaf.FileId!))) _selected.Add(leaf.FileId!);
        }

        Total(_root);
        if (_files.Count > collapseBelow)
            foreach (var top in _root.Children)
                foreach (var folder in Folders(top).Where(f => f != top)) folder.Expanded = false;
    }

    public IReadOnlyList<TorBoxNode> Roots => _root.Children;
    public int TotalCount => _files.Count;
    public long TotalBytes => _files.Sum(f => f.Size);
    public int ChoosableCount => _files.Count(f => f.Selectable);
    public int SelectedCount => _selected.Count;
    public long SelectedBytes => _files.Where(f => _selected.Contains(f.FileId!)).Sum(f => f.Size);

    /// <summary>In the order the Host listed them.</summary>
    public IReadOnlyList<string> SelectedFileIds => _files.Where(f => _selected.Contains(f.FileId!)).Select(f => f.FileId!).ToList();

    public TorBoxCheckState StateOf(TorBoxNode node)
    {
        if (!node.IsFolder) return _selected.Contains(node.FileId!) ? TorBoxCheckState.Checked : TorBoxCheckState.Unchecked;
        var leaves = Leaves(node).Where(l => l.Selectable).ToList();
        var on = leaves.Count(l => _selected.Contains(l.FileId!));
        return on == 0 ? TorBoxCheckState.Unchecked : on == leaves.Count ? TorBoxCheckState.Checked : TorBoxCheckState.Partial;
    }

    /// <summary>A file flips; a folder takes all its files unless they are all taken already, then none.</summary>
    public void Toggle(TorBoxNode node)
    {
        if (!node.IsFolder)
        {
            if (!node.Selectable) return;
            if (!_selected.Remove(node.FileId!)) _selected.Add(node.FileId!);
            return;
        }

        var leaves = Leaves(node).Where(l => l.Selectable).ToList();
        var allOn = leaves.All(l => _selected.Contains(l.FileId!));
        foreach (var leaf in leaves)
            if (allOn) _selected.Remove(leaf.FileId!); else _selected.Add(leaf.FileId!);
    }

    /// <summary>The rows to show, top down, skipping what is inside a closed folder.</summary>
    public IReadOnlyList<(TorBoxNode Node, int Depth)> Visible()
    {
        var rows = new List<(TorBoxNode, int)>();
        void Walk(TorBoxNode parent, int depth)
        {
            foreach (var child in parent.Children)
            {
                rows.Add((child, depth));
                if (child.IsFolder && child.Expanded) Walk(child, depth + 1);
            }
        }

        Walk(_root, 0);
        return rows;
    }

    /// <summary>"Files · 1 of 3 · 3.9 GB".</summary>
    public string Summary => $"Files · {SelectedCount} of {ChoosableCount} · {Fmt.Bytes(SelectedBytes)}";

    private static long Total(TorBoxNode node)
    {
        if (!node.IsFolder) return node.Size;
        node.Size = node.Children.Sum(Total);
        return node.Size;
    }

    private static IEnumerable<TorBoxNode> Leaves(TorBoxNode node)
    {
        if (!node.IsFolder) { yield return node; yield break; }
        foreach (var child in node.Children)
            foreach (var leaf in Leaves(child)) yield return leaf;
    }

    private static IEnumerable<TorBoxNode> Folders(TorBoxNode node)
    {
        if (!node.IsFolder) yield break;
        yield return node;
        foreach (var child in node.Children)
            foreach (var folder in Folders(child)) yield return folder;
    }
}
