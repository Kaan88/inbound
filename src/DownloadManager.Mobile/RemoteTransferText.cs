using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>The kind of status a Transfers card shows, which decides its colour (x1 to x6).</summary>
public enum TransferTone { Neutral, Running, Paused, Done, Failed, Canceled }

/// <summary>What a failed transfer needs before it can be retried.</summary>
public enum TransferAttention { None, SignIn, ReviewKey, ReviewCertificate }

/// <summary>Plain words for the Transfers screen (mockups x1 to x6): titles, status lines and routes without raw IDs or phone roots.</summary>
public static class RemoteTransferText
{
    public static TransferTone ToneOf(SiteTransferDto t) => t.State switch
    {
        "running" => TransferTone.Running,
        "paused" => TransferTone.Paused,
        "done" => TransferTone.Done,
        "failed" => TransferTone.Failed,
        "canceled" => TransferTone.Canceled,
        _ => TransferTone.Neutral,
    };

    /// <summary>Active means still going or waiting: running, queued or paused.</summary>
    public static bool IsActive(SiteTransferDto t) => t.State is "running" or "queued" or "paused";
    public static bool IsEnded(SiteTransferDto t) => t.State is "done" or "failed" or "canceled";

    /// <summary>How many files a transfer moves (empty folders it makes are not counted).</summary>
    public static int FileCount(SiteTransferDto t) => Math.Max(t.Files - t.Folders, t.Files > 0 ? 1 : 0);

