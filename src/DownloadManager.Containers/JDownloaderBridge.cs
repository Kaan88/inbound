using System.Text;

namespace DownloadManager.Containers;

public enum JDownloaderBridgeStatus
{
    /// <summary>JDownloader accepted the request. The links are in JDownloader, not in this app.</summary>
    Sent,
    NotRunning,
    /// <summary>JDownloader refused, which includes the user declining its dialog.</summary>
    Rejected,
    /// <summary>No answer in time. The dialog may still be open, so the links may still arrive in JDownloader.</summary>
    TimedOut,
    InvalidInput,
    Failed,
}

/// <summary>Outcome of a hand-off. The message never quotes a link, a path or a password.</summary>
public sealed record JDownloaderBridgeResult(JDownloaderBridgeStatus Status, string Message);

/// <summary>
/// Optional, one-way hand-off to a JDownloader already running on this machine, over its Click'n'Load interface.
/// Nothing happens until a method is called, and normal operation never depends on it. Links do not come back from
/// this route. JDownloader's deprecated API on port 3128 could return them, but that needs a setting changed and goes
/// around JDownloader's link protection, so it is deliberately not used.
/// The routes were read from JDownloader's public source and have not been run against a live JDownloader.
/// </summary>
public sealed class JDownloaderBridge : IDisposable
{
    public const string LinksDoNotComeBack =
        "JDownloader will ask you to confirm, and the links stay in JDownloader: they do not come back to this app.";

    public const int Port = 9666;

    private const string AliveRoute = "flash/";
    private const string AddLinksRoute = "flash/add";
    private const string AddDlcRoute = "flash/addcrypted";
    private const string AliveAnswer = "JDownloader";
    private const string SuccessAnswer = "success";
    private const string DlcExtension = ".dlc";
    private const string CcfExtension = ".ccf";
    private const int MaxAnswerBytes = 4096;
    private const int MaxLinksPerSend = 100_000;

