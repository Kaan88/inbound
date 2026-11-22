using DownloadManager.Contracts;
using DownloadManager.Mobile.TorBox;

namespace DownloadManager.Mobile.Torrents;

public enum TorrentCheck
{
    /// <summary>Nothing to check yet (empty field).</summary>
    None,
    /// <summary>Asking the Host about the text or file.</summary>
    Checking,
    /// <summary>A magnet was handed to the Host; it is looking for peers that have the file list. Nothing downloads yet.</summary>
    Waiting,
    /// <summary>The file list is known: <see cref="TorrentAddModel.Tree"/>.</summary>
    Found,
    /// <summary>A .torrent file an older Host could not list (it can't take the file's bytes to look at): it is added with all its files.</summary>
    FileUnlisted,
    /// <summary>The torrent is already in the Host's list.</summary>
    Existing,
    NotInput,
    /// <summary>The Host has torrents turned off, or no torrent engine.</summary>
    Off,
    Failed,
}

/// <summary>The outcome of pressing Download.</summary>
public sealed record TorrentSendResult(bool Done, string? Error);

/// <summary>
/// Add torrent with This phone (or the paired Host) chosen. A .torrent file is read by the Host (<c>torrent.inspect</c>) and added with its
/// bytes only when the person presses Download. A magnet has no file list until peers send it, so it is handed to the Host at once with
/// "ask when ready": it finds the file list and waits, downloading nothing, until Download names the files (or Cancel removes it).
/// </summary>
public sealed class TorrentAddModel(TorrentClient client)
{
    private int _generation;
    private byte[]? _fileBytes;
    private readonly List<Guid> _strays = [];

    public string Input { get; private set; } = "";
    public TorrentCheck Check { get; private set; }
    public TorBoxFileTree? Tree { get; private set; }
    public string? Message { get; private set; }
    public string? Name { get; private set; }

    /// <summary>The folder the Host will save to (its torrent folder), from <c>torrent.inspect</c>.</summary>
    public string? Destination { get; private set; }

    /// <summary>Room left at <see cref="Destination"/> and the Host's words when the files do not fit.</summary>
    public string? SpaceProblem { get; private set; }

    /// <summary>The job a pasted magnet created on the Host to find its file list. Null for a file, or before it is added.</summary>
    public Guid? PendingJobId { get; private set; }

    /// <summary>Peers the pending magnet has found its file list through.</summary>
    public int Peers { get; private set; }

    public bool IsFile => _fileBytes is not null;

    /// <summary>The jobs the Host made to look for a file list that nobody will download now. The caller removes them from the Host (no file was written).</summary>
    public IReadOnlyList<Guid> TakeStrays()
    {
        var jobs = _strays.ToArray();
        _strays.Clear();
        return jobs;
    }

    /// <summary>Torrents are off on this Host (the screen knows from its settings): nothing is asked of it.</summary>
    public void MarkOff()
    {
        Reset();
        Check = TorrentCheck.Off;
        Message = TorrentText.TurnedOff;
    }

    public bool IsMagnet => TorBoxAddModel.IsMagnet(Input);

    /// <summary>The text changed, or the screen closed: what was known is gone, and an answer still on its way is dropped. A pending magnet's job is for the caller to remove (<see cref="TakeStrays"/>).</summary>
    public void Reset()
    {
        _generation++;
        // The magnet's job on the Host was only made to find its file list; with the text gone it is nobody's: take it back with TakeStrays.
        if (PendingJobId is { } pending) _strays.Add(pending);
        Input = ""; Check = TorrentCheck.None; Tree = null; Message = null; Name = null; Destination = null; SpaceProblem = null;
        PendingJobId = null; Peers = 0; HeldFor = null; _fileBytes = null;
    }