    /// <summary>
    /// The card title: the file or folder name when every item shares one top-level name, "N files" otherwise.
    /// <paramref name="paths"/> are the batch's relative item paths (from <c>sites.transfer.get</c>); null while not known.
    /// A folder's title carries "· N files" once the transfer ended (the status line has the count while it runs).
    /// </summary>
    public static string Title(SiteTransferDto t, IReadOnlyList<string>? paths)
    {
        int files = FileCount(t);
        if (paths is null || paths.Count == 0) return files == 1 ? "1 file" : files + " files";
        var tops = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p).Distinct(StringComparer.Ordinal).ToList();
        if (tops.Count != 1) return files + " files";
        if (paths.Count == 1 && !paths[0].Contains('/')) return tops[0];
        return IsEnded(t) && files > 1 ? tops[0] + " · " + files + " files" : tops[0];
    }

    /// <summary>"Download/Inbound/Music" from "/storage/emulated/0/Download/Inbound/Music"; other paths stay as they are.</summary>
    public static string PhonePath(string path)
    {
        foreach (var root in new[] { "/storage/emulated/0/", "/sdcard/", "/storage/self/primary/" })
            if (path.StartsWith(root, StringComparison.Ordinal)) return path[root.Length..].TrimEnd('/');
        if (path is "/storage/emulated/0" or "/sdcard") return "Phone";
        return path;
    }

    private static string Place(string site, string? folder)
    {
        var trimmed = (folder ?? "").Trim('/');
        return trimmed.Length == 0 ? site : site + " · " + trimmed;
    }

    /// <summary>(Source, destination) of the "Source → Destination" route.</summary>
    public static (string Source, string Destination) Route(SiteTransferDto t, bool phoneHost) => t.Direction switch
    {
        "upload" => (phoneHost ? PhonePath(t.LocalFolder) : t.LocalFolder, Place(t.SiteName, t.RemoteFolder)),
        "copy" => (t.SiteName, Place(t.DestinationSiteName ?? "", t.DestinationFolder)),
        _ => (t.SiteName, phoneHost ? PhonePath(t.LocalFolder) : t.LocalFolder),
    };

    /// <summary>(From, To) of the details sheet: like <see cref="Route"/> but with the folders on both sides.</summary>
    public static (string From, string To) RouteDetail(SiteTransferDto t, bool phoneHost)
    {
        string local = phoneHost ? PhonePath(t.LocalFolder) : t.LocalFolder;
        return t.Direction switch
        {
            "upload" => (local, Place(t.SiteName, t.RemoteFolder)),
            "copy" => (Place(t.SiteName, t.RemoteFolder), Place(t.DestinationSiteName ?? "", t.DestinationFolder)),
            _ => (Place(t.SiteName, t.RemoteFolder), local),
        };
    }

    /// <summary>"today 13:31", "Yesterday 09:10" or "9 May".</summary>
    public static string StartedText(SiteTransferDto t, DateTimeOffset now)
    {
        var text = Fmt.When(t.CreatedAt, now);
        return t.CreatedAt.ToLocalTime().Date == now.ToLocalTime().Date ? "today " + text : text;
    }

    /// <summary>A long ID in two even lines (the sheet shows it in monospace).</summary>
    public static string WrapId(string id) => RemoteSftpKeyLines(id);

    private static string RemoteSftpKeyLines(string id)
    {
        if (id.Length <= 20) return id;
        int half = (id.Length + 1) / 2;
        return id[..half] + "\n" + id[half..];
    }

    public static int NotCopied(SiteTransferDto t) => Math.Max(0, t.Files - t.FilesDone - t.FilesSkipped);

    /// <summary>The status line, state word first for ended and paused transfers (coloured by <see cref="ToneOf"/>).</summary>
    public static string Status(SiteTransferDto t, DateTimeOffset now)
    {
        int files = FileCount(t);
        bool single = files <= 1;
        switch (t.State)
        {
            case "running":
            {
                var rate = t.Speed > 0 ? " · " + Fmt.Rate(t.Speed) : "";
                var left = t.Speed > 0 && t.Bytes > t.BytesDone ? " · " + Fmt.Eta((double)(t.Bytes - t.BytesDone) / t.Speed) : "";
                if (single) return t.Bytes > 0 ? Sizes(t) + rate + left : "Starting" + rate;
                int done = Math.Min(files, t.FilesDone + t.FilesSkipped);
                return $"{done} of {files} files" + (t.Bytes > 0 ? " · " + Sizes(t) : "") + rate;
            }
            case "queued": return "Queued";
            case "paused": return "Paused" + (t.Bytes > 0 ? " · " + Sizes(t) : "");
            case "failed":
            {
                int missing = Math.Max(NotCopied(t), t.FilesFailed);
                return single || missing == 0 ? "Failed" : $"Failed · {missing} of {files} not copied";
            }
            case "canceled": return "Canceled";
            case "done":
            {
                var line = "Finished";
                if (single) return line + (t.Bytes > 0 ? " · " + Fmt.Bytes(t.Bytes) : "") + When(t);
                line += $" · {files} files";
                if (t.FilesSkipped > 0) line += $", {t.FilesSkipped} already there";
                return line;
            }
            default: return t.State;
        }

        string When(SiteTransferDto x) => x.FinishedAt is { } at ? " · " + Fmt.When(at, now) : "";
    }

    /// <summary>"212 of 455 MB": done and total, the unit written once when both share it.</summary>
    public static string Sizes(SiteTransferDto t)
    {
        var total = Fmt.Bytes(t.Bytes);
        var done = Fmt.Bytes(Math.Min(t.BytesDone, t.Bytes));
        string unit = total[(total.LastIndexOf(' ') + 1)..];
        if (!done.EndsWith(" " + unit, StringComparison.Ordinal)) return done + " of " + total;
        var number = done[..^(unit.Length + 1)];
        if (number.EndsWith(".0", StringComparison.Ordinal)) number = number[..^2];
        return number + " of " + total;
    }

    /// <summary>Filter chips of the Transfers screen.</summary>
    public static bool InFilter(SiteTransferDto t, string filter) => filter switch
    {
        "active" => IsActive(t),
        "finished" => t.State == "done",
        "failed" => t.State == "failed",
        "canceled" => t.State == "canceled",
        _ => true,
    };

    /// <summary>The reason banner of the details sheet; null when there is nothing to say.</summary>
    public static (string Title, string? Detail, bool Warning)? Reason(SiteTransferDto t)
    {
        if (t.State == "paused") return ("Paused", "Unfinished files restart from the beginning on resume.", true);
        if (t.State != "failed")
            return t.State == "done" && !string.IsNullOrWhiteSpace(t.Message) ? ("Finished with a note", t.Message, true) : null;
        string? problem = ProblemText(t);
        if (problem is not null) return (problem, string.IsNullOrWhiteSpace(t.Message) ? null : t.Message, false);
        int missing = Math.Max(NotCopied(t), t.FilesFailed);
        var detail = missing > 0 ? (missing == 1 ? "Retry sends only the file that is missing." : $"Retry sends only the {missing} files that are missing.") : null;
        return (string.IsNullOrWhiteSpace(t.Message) ? "The transfer stopped" : t.Message!, detail, false);
    }

    /// <summary>True when the failure needs a sign-in or a trust decision first, so Retry alone would fail again.</summary>
    public static bool NeedsAttention(SiteTransferDto t) => ProblemText(t) is not null;

    /// <summary>
    /// The action a failed transfer shows in place of Retry when Retry alone would fail again: the same sign-in or trust flow
    /// as in the folder view. None when Retry is the right action.
    /// </summary>
    public static TransferAttention AttentionOf(SiteTransferDto t) => t.State != "failed" ? TransferAttention.None : t.Problem switch
    {
        SiteProblems.SignInNeeded => TransferAttention.SignIn,
        SiteProblems.HostKeyChanged or SiteProblems.HostKeyNotAccepted => TransferAttention.ReviewKey,
        SiteProblems.CertificateNotTrusted => TransferAttention.ReviewCertificate,
        _ => TransferAttention.None,
    };

    /// <summary>The words of the action for <paramref name="attention"/>.</summary>
    public static string AttentionLabel(TransferAttention attention) => attention switch
    {
        TransferAttention.SignIn => "Sign in again",
        TransferAttention.ReviewCertificate => "Review certificate",
        TransferAttention.ReviewKey => "Review server key",
        _ => "",
    };

    private static string? ProblemText(SiteTransferDto t) => t.Problem switch
    {
        SiteProblems.SignInNeeded => "Sign in to " + t.SiteName + " again, then retry",
        SiteProblems.HostKeyChanged => "The server's key changed",
        SiteProblems.HostKeyNotAccepted => "The server's key is not accepted yet",
        SiteProblems.CertificateNotTrusted => "The server's certificate is not trusted",
        _ => null,
    };

    /// <summary>The file name of a batch item path ("Photos/IMG_1.jpg" is "IMG_1.jpg").</summary>
    public static string LeafName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>A file of the details sheet: its name, what happened and whether that is a problem.</summary>
    public static (string Name, string Line, TransferTone Tone) FileLine(SiteTransferItemDto item) => item.State switch
    {
        "done" => (LeafName(item.Path), "Copied" + (item.Size > 0 ? " · " + Fmt.Bytes(item.Size) : ""), TransferTone.Done),
        "skipped" => (LeafName(item.Path), "Already there", TransferTone.Neutral),
        "failed" => (LeafName(item.Path), "Not copied" + (string.IsNullOrWhiteSpace(item.Error) ? "" : " · " + item.Error), TransferTone.Failed),
        "running" => (LeafName(item.Path), "Copying" + (item.Size > 0 ? " · " + Fmt.Bytes(item.Size) : ""), TransferTone.Running),
        _ => (LeafName(item.Path), "Waiting" + (item.Size > 0 ? " · " + Fmt.Bytes(item.Size) : ""), TransferTone.Neutral),
    };
}
