using DownloadManager.Contracts;

namespace DownloadManager.Mobile.TorBox;

public enum TorBoxCheck
{
    /// <summary>Nothing to check yet (empty field).</summary>
    None,
    Checking,
    /// <summary>TorBox has it cached; its files are in <see cref="TorBoxAddModel.Tree"/>.</summary>
    Hit,
    /// <summary>TorBox does not have it: sending makes it fetch it first.</summary>
    Miss,
    /// <summary>TorBox could not say whether it has it. Sending still works.</summary>
    Unknown,
    NoKey,
    NotMagnet,
    Failed,
}

/// <summary>The outcome of pressing Send to TorBox.</summary>
public sealed record TorBoxSendResult(bool Done, string? Error, string? Note);

/// <summary>
/// Add torrent with TorBox chosen: the magnet, what TorBox says about it (cache hit with files, or a miss), and the request Send makes.
/// A miss is only ever sent with the person's go-ahead (pressing Send on it): that is the one place ApproveUncached is set.
/// </summary>
public sealed class TorBoxAddModel(TorBoxClient client)
{
    private int _generation;
    private bool? _keyUsable;

    public string Input { get; private set; } = "";
    public TorBoxCheck Check { get; private set; }
    public TorBoxFileTree? Tree { get; private set; }

    /// <summary>The Host's words when the check failed.</summary>
    public string? Message { get; private set; }

    public string? Name { get; private set; }

    /// <summary>
    /// The field's text as a magnet: a magnet never holds a raw space (they are written %20 or +), so any whitespace in one came
    /// from the keyboard (Samsung's adds a space after "?") or a line break when pasting, and is dropped.
    /// </summary>
    public static string Normalize(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? string.Concat(t.Where(c => !char.IsWhiteSpace(c))) : t;
    }

    public static bool IsMagnet(string? text)
    {
        var t = Normalize(text);
        return t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) &&
               (t.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) || t.Contains("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The text changed: whatever was known about the old text is gone, and a check still waiting for its answer is dropped.</summary>
    public void Reset()
    {
        _generation++;
        Input = ""; Check = TorBoxCheck.None; Tree = null; Message = null; Name = null;
    }

    /// <summary>The key was saved or cleared meanwhile: the next check asks again.</summary>
    public void ForgetKeyState() => _keyUsable = null;

    /// <summary>
    /// Checks <paramref name="text"/> with the Host. A later call (or a cancel) makes this one's answer be dropped, so the state is always
    /// about the text last given. Returns false when the answer was dropped.
    /// </summary>
    public async Task<bool> CheckAsync(string? text, CancellationToken cancellationToken = default)
    {
        var generation = ++_generation;
        var input = Normalize(text);
        Input = input;
        Tree = null; Message = null; Name = null;
        if (input.Length == 0) { Check = TorBoxCheck.None; return true; }
        if (!IsMagnet(input)) { Check = TorBoxCheck.NotMagnet; Message = TorBoxText.NotMagnet; return true; }

        Check = TorBoxCheck.Checking;
        try
        {
            if (_keyUsable is null)
            {
                var account = await client.AccountAsync(false, cancellationToken).ConfigureAwait(false);
                if (generation != _generation) return false;
                // A read that failed says nothing about the key: the check below shows the real problem.
                if (account.Ok && account.Accounts.FirstOrDefault() is { } a)
                {
                    _keyUsable = a is { HasKey: true, KeyUnreadable: false };
                    if (!_keyUsable.Value)
                    {
                        Check = TorBoxCheck.NoKey;
                        Message = a.KeyUnreadable ? TorBoxText.KeyUnreadable : TorBoxText.NoKey;
                        return true;
                    }
                }
            }
            else if (_keyUsable == false)
            {
                Check = TorBoxCheck.NoKey; Message = TorBoxText.NoKey;
                return true;
            }

            var result = await client.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);
            if (generation != _generation) return false;
            Apply(result);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            if (generation != _generation) return false;
            Check = TorBoxCheck.Failed;
            Message = TorBoxText.Failure(e is InvalidOperationException ? e.Message : null, "network");
            return true;
        }
    }

    private void Apply(RemoteClassifyResult result)
    {
        if (!result.Ok) { Check = TorBoxCheck.Failed; Message = TorBoxText.Failure(result.Error); return; }
        if (result.Kind is not "magnet") { Check = TorBoxCheck.NotMagnet; Message = result.Reason ?? TorBoxText.NotMagnet; return; }
        Name = result.Name;
        if (result.Cached == true && result.Files is { Count: > 0 } files)
        {
            Tree = new TorBoxFileTree(files);
            Check = TorBoxCheck.Hit;
        }
        else Check = result.Cached == false ? TorBoxCheck.Miss : TorBoxCheck.Unknown;
    }

    /// <summary>Sending is allowed: a hit with at least one file chosen, or a miss or unknown that the person may send as it is.</summary>
    public bool CanSend => Check switch
    {
        TorBoxCheck.Hit => Tree is { SelectedCount: > 0 },
        TorBoxCheck.Miss or TorBoxCheck.Unknown => true,
        _ => false,
    };

    /// <summary>"Ready on TorBox · 3 files · 3.9 GB".</summary>
    public string? ReadyTitle => Check == TorBoxCheck.Hit && Tree is { } t
        ? $"Ready on TorBox · {(t.TotalCount == 1 ? "1 file" : t.TotalCount + " files")} · {Fmt.Bytes(t.TotalBytes)}" : null;

    public RemoteAddRequestDto BuildRequest(string? destination)
    {
        var request = new RemoteAddRequestDto { Input = Input, Destination = destination, StartMode = "startNow", Provider = null };
        if (Check == TorBoxCheck.Hit && Tree is { } tree)
        {
            // Cached: only the chosen files, no fetching involved.
            request.Selection = "chosen";
            request.FileIds = tree.SelectedFileIds.ToList();
        }
        else
        {
            // Not cached (or not known to be): the person pressed Send knowing TorBox may fetch it first; all files come when it is ready.
            request.Selection = "all";
            request.ApproveUncached = true;
        }

        return request;
    }

    public async Task<TorBoxSendResult> SendAsync(string? destination, CancellationToken cancellationToken = default)
    {
        if (!CanSend) return new TorBoxSendResult(false, "Nothing to send yet.", null);
        try
        {
            var result = await client.AddAsync(BuildRequest(destination), cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return new TorBoxSendResult(false, TorBoxText.Failure(result.Error), null);
            if (result.Job is { State: "failed" or "blocked" } failed)
                return new TorBoxSendResult(false, TorBoxText.Failure(failed.FailureMessage, failed.FailureKind), null);
            if (!string.IsNullOrWhiteSpace(result.Note)) return new TorBoxSendResult(false, null, result.Note);
            return new TorBoxSendResult(true, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            // The Host may have taken it before the answer was lost: say so, and do not send again by itself.
            return new TorBoxSendResult(false, e is InvalidOperationException ? e.Message : "No answer came back. Check the TorBox list before sending again.", null);
        }
    }
}
