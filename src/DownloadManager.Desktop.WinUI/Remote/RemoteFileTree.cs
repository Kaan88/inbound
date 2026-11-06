using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What was chosen in a file tree: everything that can be chosen, or these ids.</summary>
internal sealed record RemoteFileChoice(bool All, IReadOnlyList<string> FileIds);

/// <summary>A folder or a file in <see cref="RemoteFileTree"/>.</summary>
internal sealed class RemoteFileNode
{
    public required string Name { get; init; }
    public RemoteFileNode? Parent { get; init; }
    public int Depth { get; init; }
    public bool IsFolder { get; init; }
    public string? FileId { get; init; }
    public bool Infected { get; init; }

    /// <summary>Why it can't be chosen, in the Host's words where it gave some.</summary>
    public string? Note { get; init; }

    /// <summary>A file's size, or everything under a folder.</summary>
    public long Size { get; internal set; }

    public bool IsExpanded { get; set; }
    public List<RemoteFileNode> Children { get; } = [];

    /// <summary>A file: it can be chosen. A folder: something under it can.</summary>
    public bool CanChoose => ChoosableFiles > 0;

    /// <summary>A folder is null while only some of what can be chosen under it is.</summary>
    public bool? Checked => ChosenFiles == 0 ? false : ChosenFiles == ChoosableFiles ? true : null;

    internal int ChoosableFiles { get; set; }
    internal int ChosenFiles { get; set; }
    internal long ChosenBytes { get; set; }
}

/// <summary>
/// A provider item's files as folders, with what is chosen. Folders come only from the relative paths
/// the Host gave. A file that is infected, empty or has no safe path stays on show with its note and
/// can never be chosen, whatever a folder above it does. Nothing here touches the UI.
/// </summary>
internal sealed class RemoteFileTree
{
    private const int MostRowsExpanded = 300;

    private readonly List<RemoteFileNode> _roots = [];
    private readonly List<RemoteFileNode> _files = [];

    private RemoteFileTree()
    {
    }

    public IReadOnlyList<RemoteFileNode> Roots => _roots;

    public int FileCount => _files.Count;

    public int ChoosableCount => _files.Count(file => file.CanChoose);

    public int ChosenCount => _roots.Sum(node => node.ChosenFiles);

    public long ChosenBytes => _roots.Sum(node => node.ChosenBytes);

    public bool AllChosen => ChoosableCount > 0 && ChosenCount == ChoosableCount;

    public IEnumerable<string> ChosenFileIds => _files.Where(file => file.ChosenFiles > 0).Select(file => file.FileId!);

    public RemoteFileChoice Choice => new(AllChosen, AllChosen ? [] : ChosenFileIds.ToList());

    public static RemoteFileTree Empty { get; } = new();

