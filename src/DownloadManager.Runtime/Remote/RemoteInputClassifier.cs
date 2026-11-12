using System.Diagnostics;
using System.Text.RegularExpressions;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

public enum RemoteInputKind
{
    /// <summary>A provider's reference to an item on the account.</summary>
    FolderLink,
    Magnet,
    TorrentFile,
    /// <summary>A file on a site the provider can fetch from.</summary>
    HosterLink,
    /// <summary>A direct link from the provider, holding the account key. It downloads as an ordinary job, with the key kept out of everything shown or saved.</summary>
    ProviderDirectLink,
    OrdinaryLink,
    /// <summary>
    /// A web link, with an account that could take it, and no list of sites to tell whether it does.
    /// Not an ordinary link: for all anyone knows it is a hoster, which never gets a direct request.
    /// </summary>
    Undetermined,
    Unsupported,
}

/// <summary>What a pasted string or chosen file is. Holds no link, so it is safe to log or send to a client.</summary>
public sealed record RemoteInputClassification
{
    public required RemoteInputKind Kind { get; init; }
    public string? Provider { get; init; }
    public string? ProviderName { get; init; }
    public RemoteItemKind? ItemKind { get; init; }
    /// <summary>Set for a folder link. Whether it exists is for the account's own list to say.</summary>
    public string? ItemId { get; init; }
    public string? Hash { get; init; }
    /// <summary>Claimed by the input, for display until the provider says otherwise.</summary>
    public string? Name { get; init; }
    public string? DisplayHost { get; init; }
    public string? HosterName { get; init; }
    /// <summary>The provider's current word on the hoster. A site that is down stays a hoster link.</summary>
    public bool? HosterAvailable { get; init; }
    public string? HosterNote { get; init; }
    /// <summary>Links left today on this site, as the provider last said. Null when it sets no limit.</summary>
    public int? HosterDailyLinksLeft { get; init; }
    /// <summary>Bytes left today on this site, as the provider last said. Null when it sets no limit.</summary>
    public long? HosterDailyBytesLeft { get; init; }
    /// <summary>The largest single file the provider takes from this site. Null when it sets no limit.</summary>
    public long? HosterLargestFile { get; init; }
    /// <summary>Why it can't be used, when <see cref="Kind"/> is unsupported or undetermined.</summary>
    public string? Reason { get; init; }
    /// <summary>Something worth saying about an input that can be used.</summary>
    public string? Note { get; init; }
    /// <summary>
    /// Called ordinary, but a listed site couldn't be checked against this link, and <see cref="Note"/> says so.
    /// That goes on for as long as the list does and says nothing about this address in particular, so each
    /// way of adding decides for itself. It isn't <see cref="RemoteInputKind.Undetermined"/>, which would stop
    /// every ordinary link. A stale list only leaves the note, because it is still a good guide.
    /// </summary>
    public bool CheckIncomplete { get; init; }

    /// <summary>Asked for with the files, for a magnet: whether the provider has it cached. Null when it couldn't say.</summary>
    public bool? Cached { get; init; }

    /// <summary>
    /// Asked for with the files: a .torrent file's own, or a cached magnet's from the provider. Null when they aren't
    /// known, or are too many to choose from here.
    /// </summary>
    public IReadOnlyList<RemoteFileView>? Files { get; init; }
}

/// <summary>What the classifier needs to know about one provider, all of it already fetched.</summary>
public sealed record RemoteClassifierProvider
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public RemoteCapabilities Capabilities { get; init; }
    /// <summary>Why the account can't be used. Null when it can. Without an account, hoster links stay ordinary links.</summary>
    public string? AccountProblem { get; init; }
    public IRemoteFolderLinkParser? FolderLinks { get; init; }
    public IRemoteSubmitter? Submitter { get; init; }
    public IReadOnlyList<RemoteHosterMatcher> Hosters { get; init; } = [];
    public string? HosterListProblem { get; init; }
    public IRemoteSecretMatcher? Secrets { get; init; }

    public bool CanTake(RemoteCapabilities capability) => AccountProblem is null && Capabilities.HasFlag(capability);

    public static RemoteClassifierProvider From(RemoteProviderEntry entry, IReadOnlyList<RemoteHosterMatcher>? hosters = null, string? hosterListProblem = null) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        Capabilities = entry.Capabilities,
        AccountProblem = entry.AccountProblem(),
        FolderLinks = entry.FolderLinks,
        Submitter = entry.Submitter,
        Hosters = hosters ?? [],
        HosterListProblem = hosterListProblem,
        Secrets = entry.Account,
    };
}