    private static readonly Uri LoopbackAddress = new($"http://127.0.0.1:{Port}/");
    private static readonly string[] SendableSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeFtp, "magnet"];
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    // JDownloader holds the request open while its dialog waits for the user, so this is a person's reading time. The
    // CLI waits longer than this for the Host's reply (CliClient.ReplyTimeout).
    internal static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _client;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _answerTimeout;

    /// <param name="handler">For tests. The address is fixed either way: there is no way to point this at another host.</param>
    public JDownloaderBridge(HttpMessageHandler? handler = null, TimeSpan? probeTimeout = null, TimeSpan? answerTimeout = null)
    {
        _client = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: handler is null)
        {
            BaseAddress = LoopbackAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // An honest name is what JDownloader's dialog shows. Its other skip-the-dialog routes, a loopback /flashgot
        // Referer and the jd.randomnumber header, are never used: the point of this route is that the user is asked.
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Inbound Download Manager");

        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
    }

    // A proxy or a followed redirect would carry the links off this machine.
    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
    };

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            using var response = await _client.GetAsync(AliveRoute, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                && (await ReadAnswerAsync(response, timeout.Token).ConfigureAwait(false)).StartsWith(AliveAnswer, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands a local .dlc or .ccf file to JDownloader. A DLC's text is sent, as the interface expects. A CCF is named
    /// by its file URL, the same form JDownloader uses internally for a DLC, so JDownloader reads it from disk.
    /// </summary>
    public async Task<JDownloaderBridgeResult> SendFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return Invalid("Choose a file on this computer, using its full path.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (DlcExtension or CcfExtension))
        {
            return Invalid("Only .dlc and .ccf files can be sent to JDownloader.");
        }

        if (!File.Exists(path))
        {
            return Invalid("This file does not exist.");
        }

        if (extension == CcfExtension)
        {
            return ToFileUrl(path) is { } fileUrl
                ? await DeliverAsync(AddLinksRoute, [new("urls", fileUrl)], cancellationToken).ConfigureAwait(false)
                : Invalid("This file's path can't be written as an address JDownloader would read the same way.");
        }

        string dlcText;
        try
        {
            if (new FileInfo(path).Length > ContainerLimits.Default.MaxInputBytes)
            {
                return Invalid("This file is too large to be a DLC.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!DlcSupport.TryRecognise(bytes, out _))
            {
                return Invalid("This file does not have the shape of a DLC file.");
            }

            dlcText = Encoding.ASCII.GetString(ContainerText.SkipBom(bytes)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid("This file could not be opened.");
        }

        return await DeliverAsync(AddDlcRoute, [new("crypted", dlcText)], cancellationToken).ConfigureAwait(false);
    }

    /// <param name="archivePassword">A candidate archive password for these links. JDownloader treats this field the same way.</param>
    public async Task<JDownloaderBridgeResult> SendLinksAsync(
        IReadOnlyCollection<string> urls,
        string? packageName = null,
        string? archivePassword = null,
        CancellationToken cancellationToken = default)
    {
        if (urls is null || urls.Count == 0)
        {
            return Invalid("There are no links to send.");
        }

        if (urls.Count > MaxLinksPerSend)
        {
            return Invalid("There are too many links to send at once.");
        }

        foreach (var url in urls)
        {
            // JDownloader scans this field as text, so any whitespace could smuggle in another entry. file: is
            // excluded because it would have JDownloader read local paths that nobody chose.
            if (url is null
                || url.Any(char.IsWhiteSpace)
                || !ContainerText.IsUrl(url)
                || !SendableSchemes.Contains(url[..url.IndexOf(':')], StringComparer.OrdinalIgnoreCase))
            {
                return Invalid("One of the links is not a web address that can be sent.");
            }
        }

        if ((packageName is not null && ContainerText.HasControlCharacters(packageName))
            || (archivePassword is not null && ContainerText.HasControlCharacters(archivePassword)))
        {
            return Invalid("The package name or password holds characters that can't be sent.");
        }

        var fields = new List<KeyValuePair<string, string>> { new("urls", string.Join("\r\n", urls)) };
        if (!string.IsNullOrEmpty(packageName))
        {
            fields.Add(new("package", packageName));
        }

        if (!string.IsNullOrEmpty(archivePassword))
        {
            fields.Add(new("passwords", archivePassword));
        }

        return await DeliverAsync(AddLinksRoute, fields, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private async Task<JDownloaderBridgeResult> DeliverAsync(string route, IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return new JDownloaderBridgeResult(JDownloaderBridgeStatus.NotRunning, "JDownloader is not running on this computer, so nothing was sent.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_answerTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new FormUrlEncodedContent(fields) };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var accepted = response.IsSuccessStatusCode
                && (await ReadAnswerAsync(response, timeout.Token).ConfigureAwait(false)).StartsWith(SuccessAnswer, StringComparison.OrdinalIgnoreCase);

            return accepted
                ? new JDownloaderBridgeResult(JDownloaderBridgeStatus.Sent, $"Sent to JDownloader. {LinksDoNotComeBack}")
                : new JDownloaderBridgeResult(JDownloaderBridgeStatus.Rejected, "JDownloader did not accept this. You may have declined its confirmation, or it could not read what was sent.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new JDownloaderBridgeResult(JDownloaderBridgeStatus.TimedOut, $"JDownloader did not answer in time. Its confirmation may still be open. {LinksDoNotComeBack}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new JDownloaderBridgeResult(JDownloaderBridgeStatus.Failed, "The connection to JDownloader was lost, so it is not known whether this arrived.");
        }
    }

    private static async Task<string> ReadAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxAnswerBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
    }

    /// <summary>
    /// Built from escaped segments rather than handed to <see cref="Uri"/>, which reads a %41 in a file's name as an
    /// escape and so names another file. Null when the result still wouldn't lead back to this exact path.
    /// </summary>
    internal static string? ToFileUrl(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string url;
        if (OperatingSystem.IsWindows())
        {
            if (segments.Length < 2 || segments[0].Length != 2 || !char.IsAsciiLetter(segments[0][0]) || segments[0][1] != ':')
            {
                return null;
            }

            url = "file:/" + segments[0] + "/" + string.Join('/', segments.Skip(1).Select(Uri.EscapeDataString));
        }
        else
        {
            // A rooted Linux path splits into an empty first part. .NET on Linux can't read the short file:/ form back
            // (the check below would then refuse every path), so this is file:/// with an empty host, which Java reads
            // as the same file.
            if (segments.Length < 2 || segments[0].Length != 0)
            {
                return null;
            }

            url = "file:///" + string.Join('/', segments.Skip(1).Select(Uri.EscapeDataString));
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && string.Equals(parsed.LocalPath, path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), StringComparison.Ordinal)
            ? url
            : null;
    }

    private static JDownloaderBridgeResult Invalid(string message) => new(JDownloaderBridgeStatus.InvalidInput, message);
}