    /// <summary>
    /// A magnet: asks the Host about it (<c>torrent.inspect</c>, which finds no peers), then hands it over to find its file list.
    /// Returns false when the answer was dropped because the text changed meanwhile.
    /// </summary>
    public async Task<bool> CheckMagnetAsync(string? text, string? destination = null, CancellationToken cancellationToken = default)
    {
        var generation = ++_generation;
        var input = TorBoxAddModel.Normalize(text);
        if (PendingJobId is { } previous) _strays.Add(previous);
        Input = input; _fileBytes = null;
        Tree = null; Message = null; Name = null; PendingJobId = null; Peers = 0; HeldFor = null; Destination = null; SpaceProblem = null;
        if (input.Length == 0) { Check = TorrentCheck.None; return true; }
        if (!TorBoxAddModel.IsMagnet(input)) { Check = TorrentCheck.NotInput; Message = TorrentText.NotInput; return true; }
        Check = TorrentCheck.Checking;
        try
        {
            var seen = await client.InspectAsync(input, destination, cancellationToken).ConfigureAwait(false);
            if (generation != _generation) return false;
            if (!seen.Ok) { Fail(seen.Error); return true; }
            Name = seen.Name;
            Destination = seen.Destination;
            if (seen.ExistingJobId is not null) { Check = TorrentCheck.Existing; return true; }
            // Not cancelled: once sent, the Host makes a job, and only its reply says which one to take back if the screen
            // closed meanwhile (Fable A9-8). Reset moved the generation, so the job goes to the strays below.
            var added = await client.AddAsync(TorrentClient.MagnetRequest(input, destination), CancellationToken.None).ConfigureAwait(false);
            if (generation != _generation)
            {
                // The text changed while the Host took it: the job it made is nobody's now.
                if (added is { Ok: true, Existing: false, Job: { } stray }) _strays.Add(stray.Id);
                return false;
            }

            if (!added.Ok || added.Job is null) { Fail(added.Error); return true; }
            if (added.Existing) { Check = TorrentCheck.Existing; return true; }
            PendingJobId = added.Job.Id;
            Check = TorrentCheck.Waiting;
            Apply(added.Job);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation != _generation) return false;
            Fail(e is InvalidOperationException ? e.Message : null);
            return true;
        }
    }

    /// <summary>
    /// A .torrent file the person chose, sent to the Host by its bytes. A Host that cannot read them (an older one, which reads only a path) is
    /// given the whole file at Download; its files can be changed on the torrent's Files tab.
    /// </summary>
    /// <summary>What a Host without <see cref="TorrentInspectRequest.Data"/> answers to an inspect with no Input.</summary>
    public const string OlderHostEmptyInput = "Enter a magnet link or choose a .torrent file.";

    public async Task<bool> CheckFileAsync(byte[] bytes, string displayName, CancellationToken cancellationToken = default)
    {
        var generation = ++_generation;
        if (PendingJobId is { } previous) _strays.Add(previous);
        _fileBytes = bytes;
        Input = displayName;
        Tree = null; Message = null; Name = displayName; PendingJobId = null; Peers = 0; Destination = null; SpaceProblem = null;
        Check = TorrentCheck.Checking;
        try
        {
            var seen = await client.InspectFileAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (generation != _generation) return false;
            if (!seen.Ok && seen.Error == OlderHostEmptyInput)
            {
                // An older Host ignored the bytes and found nothing in Input: Download hands it the whole file.
                Check = TorrentCheck.FileUnlisted;
                return true;
            }

            if (!seen.Ok || seen.Files is not { Count: > 0 } files) { Fail(seen.Ok ? TorrentText.FileNotRead : seen.Error); _fileBytes = null; return true; }
            Name = seen.Name ?? displayName;
            Destination = seen.Destination;
            SpaceProblem = seen.SpaceProblem;
            if (seen.ExistingJobId is not null) { Check = TorrentCheck.Existing; return true; }
            Tree = TorrentFileTrees.Build(files);
            Check = TorrentCheck.Found;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation != _generation) return false;
            Fail(e is InvalidOperationException ? e.Message : null);
            _fileBytes = null;
            return true;
        }
    }

    private void Fail(string? error)
    {
        // A refusal because torrents are off is a setting the person can change, not a failure of the torrent.
        if (IsTurnedOff(error)) { Check = TorrentCheck.Off; Message = TorrentText.TurnedOff; return; }
        Check = TorrentCheck.Failed;
        Message = string.IsNullOrWhiteSpace(error) ? "That could not be done." : error.Trim();
    }

    private static bool IsTurnedOff(string? error) => !string.IsNullOrWhiteSpace(error) && error.Contains("turned off", StringComparison.OrdinalIgnoreCase);

    private void Apply(JobDto job)
    {
        if (job.Torrent is not { } torrent) return;
        Peers = torrent.Peers;
        HeldFor = TorrentText.Waiting(torrent);
    }

    /// <summary>
    /// While <see cref="TorrentCheck.Waiting"/>: why the Host holds the magnet ("Waiting for Wi-Fi"), so no peer is asked for
    /// its file list yet; null while it looks (Fable A9-5).
    /// </summary>
    public string? HeldFor { get; private set; }

    /// <summary>
    /// The pending magnet's job as the list now has it (<paramref name="job"/> null: it is gone). Once it has its file list, asks the Host for the
    /// files. Returns true when the screen should draw again. A newer text drops the answer.
    /// </summary>
    public async Task<bool> PollAsync(JobDto? job, CancellationToken cancellationToken = default)
    {
        if (Check != TorrentCheck.Waiting || PendingJobId is not { } id) return false;
        var generation = _generation;
        if (job is null) { Check = TorrentCheck.Failed; Message = "The torrent was removed from Downloads."; PendingJobId = null; return true; }
        var peers = Peers;
        var held = HeldFor;
        Apply(job);
        if (job.Torrent is not { HasMetadata: true })
        {
            if (job.State is "failed" or "cancelled")
            {
                Check = TorrentCheck.Failed;
                Message = job.Failure?.Message is { Length: > 0 } why ? why : "The torrent could not start.";
                return true;
            }

            return peers != Peers || held != HeldFor;
        }

        try
        {
            var files = await client.FilesAsync(id, cancellationToken).ConfigureAwait(false);
            if (generation != _generation || Check != TorrentCheck.Waiting) return false;
            if (!files.Ok || files.Files.Count == 0) return false;
            Tree = TorrentFileTrees.Build(files.Files);
            Check = TorrentCheck.Found;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            // Asked again on the next look.
            return false;
        }
    }

    /// <summary>Download is allowed: a file list with something chosen and room for it, or a file added whole.</summary>
    public bool CanDownload => Check switch
    {
        TorrentCheck.Found => Tree is { SelectedCount: > 0 } && SpaceProblem is null,
        TorrentCheck.FileUnlisted => true,
        _ => false,
    };

    /// <summary>"Download 14.9 GB"; plain "Download" without a size to name.</summary>
    public string DownloadLabel => Check == TorrentCheck.Found && Tree is { SelectedCount: > 0 } t ? "Download " + Fmt.Bytes(t.SelectedBytes) : "Download";

    /// <summary>"30 files · 41.7 GB".</summary>
    public string? FoundTitle => Tree is { } t ? $"{(t.TotalCount == 1 ? "1 file" : t.TotalCount + " files")} · {Fmt.Bytes(t.TotalBytes)}" : null;

    /// <summary>"File list found through 34 peers" for a magnet ("File list found" when the Host already had it), "File list read from the torrent file" for a file.</summary>
    public string FoundDetail => IsFile ? "File list read from the torrent file" : Peers > 0 ? $"File list found through {TorrentText.Peers(Peers)}" : "File list found";

    /// <summary>Names the files and starts the pending magnet, or adds the chosen file's torrent.</summary>
    public async Task<TorrentSendResult> DownloadAsync(string? destination, CancellationToken cancellationToken = default)
    {
        if (!CanDownload) return new TorrentSendResult(false, "Nothing to download yet.");
        try
        {
            if (_fileBytes is { } bytes)
            {
                var chosen = Check == TorrentCheck.Found ? TorrentFileTrees.Indexes(Tree!) : null;
                var added = await client.AddAsync(TorrentClient.FileRequest(bytes, destination, chosen), cancellationToken).ConfigureAwait(false);
                return added.Ok ? new TorrentSendResult(true, null) : new TorrentSendResult(false, Words(added.Error));
            }

            if (PendingJobId is not { } id) return new TorrentSendResult(false, "Nothing to download yet.");
            var set = await client.SetFilesAsync(id, TorrentFileTrees.Indexes(Tree!), cancellationToken).ConfigureAwait(false);
            if (!set.Ok) return new TorrentSendResult(false, Words(set.Error));
            // The Host has it from here: it no longer waits for a choice, so Cancel must not remove it.
            PendingJobId = null;
            return new TorrentSendResult(true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            return new TorrentSendResult(false, e is InvalidOperationException ? e.Message : "No answer came back. Check Downloads before adding it again.");
        }
    }

    private static string Words(string? error) => string.IsNullOrWhiteSpace(error) ? "That could not be done." : error.Trim();
}
