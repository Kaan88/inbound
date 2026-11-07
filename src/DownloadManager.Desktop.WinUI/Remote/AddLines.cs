using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Which way into the add dialog this is. It decides what the dialog shows, and what it accepts.</summary>
internal enum AddFocus
{
    /// <summary>Add Links: anything pasted.</summary>
    Links,
    /// <summary>Add Magnet: only magnets get through.</summary>
    Magnet,
    /// <summary>Import Torrent File: the files picked, with no paste box.</summary>
    TorrentFiles,
    /// <summary>Add URL, for an address already found to go through a provider.</summary>
    OneAddress,
    /// <summary>Import Download List, for the entries that can't simply be added. No paste box: the lines are the file's.</summary>
    ImportedLinks,
}

/// <summary>Where a line goes when it is added. Nothing but <see cref="Direct"/> ever becomes a <c>job.add</c>.</summary>
internal enum AddRoute
{
    /// <summary>Not known yet. It can't go anywhere until it is.</summary>
    Checking,
    /// <summary><c>job.add</c>, like any ordinary download.</summary>
    Direct,
    /// <summary><c>remote.add</c> with the input.</summary>
    Provider,
    /// <summary>The file selection dialog on the item it refers to, then <c>remote.add</c> with that item.</summary>
    FolderLink,
    /// <summary>It can't be added as it is.</summary>
    None,
}