    public static RemoteFileTree Build(IEnumerable<RemoteFileDto> files)
    {
        var tree = new RemoteFileTree();
        var folders = new Dictionary<(RemoteFileNode?, string), RemoteFileNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!string.IsNullOrEmpty(file.FileId) && !seen.Add(file.FileId)) continue;
            tree.Add(file, folders);
        }

        tree.Finish(tree._roots);
        return tree;
    }

    private void Add(RemoteFileDto file, Dictionary<(RemoteFileNode?, string), RemoteFileNode> folders)
    {
        // A path the Host wouldn't vouch for names no folder, so it stays at the top.
        var parts = string.IsNullOrWhiteSpace(file.RelativePath)
            ? []
            : UrlRedactor.HideAddresses(file.RelativePath).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        RemoteFileNode? parent = null;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            // One folder on disk, however a provider spells it.
            var key = (parent, parts[i].ToUpperInvariant());
            if (!folders.TryGetValue(key, out var folder))
            {
                folder = new RemoteFileNode { Name = parts[i], Parent = parent, Depth = parent is null ? 0 : parent.Depth + 1, IsFolder = true };
                folders[key] = folder;
                (parent?.Children ?? _roots).Add(folder);
            }

            parent = folder;
        }

        var hasPath = parts.Length > 0;
        var note = NoteFor(file, hasPath);
        var node = new RemoteFileNode
        {
            Name = hasPath ? parts[^1] : "File with no safe path",
            Parent = parent,
            Depth = parent is null ? 0 : parent.Depth + 1,
            FileId = file.FileId,
            Infected = file.Infected,
            Size = Math.Max(0, file.Size),
            Note = note,
            ChoosableFiles = note is null ? 1 : 0,
        };
        (parent?.Children ?? _roots).Add(node);
        _files.Add(node);
    }

    private static string? NoteFor(RemoteFileDto file, bool hasPath)
    {
        var words = string.IsNullOrWhiteSpace(file.Note) ? null : UrlRedactor.HideAddresses(file.Note);
        if (!hasPath) return words ?? "Its path isn't safe to use.";
        if (file.Infected) return words ?? "Marked as infected.";
        if (file.Size <= 0) return words ?? "It is empty.";
        if (string.IsNullOrEmpty(file.FileId)) return words ?? "It can't be chosen.";
        return file.Selectable ? null : words ?? "It can't be chosen.";
    }

    private void Finish(List<RemoteFileNode> nodes)
    {
        var expand = _files.Count <= MostRowsExpanded;
        void Walk(List<RemoteFileNode> level)
        {
            level.Sort((left, right) => left.IsFolder != right.IsFolder
                ? (left.IsFolder ? -1 : 1)
                : NaturalStringComparer.Instance.Compare(left.Name, right.Name));
            foreach (var node in level.Where(node => node.IsFolder))
            {
                // With many files, only the first level opens by itself, and the list stays quick to read.
                node.IsExpanded = expand || node.Depth == 0;
                Walk(node.Children);
                node.Size = node.Children.Sum(child => child.Size);
                node.ChoosableFiles = node.Children.Sum(child => child.ChoosableFiles);
            }
        }

        Walk(nodes);
    }

    /// <summary>What is on show: every open folder's contents, in order.</summary>
    public IEnumerable<RemoteFileNode> Visible()
    {
        static IEnumerable<RemoteFileNode> Walk(IEnumerable<RemoteFileNode> level)
        {
            foreach (var node in level)
            {
                yield return node;
                if (!node.IsFolder || !node.IsExpanded) continue;
                foreach (var child in Walk(node.Children)) yield return child;
            }
        }

        return Walk(_roots);
    }

    /// <summary>What a folder shows when it opens.</summary>
    public static IEnumerable<RemoteFileNode> VisibleUnder(RemoteFileNode folder)
    {
        foreach (var child in folder.Children)
        {
            yield return child;
            if (!child.IsFolder || !child.IsExpanded) continue;
            foreach (var nested in VisibleUnder(child)) yield return nested;
        }
    }

    /// <summary>A folder takes everything under it that can be chosen. Something that can't be chosen stays as it is.</summary>
    public void SetChecked(RemoteFileNode node, bool chosen)
    {
        if (!node.IsFolder)
        {
            if (!node.CanChoose || (node.ChosenFiles > 0) == chosen) return;
            var change = chosen ? 1 : -1;
            for (var at = node; at is not null; at = at.Parent)
            {
                at.ChosenFiles += change;
                at.ChosenBytes += change * node.Size;
            }

            return;
        }

        foreach (var child in node.Children) SetChecked(child, chosen);
    }

    public void SelectAll()
    {
        foreach (var file in _files) SetChecked(file, true);
    }

    public void SelectNone()
    {
        foreach (var file in _files) SetChecked(file, false);
    }

    /// <summary>Puts back an earlier choice after the list was read again. An id no longer there is simply gone.</summary>
    public void Restore(IEnumerable<string> fileIds)
    {
        var chosen = fileIds.ToHashSet(StringComparer.Ordinal);
        foreach (var file in _files.Where(file => file.FileId is not null && chosen.Contains(file.FileId))) SetChecked(file, true);
    }

    /// <summary>For the line above the tree. Sizes count only what is chosen.</summary>
    public string Summary()
    {
        if (_files.Count == 0) return string.Empty;
        var left = _files.Count - ChoosableCount;
        var chosen = $"{ChosenCount} of {ChoosableCount} {(ChoosableCount == 1 ? "file" : "files")} selected";
        var size = ChosenCount > 0 ? $", {RemoteText.Bytes(ChosenBytes)}" : string.Empty;
        var refused = left > 0 ? $". {left} can't be selected" : string.Empty;
        return chosen + size + refused;
    }
}
