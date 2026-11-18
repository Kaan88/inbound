using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DownloadManager.Mobile.Updates;

public interface IAndroidUpdateFeed
{
    Task<AndroidUpdateManifest?> ReadAsync(CancellationToken ct);
    Task DownloadAsync(AndroidUpdateManifest manifest, string path, Action<long> progress, CancellationToken ct);
}

/// <summary>Public GitHub release assets only; bounded reads and explicit HTTPS redirects, no cookies or credentials.</summary>
public sealed class GithubAndroidFeed(HttpClient http) : IAndroidUpdateFeed
{
    public async Task<AndroidUpdateManifest?> ReadAsync(CancellationToken ct)
    {
        using var response = await GetAsync(new Uri(AndroidUpdateTrust.FeedUrl), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // first Android release not published yet
        response.EnsureSuccessStatusCode();
        using var memory = new MemoryStream();
        await CopyBoundedAsync(response, memory, 64 * 1024, null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(memory.ToArray(), AndroidUpdateJson.Default.AndroidUpdateManifest)
            ?? throw new InvalidDataException("The Android update information is empty.");
    }

    public async Task DownloadAsync(AndroidUpdateManifest manifest, string path, Action<long> progress, CancellationToken ct)
    {
        if (!AndroidUpdateTrust.IsReleaseUrl(manifest.ApkUrl)) throw new InvalidDataException("Update address refused.");
        using var response = await GetAsync(new Uri(manifest.ApkUrl), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != manifest.Size)
            throw new InvalidDataException("The update download has the wrong size.");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var bytes = await CopyBoundedAsync(response, output, manifest.Size, progress, ct).ConfigureAwait(false);
        if (bytes != manifest.Size) throw new InvalidDataException("The update download was incomplete.");
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedRedirect(address)) throw new InvalidDataException("Update redirect refused.");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Inbound-Android-Updater/1.0");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("Update redirect has no address.");
            address = new Uri(address, location);
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    private static bool AllowedRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/Kaan88/inbound/releases/", StringComparison.Ordinal));

    private static async Task<long> CopyBoundedAsync(HttpResponseMessage response, Stream output, long limit,
        Action<long>? progress, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[65536];
        long total = 0, reported = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > limit) throw new InvalidDataException("The update response is larger than allowed.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (total - reported >= 1024 * 1024) { progress?.Invoke(total); reported = total; }
        }
        progress?.Invoke(total);
        return total;
    }
}