/// <summary>Finds what could be added in pasted text. Nothing here looks at the network or at a file.</summary>
internal static partial class LinkExtractor
{
    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ')', ']', '}', '!', '?'];

    [GeneratedRegex(@"https?://[^\s""'<>`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpPattern();

    // A path counts only as a whole line, because "C:\x.torrent" inside a sentence is anyone's guess.
    [GeneratedRegex(@"(?<link>(?:https?://|magnet:)[^\s""'<>`]+)|^[ \t]*(?<path>(?:[a-z]:\\|\\\\)[^\r\n""<>|*?]*\.torrent)[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex InputPattern();

    /// <summary>What Add Links takes with no provider to send anything to: distinct http and https links in free text.</summary>
    public static List<string> HttpLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return HttpPattern().Matches(text)
            .Select(match => match.Value.TrimEnd(TrailingPunctuation))
            .Where(IsWebLink)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// With a provider: links, folder links and magnets anywhere in the text, and .torrent paths on a line
    /// of their own, in the order they appear. Case counts: a hoster link's key is case-sensitive, so two
    /// that differ only by case are two different links.
    /// </summary>
    public static List<string> Inputs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();

        // A multi-line TextBox hands back bare CRs, and the regular expression's ^ and $ only know LF.
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (Match match in InputPattern().Matches(lines))
        {
            var path = match.Groups["path"];
            var input = path.Success ? path.Value : match.Groups["link"].Value.TrimEnd(TrailingPunctuation);
            if (path.Success || IsMagnet(input) || IsWebLink(input)) found.Add(input);
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    public static bool IsMagnet(string input) => input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    public static bool IsTorrentPath(string input) => input.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && !IsMagnet(input) && !input.Contains("://", StringComparison.Ordinal);

    public static bool IsWebLink(string input)
        => Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

/// <summary>
/// One line of the add dialog. <see cref="Input"/> can hold a secret, such as a hoster link's key or a
/// direct link's token, so it is only ever sent to the Host and never shown.
/// </summary>
internal sealed class AddLine(string input)
{
    public string Input { get; } = input;

    /// <summary>What the Host said. Null until it has, and failed calls have <c>Ok = false</c>.</summary>
    public RemoteClassifyResult? Result { get; internal set; }

    /// <summary>Refused before anything was asked, such as a web link in Add Magnet.</summary>
    public string? Refusal { get; internal set; }

    /// <summary>A job with this address is already on the list. Only matters for direct downloads.</summary>
    public bool IsListed { get; internal set; }

    public bool IsSelected { get; internal set; }

    /// <summary>Where it is among the lines, from 1, when there is more than one. Lines showing only a host are told apart by this.</summary>
    public int Position { get; internal set; }

    /// <summary>The user said so, and a later answer from the Host doesn't change it.</summary>
    public bool SelectionTouched { get; internal set; }

    /// <summary>The one way a link nobody could classify may become a direct download: the user asked for exactly that.</summary>
    public bool DirectChosen { get; internal set; }

    public bool IsChecking => Result is null && Refusal is null;

    /// <summary>The Host didn't answer, or said it can't tell. The link could be one a provider handles.</summary>
    public bool CantTell => Result is { Ok: false } or { Kind: "undetermined" };

    /// <summary>Called ordinary, with a listed site left unchecked. It can still go directly, and the note says what is at stake.</summary>
    public bool CheckIncomplete => Result is { Ok: true, Kind: "ordinaryLink", CheckIncomplete: true };

    public AddRoute Route
    {
        get
        {
            if (Refusal is not null) return AddRoute.None;
            if (Result is not { } result) return AddRoute.Checking;
            if (CantTell) return DirectChosen && LinkExtractor.IsWebLink(Input) ? AddRoute.Direct : AddRoute.None;
            return result.Kind switch
            {
                "ordinaryLink" or "providerDirectLink" => AddRoute.Direct,
                "magnet" or "torrentFile" or "hosterLink" => AddRoute.Provider,
                "folderLink" when result.ItemId is not null && result.ItemKind is not null && result.Provider is not null => AddRoute.FolderLink,
                // Includes any kind this build has never heard of.
                _ => AddRoute.None,
            };
        }
    }

    /// <summary>The provider says the site is down, or that it has no links left for it today. Adding it would most likely just fail.</summary>
    public bool HosterLooksUnusable => Result is { Kind: "hosterLink" } hoster &&
        (hoster.HosterAvailable == false || hoster.HosterDailyLinksLeft == 0 || hoster.HosterDailyBytesLeft == 0);

    public override string ToString() => "[add line]";
}

/// <summary>
/// The lines of one add dialog and what the Host says about each. Text changes are debounced, a line
/// keeps its answer while it stays in the text, and only new ones are asked about, one at a time on one
/// connection of their own. Closing the session closes that connection, which cancels the call.
/// Every public member belongs to the UI thread, and every event is raised on it.
/// </summary>
internal sealed class AddLineSession : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);

    private readonly AddFocus _focus;
    private readonly Func<IRemoteClassifier> _open;
    private readonly Func<string, bool> _isListed;
    private readonly Action<Action> _post;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _queueSync = new();
    private readonly Queue<AddLine> _queue = new();
    private readonly List<AddLine> _lines = [];
    private CancellationTokenSource? _pendingText;
    private bool _textPending;
    private bool _working;
    private bool _skipListed = true;
    private bool _closed;

    public AddLineSession(AddFocus focus, Func<IRemoteClassifier> open, Func<string, bool> isListed, Action<Action> post, TimeSpan? debounce = null)
    {
        _focus = focus;
        _open = open;
        _isListed = isListed;
        _post = post;
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>Which lines there are changed.</summary>
    public event Action? LinesChanged;

    /// <summary>What is known about a line changed.</summary>
    public event Action<AddLine>? LineChanged;

    public IReadOnlyList<AddLine> Lines => _lines;

    /// <summary>The same rule as the Skip URLs box: a direct link already on the list starts unticked.</summary>
    public bool SkipListed
    {
        get => _skipListed;
        set
        {
            if (_skipListed == value) return;
            _skipListed = value;
            foreach (var line in _lines.Where(line => line.Route == AddRoute.Direct)) Default(line);
        }
    }

    /// <summary>At least one line goes somewhere, none of those chosen is still unknown, and the lines are the ones in the box.</summary>
    public bool CanAdd => !_textPending && _lines.Any(line => line.IsSelected && line.Route is AddRoute.Direct or AddRoute.Provider or AddRoute.FolderLink)
        && !_lines.Any(line => line.IsSelected && line.Route == AddRoute.Checking);

    public IReadOnlyList<AddLine> Chosen => _lines.Where(line => line.IsSelected && line.Route != AddRoute.Checking && line.Route != AddRoute.None).ToList();

    /// <summary>The text changed. Nothing happens until it has stopped changing for a moment.</summary>
    public void SetText(string? text)
    {
        if (_closed) return;
        _pendingText?.Cancel();
        _textPending = true;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _pendingText = pending;
        _ = ApplyAfterDebounceAsync(text, pending.Token);
    }

    /// <summary>For text that was already there when the dialog opened.</summary>
    public void SetTextNow(string? text)
    {
        _pendingText?.Cancel();
        ApplyInputs(LinkExtractor.Inputs(text));
    }

    public void SetInputs(IEnumerable<string> inputs)
    {
        _pendingText?.Cancel();
        ApplyInputs(inputs.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>A line whose answer is already in hand, as from Add URL's own check.</summary>
    public void Seed(string input, RemoteClassifyResult result)
    {
        var line = new AddLine(input) { Result = result, IsListed = _isListed(input) };
        Default(line, raise: false);
        _lines.Add(line);
        Number();
        LinesChanged?.Invoke();
    }

    /// <summary>Asks again. Whatever was chosen about the old answer no longer counts.</summary>
    public void Retry(AddLine line)
    {
        if (_closed || !_lines.Contains(line)) return;
        line.Result = null;
        line.DirectChosen = false;
        line.SelectionTouched = false;
        Default(line);
        Enqueue(line);
    }

    public void Select(AddLine line, bool selected)
    {
        if (!CanSelect(line)) selected = false;
        line.IsSelected = selected;
        line.SelectionTouched = true;
        LineChanged?.Invoke(line);
    }

    public void SelectAll(bool selected)
    {
        foreach (var line in _lines) Select(line, selected);
    }

    /// <summary>What a click on Select all does: everything goes off only when everything is on. From mixed, it all goes on.</summary>
    public void ToggleAll() => SelectAll(SelectAllState(_lines) != true);

    /// <summary>The explicit choice for a link that can't be told apart from one a provider handles.</summary>
    public void ChooseDirect(AddLine line, bool direct)
    {
        if (!line.CantTell || !LinkExtractor.IsWebLink(line.Input)) direct = false;
        line.DirectChosen = direct;
        line.SelectionTouched = false;
        Default(line);
    }

    public static bool CanSelect(AddLine line) => line.Route != AddRoute.None;

    /// <summary>The Select all box: on when every selectable line is ticked, off when none is, and mixed (null) when only some are.</summary>
    public static bool? SelectAllState(IEnumerable<AddLine> lines)
    {
        var selectable = lines.Where(CanSelect).ToList();
        var ticked = selectable.Count(line => line.IsSelected);
        if (ticked == 0) return false;
        return ticked == selectable.Count ? true : null;
    }

    private async Task ApplyAfterDebounceAsync(string? text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _post(() =>
        {
            if (!cancellationToken.IsCancellationRequested) ApplyInputs(LinkExtractor.Inputs(text));
        });
    }

    private void ApplyInputs(List<string> inputs)
    {
        if (_closed) return;
        var wasPending = _textPending;
        _textPending = false;
        var known = _lines.ToDictionary(line => line.Input, StringComparer.Ordinal);
        var next = new List<AddLine>(inputs.Count);
        var fresh = new List<AddLine>();
        foreach (var input in inputs)
        {
            if (!known.TryGetValue(input, out var line))
            {
                line = new AddLine(input) { IsListed = _isListed(input) };
                if (_focus == AddFocus.Magnet && !LinkExtractor.IsMagnet(input))
                {
                    line.Refusal = "This isn't a magnet. Use Add Links for other links.";
                }

                Default(line, raise: false);
                if (line.IsChecking) fresh.Add(line);
            }

            next.Add(line);
        }

        var same = next.SequenceEqual(_lines);
        _lines.Clear();
        _lines.AddRange(next);
        Number();

        // Text that comes to the same lines still has to say so: Add waited for it.
        if (!same || wasPending) LinesChanged?.Invoke();
        foreach (var line in fresh) Enqueue(line);
    }

    private void Number()
    {
        for (var i = 0; i < _lines.Count; i++) _lines[i].Position = _lines.Count > 1 ? i + 1 : 0;
    }

    private void Default(AddLine line, bool raise = true)
    {
        if (!line.SelectionTouched)
        {
            line.IsSelected = line.Route switch
            {
                AddRoute.Checking or AddRoute.FolderLink => true,
                AddRoute.Provider => !line.HosterLooksUnusable,
                AddRoute.Direct => !(line.IsListed && _skipListed),
                _ => false,
            };
        }
        else if (!CanSelect(line))
        {
            line.IsSelected = false;
        }

        if (raise) LineChanged?.Invoke(line);
    }

    private void Enqueue(AddLine line)
    {
        var start = false;
        lock (_queueSync)
        {
            _queue.Enqueue(line);
            if (!_working)
            {
                _working = true;
                start = true;
            }
        }

        if (start) _ = Task.Run(() => WorkAsync(_closing.Token));
    }

    /// <summary>Ends by itself when there is nothing left to ask, so it never holds a connection for no reason.</summary>
    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        IRemoteClassifier? classifier = null;
        try
        {
            classifier = _open();
            while (TryNext(out var line))
            {
                RemoteClassifyResult result;
                try
                {
                    result = await classifier.ClassifyAsync(line.Input, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_queueSync) _working = false;
                    return;
                }

                _post(() => Complete(line, result));
                if (classifier.Broken)
                {
                    // It would be the same for every other line, so each would wait to be told for no reason.
                    while (TryNext(out var next)) _post(() => Complete(next, new RemoteClassifyResult { Error = result.Error }));
                    return;
                }
            }
        }
        finally
        {
            if (classifier is not null) await classifier.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool TryNext(out AddLine line)
    {
        lock (_queueSync)
        {
            if (_queue.TryDequeue(out line!)) return true;
            _working = false;
            return false;
        }
    }

    private void Complete(AddLine line, RemoteClassifyResult result)
    {
        if (_closed || !_lines.Contains(line) || !line.IsChecking) return;
        line.Result = result;
        Default(line);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _closing.Cancel();
        _closing.Dispose();
    }

    public override string ToString() => "[add lines]";
}

/// <summary>What happens to one entry of an imported download list.</summary>
internal enum ImportWay
{
    /// <summary><c>job.add</c>, with everything the file says about it.</summary>
    Direct,
    /// <summary>The add dialog, which offers what it can go through or asks what to do with it.</summary>
    Review,
    /// <summary>It can't be added any way at all, and the Host has said why.</summary>
    Refused,
}

/// <summary>Sorts an imported list by the same answers and the same rules as the lines of Add Links.</summary>
internal static class ImportRouting
{
    /// <param name="input">The entry's address.</param>
    /// <param name="result">Null when no provider could take it, so nothing was asked.</param>
    public static ImportWay WayFor(string input, RemoteClassifyResult? result)
    {
        if (result is null) return ImportWay.Direct;
        var line = new AddLine(input) { Result = result };
        return line.Route switch
        {
            AddRoute.Direct => ImportWay.Direct,
            AddRoute.None when !line.CantTell => ImportWay.Refused,
            _ => ImportWay.Review,
        };
    }

    /// <summary>One connection, one call at a time, so a long list doesn't read a whole snapshot per entry. A lost connection answers the rest as failed.</summary>
    public static async Task<Dictionary<string, RemoteClassifyResult>> ClassifyAllAsync(IEnumerable<string> inputs, Func<IRemoteClassifier> open, CancellationToken cancellationToken = default)
    {
        var answers = new Dictionary<string, RemoteClassifyResult>(StringComparer.Ordinal);
        await using var classifier = open();
        foreach (var input in inputs)
        {
            if (!answers.ContainsKey(input)) answers[input] = await classifier.ClassifyAsync(input, cancellationToken);
        }

        return answers;
    }
}

/// <summary>What a line of the add dialog shows, and what it lets the user do.</summary>
/// <param name="Word">The kind of input, in a word or two.</param>
/// <param name="CanChooseDirect">A link nobody could classify: offer an explicit direct download. Never true for a hoster link.</param>
/// <param name="CanTakeLinkPassword">Goes to a site through the provider, and such a site can want a password for the link.</param>
internal sealed record AddLineView(
    string Name,
    string Word,
    string Detail,
    bool IsProblem,
    bool CanSelect,
    bool CanRetry,
    bool CanChooseDirect,
    bool CanTakeLinkPassword,
    string ToolTip,
    string AccessibleName);

internal static partial class RemoteText
{
    public const string ClassifyLost = "The background Host didn't answer, so this wasn't checked. Nothing was sent. Try again.";

    /// <summary>A lost classify call changed nothing, so the words about having possibly gone through don't apply.</summary>
    public static string ClassifyFailure(string error) => error is HostLost or HostTooSlow ? ClassifyLost : error;

    /// <summary>For a command that goes straight to a direct request, where the note would otherwise never be seen.</summary>
    public static string CheckIncompleteWords(RemoteClassifyResult result, string provider)
    {
        var note = UrlRedactor.HideAddresses(result.Note);
        if (string.IsNullOrWhiteSpace(note)) note = $"This app couldn't fully check whether {provider} handles this link.";
        return $"{note} Download it directly only if you know it isn't one of those sites.";
    }

    /// <param name="fallbackProvider">The account's name, for a line the Host named no provider for.</param>
    public static AddLineView AddLineWords(AddLine line, string fallbackProvider)
    {
        var result = line.Result;
        var provider = string.IsNullOrWhiteSpace(result?.ProviderName) ? fallbackProvider : result.ProviderName;
        var name = AddLineName(line);
        var details = new List<string>();
        var word = "Checking…";
        var problem = false;
        var retry = false;
        var direct = false;
        var linkPassword = false;
        if (line.Refusal is { } refusal)
        {
            word = "Unsupported";
            details.Add(refusal);
            problem = true;
        }
        else if (result is { Ok: false } || result is { Kind: "undetermined" })
        {
            word = "Can't tell";
            details.Add(Failure(result.Ok ? result.Reason : result.Error, null, provider));
            AddNote(details, result.Note);
            direct = LinkExtractor.IsWebLink(line.Input);
            if (direct) details.Add("So it isn't treated as an ordinary link unless you say so.");
            problem = true;
            retry = true;
        }
        else if (result is not null)
        {
            switch (result.Kind)
            {
                case "folderLink":
                    word = "Folder link";
                    // Its files are chosen on its row once it is listed, as for anything else that asks, so nothing is said of them here.
                    details.Add($"Refers to an item on your {provider} account.");
                    AddNote(details, result.Note);
                    break;
                case "magnet":
                    word = "Magnet";
                    details.Add($"Goes through {provider}.");
                    AddNote(details, result.Note);
                    break;
                case "torrentFile":
                    word = "Torrent file";
                    details.Add($"Goes through {provider}.");
                    AddNote(details, result.Note);
                    break;
                case "hosterLink":
                    word = $"Hoster through {provider}";
                    details.AddRange(HosterDetails(result, provider));
                    AddNote(details, result.Note);
                    problem = line.HosterLooksUnusable;
                    linkPassword = true;
                    break;
                case "providerDirectLink":
                    word = "Direct provider link";
                    AddNote(details, result.Note);
                    break;
                case "ordinaryLink":
                    word = "Ordinary link";
                    if (line.IsListed) details.Add("Already in the list");
                    AddNote(details, result.Note);
                    break;
                default:
                    word = "Unsupported";
                    details.Add(string.IsNullOrWhiteSpace(result.Reason) ? "This app can't use this." : UrlRedactor.HideAddresses(result.Reason));
                    problem = true;
                    break;
            }
        }

        if (line is { DirectChosen: true, CantTell: true })
        {
            details.Clear();
            details.Add($"It will download directly, not through {provider}, because you chose that.");
            problem = false;
        }

        var detail = string.Join(" ", details);
        var spoken = detail.Length > 0 ? $"{name}, {word}, {detail}" : $"{name}, {word}";
        var tip = result?.Kind == "ordinaryLink" ? UrlRedactor.Redact(line.Input) : spoken;
        return new AddLineView(name, word, detail, problem, AddLineSession.CanSelect(line), retry, direct, linkPassword, tip, spoken);
    }

    private static void AddNote(List<string> details, string? note)
    {
        if (!string.IsNullOrWhiteSpace(note)) details.Add(UrlRedactor.HideAddresses(note));
    }

    /// <summary>
    /// An address for a read-only box. A link the Host says holds an account key shows its host alone,
    /// because the key can be anywhere in it and only the Host knows what it looks like.
    /// </summary>
    public static string AddressToShow(string url, RemoteClassifyResult? result)
    {
        if (result?.Kind == "providerDirectLink")
        {
            return Clean(result.DisplayHost, Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost : "This link");
        }

        return ShownAddress.For(url);
    }

    /// <summary>
    /// The name a server gave, for showing and for the name box. Without a name of its own the engine uses the
    /// address's last part, and in a direct link that can be the key, so a name found in the address is left for
    /// the Host to settle. Only letters and digits are compared, because the engine cleans a name before giving it.
    /// </summary>
    public static string? ServerNameToShow(string? name, string url, RemoteClassifyResult? result)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (result?.Kind != "providerDirectLink" && !ProviderLinks.IsProviderLink(url)) return name;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var address = LettersAndDigits(Uri.UnescapeDataString(uri.PathAndQuery));
        return address.Contains(LettersAndDigits(name), StringComparison.OrdinalIgnoreCase) ? null : name;

        static string LettersAndDigits(string text) => string.Concat(text.Where(char.IsLetterOrDigit));
    }

    /// <summary>Why an ordinary add dialog opened although a key is saved. Null when that isn't why.</summary>
    public static string? UnreadableKeyNotice(IEnumerable<RemoteAccountDto> accounts)
    {
        var account = accounts.FirstOrDefault(candidate => candidate.KeyUnreadable);
        if (account is null) return null;
        var name = ProviderName(account);
        return $"The saved {name} API key can't be read on this Windows account, so links here aren't going through {name}. Enter the key again in Options to use it.";
    }

    public static string ImportSummary(int accepted, int total, int failed, string? firstError, int toReview)
    {
        var added = $"Added {accepted} of {total} downloads{(toReview > 0 ? " directly" : string.Empty)}";
        var outcome = added + (failed > 0 ? $"; {failed} failed. {firstError}" : ".");
        if (toReview == 0) return outcome;
        var entries = toReview == 1 ? "1 entry isn't" : $"{toReview} entries aren't";
        var them = toReview == 1 ? "it" : "them";
        return $"{outcome} {entries} added yet: the next dialog shows what can be done with {them}. Saved names, folders and checksums from the file don't carry over to {them}.";
    }

    /// <summary>The import's outcome when the dialog for its remaining entries couldn't open, so nothing promises that dialog.</summary>
    public static string ImportReviewNotOpened(int accepted, int total, int failed, int toReview)
    {
        var added = $"Added {accepted} of {total} downloads directly{(failed > 0 ? $"; {failed} failed" : string.Empty)}.";
        var entries = toReview == 1 ? "1 entry wasn't" : $"{toReview} entries weren't";
        var them = toReview == 1 ? "it" : "them";
        return $"{added} {entries} added, because another dialog was open. Add {them} with Add Links.";
    }

    public static string ProviderOffer(string provider)
        => $"{provider} handles this site, so this link can go through your account. It can also download directly from the site instead.";

    public static string RefreshRefused(string provider)
        => $"{provider} handles this link, so it can't replace this download's address. The address wasn't changed. Use Add Links to send it through {provider}.";

    /// <summary>Only what the provider itself said about the site. Nothing here says whether the file is cached: adding it finds that out.</summary>
    private static IEnumerable<string> HosterDetails(RemoteClassifyResult result, string provider)
    {
        if (result.HosterAvailable == false) yield return $"{provider} says this site isn't available right now, so adding it will probably fail.";
        else if (result.HosterAvailable == true) yield return $"{provider} says this site is available.";
        if (!string.IsNullOrWhiteSpace(result.HosterNote))
        {
            var note = UrlRedactor.HideAddresses(result.HosterNote).TrimEnd();
            yield return note.EndsWith('.') || note.EndsWith('!') || note.EndsWith('?') ? note : $"{provider} notes: {note}.";
        }

        var limits = new List<string>();
        if (result.HosterDailyLinksLeft is { } links)
        {
            limits.Add(links switch { 0 => "no links left today", 1 => "1 link left today", _ => $"{links} links left today" });
        }

        if (result.HosterDailyBytesLeft is { } bytes) limits.Add(bytes > 0 ? $"{Bytes(bytes)} left today" : "no data left today");
        if (result.HosterLargestFile is { } largest) limits.Add($"largest file {Bytes(largest)}");
        if (limits.Count > 0) yield return $"Limits {provider} gives for this site: {string.Join(", ", limits)}.";
    }

    /// <summary>Host names and file names only. The rest of an address can be a key, and is never shown here.</summary>
    private static string AddLineName(AddLine line)
    {
        var input = line.Input;
        var result = line.Result;
        if (LinkExtractor.IsMagnet(input)) return Clean(result?.Name, "Magnet link");
        if (LinkExtractor.IsTorrentPath(input)) return Clean(Path.GetFileName(input), "Torrent file");

        var host = Uri.TryCreate(input, UriKind.Absolute, out var uri) ? uri.IdnHost : "This link";
        if (result?.Kind == "ordinaryLink" && uri is not null)
        {
            var segment = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
            return segment.Length > 0 ? UrlRedactor.HideAddresses(segment) : host;
        }

        if (!string.IsNullOrWhiteSpace(result?.Name)) return UrlRedactor.HideAddresses(result.Name);
        if (result is { Kind: "folderLink", ItemId.Length: > 0 }) return $"{KindSingular(result.ItemKind)} {result.ItemId}";
        var shown = Clean(result?.DisplayHost, host);
        return line.Position > 0 ? $"{shown} (link {line.Position})" : shown;
    }

    /// <summary>The line of status under the list.</summary>
    public static (string Text, bool IsProblem) AddSummary(IReadOnlyList<AddLine> lines, AddFocus focus, bool hasText)
    {
        if (lines.Count == 0)
        {
            return (hasText
                ? focus == AddFocus.Magnet ? "No magnet was found in this text." : "No links were found in this text."
                : string.Empty, hasText);
        }

        var checking = lines.Count(line => line.IsChecking);
        if (checking > 0) return ($"Checking {checking} of {lines.Count}…", false);
        var chosen = lines.Count(line => line.IsSelected);
        var stuck = lines.Count(line => line.Route == AddRoute.None);
        return (stuck == 0
            ? $"{chosen} of {lines.Count} selected."
            : $"{chosen} of {lines.Count} selected. {stuck} can't be added as {(stuck == 1 ? "it is" : "they are")}.", false);
    }

    /// <summary>The words after an add that went through, empty when nothing new was added. The rows on the list say the rest.</summary>
    public static string AddedWords(int downloads, int items, string provider)
    {
        var parts = new List<string>(2);
        if (downloads > 0) parts.Add(downloads == 1 ? "1 download" : $"{downloads} downloads");
        if (items > 0) parts.Add(items == 1 ? $"1 item for {provider}" : $"{items} items for {provider}");
        return parts.Count == 0 ? string.Empty : $"Added {string.Join(" and ", parts)}.";
    }

    /// <summary>A line of an add that was refused, with the reason. Addresses in the reason are taken out, as everywhere.</summary>
    public static string Refused(string name, string? error, string? provider = null) => $"{name}: {Failure(error, null, provider)}";

    /// <summary>
    /// For something added again, by the name its row has, so it can be found: a folder link's line only knows the
    /// item's number. Never "added": it was on the list already, and the Host's note says what of the new request it
    /// didn't use.
    /// </summary>
    public static string AlreadyListed(RemoteJobDto? listed, string lineName, string? note)
        => AlreadyListed(listed is null ? lineName : JobName(listed), note);

    /// <summary>For something added again. Never "added": it was on the list already, and the Host's note says what of the new request it didn't use.</summary>
    public static string AlreadyListed(string name, string? note)
    {
        var said = string.IsNullOrWhiteSpace(note) ? null : UrlRedactor.HideAddresses(note).Trim();
        return said is null || !said.Contains("already on the list", StringComparison.OrdinalIgnoreCase)
            ? $"{name}: already on the list.{(said is null ? string.Empty : $" {said}")}"
            : $"{name}: {said}";
    }

    /// <summary>
    /// What a whole add comes to when it needs saying: everything that went wrong or was there already, each on
    /// its own line and none left out, after what was added.
    /// </summary>
    /// <param name="added">From <see cref="AddedWords"/>.</param>
    public static string AddReport(string added, IReadOnlyList<string> messages, IReadOnlyList<string> problems)
    {
        var lines = new List<string>(messages);
        if (added.Length > 0) lines.Add(added);
        lines.AddRange(problems);
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Which files a magnet or a .torrent file takes, settled in the add dialog and straight after it. A .torrent file
/// lists its own files, and a cached magnet's come from the provider's cache. Anything else can only be chosen once
/// the provider has it: left alone, an uncached one takes all, unless "Let me choose files when ... is done" says wait.
/// </summary>
internal static class AddChoices
{
    /// <summary>The only inputs whose files can be known before they are added, so the only ones worth asking the Host about.</summary>
    public static bool WantsFiles(string input) => LinkExtractor.IsMagnet(input) || LinkExtractor.IsTorrentPath(input);

    /// <summary>The files to choose from straight after Add: known now, with more than one that can be chosen. Null leaves it to the Host.</summary>
    public static IReadOnlyList<RemoteFileDto>? FilesToChoose(AddLine line)
        => line is { Route: AddRoute.Provider, Result: { Kind: "magnet" or "torrentFile", Files: { Count: > 1 } files } } &&
           RemoteFileTree.Build(files).ChoosableCount > 1
            ? files
            : null;

    /// <summary>
    /// Where "Let me choose files when ... is done" means something: a chosen magnet or .torrent file whose files
    /// aren't known yet, and that isn't known to be cached. A cached one is asked about by itself.
    /// </summary>
    public static bool ChooseLaterApplies(AddLine line)
        => line is { IsSelected: true, Route: AddRoute.Provider, Result: { Kind: "magnet" or "torrentFile", Files: null } result } && result.Cached != true;

    public static bool ChooseLaterApplies(IEnumerable<AddLine> lines) => lines.Any(ChooseLaterApplies);

    /// <summary>With no file dialog: wait for a choice if the box says so and it applies, and otherwise the Host's own rule.</summary>
    public static AddFileChoice? Default(AddLine line, bool chooseLater) => chooseLater && ChooseLaterApplies(line) ? AddFileChoice.Later : null;
}

/// <summary>The <c>selection</c> remote.add is sent, with the files that go with it.</summary>
internal sealed record AddFileChoice(string Selection, IReadOnlyList<string>? FileIds = null, IReadOnlyList<RemoteFilePathDto>? FilePaths = null)
{
    public static AddFileChoice Later { get; } = new("ask");

    public static AddFileChoice All { get; } = new("all");

    /// <summary>
    /// From the file dialog. A magnet's files are the provider's, so they go by id. A .torrent file's are its own and
    /// have no ids the provider knows, so they go by path and size, for the Host to find in the provider's list.
    /// </summary>
    public static AddFileChoice From(AddLine line, RemoteFileChoice choice)
    {
        if (choice.All) return All;
        if (line.Result?.Kind != "torrentFile") return new("chosen", FileIds: choice.FileIds.ToList());
        var chosen = choice.FileIds.ToHashSet(StringComparer.Ordinal);
        var paths = (line.Result.Files ?? [])
            .Where(file => chosen.Contains(file.FileId) && !string.IsNullOrEmpty(file.RelativePath))
            .Select(file => new RemoteFilePathDto { Path = file.RelativePath!, Size = file.Size })
            .ToList();
        return new("chosen", FilePaths: paths);
    }

    public override string ToString() => $"[files: {Selection}]";
}

/// <summary>What the Host is asked to add. Adding is the decision, so each request carries the go-ahead and nothing waits for a second yes.</summary>
internal static class AddRequests
{
    /// <param name="files">Null leaves the choice of files to the Host.</param>
    public static RemoteAddRequestDto ForLine(AddLine line, string? destination, int queueId, bool startLater, string? linkPassword, AddFileChoice? files = null) => new()
    {
        Input = line.Input,
        Destination = destination,
        QueueId = queueId,
        StartMode = startLater ? "later" : "startNow",
        LinkPassword = linkPassword,
        ApproveUncached = true,
        Selection = files?.Selection,
        FileIds = files?.FileIds?.ToList(),
        FilePaths = files?.FilePaths?.ToList(),
    };

    /// <summary>
    /// A folder link names an item already on the account. It is added to ask for its files, so they are chosen on its
    /// row once the Host has the item, and one that holds a single file just downloads. Null when the line isn't one.
    /// </summary>
    public static RemoteAddRequestDto? ForFolderLink(AddLine line, string? destination, int queueId, bool startLater)
        => line is { Route: AddRoute.FolderLink, Result: { Provider: { } provider, ItemKind: { } kind, ItemId: { } itemId } }
            ? new()
            {
                Provider = provider,
                ItemKind = kind,
                ItemId = itemId,
                Destination = destination,
                QueueId = queueId,
                StartMode = startLater ? "later" : "startNow",
                ApproveUncached = true,
                Selection = AddFileChoice.Later.Selection,
            }
            : null;

    public static RemoteContainerAddRequest ForContainer(Guid previewId, IReadOnlyList<int> entryIds, string? folder, int queueId) => new()
    {
        PreviewId = previewId,
        EntryIds = [.. entryIds],
        Destination = folder,
        StartMode = "startNow",
        QueueId = queueId,
        ApproveUncached = true,
    };
}
