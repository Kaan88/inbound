using System.Net;
using System.Text.Json;
using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// The key from the environment, and what the tests share. Nothing here may reach test output: a
/// direct link holds the key, and the account's names are the owner's. Tests compare and report
/// counts, and <see cref="Scrub"/> is for anything that has to be said in words.
/// </summary>
internal static class LiveAccount
{
    public const string ReadTrait = "TorBoxLive";
    public const string SubmitTrait = "TorBoxLiveSubmit";
    public const string KeyVariable = "TORBOX_API_KEY";
    public const string SubmitVariable = "TORBOX_LIVE_SUBMIT";

    public const string NoKey = $"{KeyVariable} is not set, so this live TorBox test did not run. Set it for this one process to run it.";
    public const string NoSubmit = $"This test sends something to the account. Set {SubmitVariable}=1 as well as {KeyVariable} to run it.";

    public static string? Key
    {
        get
        {
            var key = Environment.GetEnvironmentVariable(KeyVariable);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
    }

    public static readonly HttpClient Http = MakeHttp();

    public static TorBoxClient Client(string? key = null)
    {
        var used = key ?? Key;
        return new TorBoxClient(Http, () => used);
    }

    public static string Scrub(string? text)
    {
        var masked = new RemoteKeyPattern(Key).Mask(text ?? string.Empty);
        return UrlRedactor.HideAddresses(masked);
    }

    private static readonly Lazy<Task<IReadOnlyList<RemoteItem>>> TorrentList =
        new(() => Client().ListAsync(RemoteItemKind.Torrent, 0, 1000, fresh: true));
    private static readonly Lazy<Task<IReadOnlyList<RemoteItem>>> WebList =
        new(() => Client().ListAsync(RemoteItemKind.WebDownload, 0, 1000, fresh: true));

    public static Task<IReadOnlyList<RemoteItem>> Torrents => TorrentList.Value;
    public static Task<IReadOnlyList<RemoteItem>> WebDownloads => WebList.Value;

    public static async Task<IReadOnlyList<RemoteItem>> EverythingAsync() =>
        [.. await Torrents.ConfigureAwait(false), .. await WebDownloads.ConfigureAwait(false)];

    /// <summary>Smallest first. Only what the provider says can be fetched.</summary>
    public static async Task<List<(RemoteItem Item, RemoteFile File)>> FetchableFilesAsync()
    {
        var everything = await EverythingAsync().ConfigureAwait(false);
        return everything
            .Where(item => item.Stage == RemoteStage.Ready)
            .SelectMany(item => item.Files.Select(file => (Item: item, File: file)))
            .Where(pair => pair.File.Size > 0 && !pair.File.Infected)
            .OrderBy(pair => pair.File.Size)
            .ToList();
    }

    /// <summary>The API's answer as it is, for what the client deliberately doesn't show. The caller disposes the document.</summary>
    public static async Task<(HttpStatusCode Status, JsonDocument? Body)> RawAsync(string relative, string? bearer = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TorBoxClient.DefaultBaseAddress, relative));
        var used = bearer == string.Empty ? Key : bearer;
        if (used is not null) request.Headers.Authorization = new("Bearer", used);
        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        try
        {
            return (response.StatusCode, JsonDocument.Parse(text));
        }
        catch (JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    /// <summary>A direct link's answer, with its address kept out of whatever goes wrong.</summary>
    public static async Task<HttpResponseMessage> CdnAsync(Uri link, string? range = null, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        try
        {
            return await (http ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("The direct link could not be read: " + Scrub(ex.Message));
        }
    }

    /// <summary>The same link with another query. The result is as secret as the link.</summary>
    public static Uri WithQuery(Uri link, string query) => new UriBuilder(link) { Query = query }.Uri;

    private static HttpClient MakeHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 16,
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/0.1");
        return http;
    }
}

/// <summary>Reads only. Skips itself, saying why, when no key is set.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveAccount.Key is null) Skip = LiveAccount.NoKey;
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (LiveAccount.Key is null) Skip = LiveAccount.NoKey;
    }
}

/// <summary>Sends something to the account, so it needs a second switch on top of the key.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveSubmitFactAttribute : FactAttribute
{
    public LiveSubmitFactAttribute()
    {
        if (LiveAccount.Key is null) Skip = LiveAccount.NoKey;
        else if (Environment.GetEnvironmentVariable(LiveAccount.SubmitVariable) != "1") Skip = LiveAccount.NoSubmit;
    }
}