/// <summary>
/// One entry of a provider's hoster list with its pattern ready to use. The pattern comes from the
/// provider and is untrusted: one that won't compile or takes too long costs that hoster, not the app.
/// </summary>
public sealed class RemoteHosterMatcher
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const int LongestPattern = 4096;

    private readonly Regex? _pattern;
    private volatile bool _skip;

    private RemoteHosterMatcher(RemoteHoster hoster, Regex? pattern)
    {
        Hoster = hoster;
        _pattern = pattern;
    }

    public RemoteHoster Hoster { get; }

    /// <summary>A pattern that had to be left out, and no domains to go by instead, so this hoster can't be recognised right now.</summary>
    public bool CantBeChecked => _skip && Hoster.Domains.Count == 0;

    /// <returns>Null when nothing about the hoster can be used to recognise a link. Its domains still can if its pattern can't.</returns>
    public static RemoteHosterMatcher? Create(RemoteHoster hoster)
    {
        var domainsOnly = hoster.Domains.Count > 0 ? new RemoteHosterMatcher(hoster, null) : null;
        if (string.IsNullOrEmpty(hoster.Pattern) || hoster.Pattern.Length > LongestPattern)
        {
            return domainsOnly;
        }

        try
        {
            return new RemoteHosterMatcher(hoster, new Regex(hoster.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
        }
        catch (ArgumentException)
        {
            return domainsOnly;
        }
    }

    /// <summary>
    /// A hoster that lists domains is only ever asked about addresses on them: a pattern alone would
    /// let a private link that merely quotes a hoster's address, query and all, be sent to the provider.
    /// A hoster with no domains can't be held to that, so its pattern doesn't see the query.
    /// </summary>
    /// <param name="mayRunPattern">False once a paste has spent its time on patterns.</param>
    public bool Matches(string address, string host, bool mayRunPattern = true)
    {
        var hasDomains = Hoster.Domains.Count > 0;
        if (hasDomains && !Hoster.Domains.Any(domain =>
                host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_pattern is null || _skip)
        {
            return hasDomains;
        }

        if (!mayRunPattern)
        {
            return hasDomains;
        }

        try
        {
            return _pattern.IsMatch(hasDomains ? address : WithoutQuery(address));
        }
        catch (RegexMatchTimeoutException)
        {
            // Once is enough: 160 slow patterns would otherwise cost every paste many seconds.
            _skip = true;
            return hasDomains;
        }
    }

    private static string WithoutQuery(string address)
    {
        var end = address.IndexOfAny(['?', '#']);
        return end < 0 ? address : address[..end];
    }
}

/// <summary>
/// Sorts one input. Pure: everything it needs is in its arguments, and it reaches no network or
/// file. Later steps act on the kind it gives, and a hoster link never becomes a direct attempt.
/// </summary>
public static class RemoteInputClassifier
{
    public const int LongestInput = 32 * 1024;

    /// <summary>All patterns together, for one paste. A hostile list can't hold an IPC call for long.</summary>
    private static readonly TimeSpan LongestOnPatterns = TimeSpan.FromSeconds(2);

    /// <param name="localIsOrdinary">
    /// UI.B27: an address on this PC or its local network (<see cref="NetworkAddresses.IsLocalWebAddress(Uri)"/>) is an
    /// ordinary link, whatever a provider's list says. False only in tests, where a local server stands in for a hoster.
    /// </param>
    public static RemoteInputClassification Classify(string? text, IReadOnlyList<RemoteClassifierProvider> providers, bool localIsOrdinary = true)
    {
        var input = text?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return Unsupported("Nothing was entered.");
        }

        if (input.Length > LongestInput)
        {
            return Unsupported("This is too long to be a link.");
        }

        if (input.Any(char.IsControl))
        {
            return Unsupported("Enter one link at a time.");
        }

        if (input.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyMagnet(input, providers);
        }

        // Before anything opens it: looking at a file on a network path already sends this Windows
        // account's credentials to whatever host the text names.
        if (IsNetworkOrDevicePath(input))
        {
            return Unsupported("Only .torrent files on a drive of this computer can be added, not ones on a network path.");
        }

        if (IsLocalPath(input))
        {
            return Path.GetExtension(input).Equals(".torrent", StringComparison.OrdinalIgnoreCase)
                ? ForTorrents(providers, static provider => new RemoteInputClassification
                {
                    Kind = RemoteInputKind.TorrentFile,
                    Provider = provider.Id,
                    ProviderName = provider.DisplayName,
                    ItemKind = RemoteItemKind.Torrent,
                })
                : Unsupported("Only .torrent files can be added from this computer.");
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return Unsupported("This isn't a link this app can use.");
        }

        return uri.Scheme switch
        {
            "http" or "https" => ClassifyWeb(input, uri, providers, localIsOrdinary),
            // FTP was cut in 0.7.2.
            "ftp" or "ftps" => Unsupported(FtpAddresses.NotSupported),
            _ => Unsupported("Only http and https addresses and magnets can be added."),
        };
    }

    /// <summary>For a .torrent file already read. A malformed file gets its reason here, before anything is created.</summary>
    /// <param name="withFiles">Also the files it lists, to choose from before it is added.</param>
    public static RemoteInputClassification ClassifyTorrent(byte[] file, IReadOnlyList<RemoteClassifierProvider> providers, bool withFiles = false)
    {
        if (!TorrentMetadata.TryParse(file, out var metadata, out var problem))
        {
            return Unsupported(problem!);
        }

        return ForTorrents(providers, provider => new RemoteInputClassification
        {
            Kind = RemoteInputKind.TorrentFile,
            Provider = provider.Id,
            ProviderName = provider.DisplayName,
            ItemKind = RemoteItemKind.Torrent,
            Hash = metadata!.InfoHash,
            Name = RemoteText.Clean(metadata.Name, 200),
            Files = withFiles ? FilesToChoose(metadata, provider.DisplayName) : null,
        });
    }

    /// <summary>
    /// A .torrent file's own list, with ids that only say where each file is in it. What is chosen from this goes back
    /// by path and size, which is how the provider's files are matched to it once it lists them. None when the parse kept
    /// only the first files (<see cref="TorrentMetadata.FilesCut"/>), so a partial list is never offered as the whole.
    /// </summary>
    private static List<RemoteFileView>? FilesToChoose(TorrentMetadata metadata, string providerName)
        => !metadata.FilesCut && metadata.Files.Count is > 0 and <= RemoteJobCoordinator.MostFilesToChooseFrom
            ? metadata.Files.Select((file, index) => RemoteJobCoordinator.ToFileView(providerName, new RemoteFile
            {
                FileId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path = file.Path,
                Size = file.Length,
            })).ToList()
            : null;

    /// <summary>The info hash of a magnet, lower case hex, from either way of writing it. Null with the reason if none can be used.</summary>
    public static string? InfoHashOfMagnet(string magnet, out string? displayName, out string? problem)
    {
        displayName = null;
        problem = null;
        var query = magnet.IndexOf('?');
        if (query < 0)
        {
            problem = "This magnet has no content hash.";
            return null;
        }

        string? hash = null;
        var hasVersion2 = false;
        foreach (var pair in magnet[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = Decode(pair[..equals]);
            var value = Decode(pair[(equals + 1)..]);
            if (name.Equals("dn", StringComparison.OrdinalIgnoreCase))
            {
                displayName ??= RemoteText.Clean(value, 200);
            }
            else if (name.Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                {
                    hash ??= NormalizeInfoHash(value["urn:btih:".Length..]);
                }
                else if (value.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
                {
                    hasVersion2 = true;
                }
            }
        }

        if (hash is null)
        {
            problem = hasVersion2
                ? "This magnet has only a version 2 hash. Only magnets with a version 1 hash can be added, because that is how the item is found again."
                : "This magnet has no usable content hash.";
        }

        return hash;
    }

    private static RemoteInputClassification ClassifyMagnet(string magnet, IReadOnlyList<RemoteClassifierProvider> providers)
    {
        var hash = InfoHashOfMagnet(magnet, out var name, out var problem);
        return hash is null
            ? Unsupported(problem!)
            : ForTorrents(providers, provider => new RemoteInputClassification
            {
                Kind = RemoteInputKind.Magnet,
                Provider = provider.Id,
                ProviderName = provider.DisplayName,
                ItemKind = RemoteItemKind.Torrent,
                Hash = hash,
                Name = name,
            });
    }

    private static RemoteInputClassification ForTorrents(
        IReadOnlyList<RemoteClassifierProvider> providers,
        Func<RemoteClassifierProvider, RemoteInputClassification> build)
    {
        var usable = providers.FirstOrDefault(provider => provider.CanTake(RemoteCapabilities.Torrents));
        if (usable is not null)
        {
            return build(usable);
        }

        return Unsupported(providers.FirstOrDefault(provider => provider.Capabilities.HasFlag(RemoteCapabilities.Torrents))?.AccountProblem
            ?? "This app has no provider for torrents, and can't download them by itself.");
    }

    private static RemoteInputClassification ClassifyWeb(string input, Uri uri, IReadOnlyList<RemoteClassifierProvider> providers, bool localIsOrdinary)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var provider in providers)
        {
            if (provider.FolderLinks?.ParseFolderLink(input) is not { } parsed)
            {
                continue;
            }

            if (parsed.Reference is not { } reference)
            {
                return Unsupported(parsed.Problem ?? "This folder link can't be used.");
            }

            return provider.AccountProblem is { } problem
                ? Unsupported(problem)
                : new RemoteInputClassification
                {
                    Kind = RemoteInputKind.FolderLink,
                    Provider = provider.Id,
                    ProviderName = provider.DisplayName,
                    ItemKind = reference.Kind,
                    ItemId = reference.ItemId,
                    DisplayHost = host,
                };
        }

        if (ProviderLinks.IsMasked(input))
        {
            return Unsupported("This address has its key hidden, so it can't download anything. Paste the original link.");
        }

        if (ProviderLinks.IsProviderLink(input) || providers.Any(provider => provider.Secrets?.Holds(input) == true))
        {
            return new RemoteInputClassification
            {
                Kind = RemoteInputKind.ProviderDirectLink,
                DisplayHost = host,
                Note = "This link holds an account key. It will download, with the key kept out of everything shown or saved.",
            };
        }

        // UI.B27: no provider can fetch from this PC or its local network, so nothing is matched against its list.
        if (localIsOrdinary && NetworkAddresses.IsLocalWebAddress(uri))
        {
            return new RemoteInputClassification { Kind = RemoteInputKind.OrdinaryLink, DisplayHost = host };
        }

        string? listProblem = null;
        string? notChecked = null;
        var onPatterns = Stopwatch.StartNew();
        foreach (var provider in providers.Where(provider => provider.CanTake(RemoteCapabilities.WebDownloads)))
        {
            listProblem ??= provider.HosterListProblem;
            foreach (var matcher in provider.Hosters)
            {
                var mayRunPattern = onPatterns.Elapsed < LongestOnPatterns;
                if (matcher.Matches(input, host, mayRunPattern))
                {
                    // A site's own address names no file. A provider's cache can go by the exact text, so sending one
                    // can bring back whatever somebody once got for it: 9 GB of a stranger's file, when tried.
                    if (uri.AbsolutePath is "" or "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0)
                    {
                        return Unsupported($"This is the address of {RemoteText.Clean(matcher.Hoster.Name, 100) ?? "a site"}, not a link to a file.");
                    }

                    return new RemoteInputClassification
                    {
                        Kind = RemoteInputKind.HosterLink,
                        Provider = provider.Id,
                        ProviderName = provider.DisplayName,
                        ItemKind = RemoteItemKind.WebDownload,
                        Hash = provider.Submitter?.PredictHash(RemoteItemKind.WebDownload, input),
                        DisplayHost = host,
                        HosterName = RemoteText.Clean(matcher.Hoster.Name, 100),
                        HosterAvailable = matcher.Hoster.Available,
                        HosterNote = RemoteText.Clean(matcher.Hoster.Note, 200),
                        HosterDailyLinksLeft = matcher.Hoster.DailyLinkLimit is > 0 and var links ? Math.Max(0, links - (matcher.Hoster.DailyLinksUsed ?? 0)) : null,
                        HosterDailyBytesLeft = matcher.Hoster.DailyBandwidthLimit is > 0 and var bytes ? Math.Max(0, bytes - (matcher.Hoster.DailyBandwidthUsed ?? 0)) : null,
                        HosterLargestFile = matcher.Hoster.PerLinkSizeLimit is > 0 and var largest ? largest : null,
                    };
                }

                if (matcher.CantBeChecked || (!mayRunPattern && matcher.Hoster.Domains.Count == 0))
                {
                    notChecked ??= $"{provider.DisplayName} lists a site this app couldn't check this link against, so this link may be one it can handle.";
                }
            }
        }

        // No list at all, and one is wanted. A list that couldn't be refreshed is still a good guide.
        if (providers.FirstOrDefault(provider =>
                provider.CanTake(RemoteCapabilities.WebDownloads) && provider.Hosters.Count == 0 && provider.HosterListProblem is not null) is { } unsure)
        {
            return new RemoteInputClassification
            {
                Kind = RemoteInputKind.Undetermined,
                Provider = unsure.Id,
                ProviderName = unsure.DisplayName,
                DisplayHost = host,
                Reason = $"{unsure.DisplayName} could not be reached to check whether it handles this link, so nothing was sent. Try again in a minute.",
                Note = unsure.HosterListProblem,
            };
        }

        return new RemoteInputClassification
        {
            Kind = RemoteInputKind.OrdinaryLink,
            DisplayHost = host,
            Note = notChecked ?? listProblem,
            CheckIncomplete = notChecked is not null,
        };
    }

    /// <summary>
    /// A path that names another computer, or a Windows device. Whatever it ends in, it isn't opened.
    /// That includes the \\?\ and \??\ forms, the second of which counts as fully qualified and can name a share.
    /// </summary>
    public static bool IsNetworkOrDevicePath(string input)
        => input.Length >= 2 && input[0] is '\\' or '/' && input[1] is '\\' or '/' or '?';

    /// <summary>A rooted Windows path. A one-letter scheme is a drive, so this comes before any look at it as an address.</summary>
    private static bool IsLocalPath(string input)
    {
        try
        {
            return Path.IsPathFullyQualified(input);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? NormalizeInfoHash(string value)
    {
        if (value.Length == 40 && value.All(char.IsAsciiHexDigit))
        {
            return value.ToLowerInvariant();
        }

        return value.Length == 32 ? Base32ToHex(value) : null;
    }

    private static string? Base32ToHex(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return index == 20 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static RemoteInputClassification Unsupported(string reason)
        => new() { Kind = RemoteInputKind.Unsupported, Reason = reason };
}

internal static class RemoteText
{
    /// <summary>
    /// For words from a provider or from an input: no addresses, no control characters, one line, a
    /// sensible length. Everything is hidden before anything is cut, so a secret across the limit
    /// can't leave a part of itself behind.
    /// </summary>
    /// <param name="mask">Hides what only the caller knows about, such as the account key.</param>
    /// <param name="hide">Values that must not come back in any form, such as what was just submitted.</param>
    public static string? Clean(string? text, int longest, Func<string, string>? mask = null, IEnumerable<string?>? hide = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var bounded = text.Length <= UrlRedactor.LongestText ? text : text[..UrlRedactor.LongestText];
        foreach (var form in Forms(hide))
        {
            bounded = bounded.Replace(form, UrlRedactor.Mask, StringComparison.OrdinalIgnoreCase);
        }

        var hidden = UrlRedactor.HideAddresses(mask is null ? bounded : mask(bounded));
        var single = new string(hidden.Select(c => FileNameResolver.IsDeceptiveOrControl(c) ? ' ' : c).ToArray()).Trim();
        if (single.Length == 0)
        {
            return null;
        }

        return single.Length <= longest ? single : single[..longest];
    }

    /// <summary>A value as it may be quoted: as it is, percent-encoded, and an address without its scheme. Longest first.</summary>
    private static IEnumerable<string> Forms(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return [];
        }

        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            forms.Add(value);
            forms.Add(Uri.EscapeDataString(value));
            var scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                forms.Add(value[(scheme + 3)..]);
                forms.Add(Uri.EscapeDataString(value[(scheme + 3)..]));
            }

            // The same address with its fragment left off, as a site would see it.
            var fragment = value.IndexOf('#', StringComparison.Ordinal);
            if (fragment > 0)
            {
                forms.Add(value[..fragment]);
                forms.Add(Uri.EscapeDataString(value[..fragment]));
                if (scheme > 0 && scheme + 3 < fragment)
                {
                    forms.Add(value[(scheme + 3)..fragment]);
                }
            }
        }

        return forms.OrderByDescending(form => form.Length);
    }
}
