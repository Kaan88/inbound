using System.Security.Cryptography;
using DownloadManager.Core.Providers;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// These POST to the account, so they have their own trait and need a second switch. Each asks for
/// invented content with add_only_if_cached, which should create nothing. If something does get
/// created, the test removes it and fails.
/// </summary>
[Trait("Category", LiveAccount.SubmitTrait)]
public sealed class LiveSubmitTests(ITestOutputHelper output)
{
    private void Say(string text) => output.WriteLine(LiveAccount.Scrub(text));

    private static string InventedHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private async Task AsksOnlyIfCachedAndCreatesNothingAsync(RemoteItemKind kind, string link, string hash)
    {
        var client = LiveAccount.Client();
        RemoteProviderException? refused = null;
        try
        {
            var answer = await client.SubmitAsync(new RemoteSubmission { Kind = kind, Link = link, OnlyIfCached = true });
            if (answer.Ref is { } created)
            {
                var removed = await LiveAccount.RemoveUnlessKeptAsync(client, created);
                Assert.Fail(removed
                    ? "Asked only if cached, for content that can't be, and it created an item. It was removed."
                    : "Asked only if cached, for content that can't be, and it answered with an item that is kept or already gone. Nothing was removed.");
            }
        }
        catch (RemoteProviderException ex)
        {
            refused = ex;
        }

        Assert.NotNull(refused);
        Say($"kind {refused.Kind}, code {refused.ProviderCode}, words: {refused.Message}");
        Assert.Equal(RemoteFailureKind.NotCached, refused.Kind);

        var items = await client.ListAsync(kind, 0, 1000, fresh: true);
        Assert.DoesNotContain(items, item => string.Equals(item.Hash, hash, StringComparison.OrdinalIgnoreCase));
    }

    [LiveSubmitFact]
    public Task AnUncachedMagnetAskedOnlyIfCachedCreatesNothing()
    {
        var hash = InventedHex(20);
        return AsksOnlyIfCachedAndCreatesNothingAsync(RemoteItemKind.Torrent, $"magnet:?xt=urn:btih:{hash}&dn=invented", hash);
    }

    [LiveSubmitFact]
    public Task AnUncachedHosterLinkAskedOnlyIfCachedCreatesNothing()
    {
        var link = $"https://mega.nz/file/{InventedHex(4)}#{InventedHex(16)}";
        var hash = LiveAccount.Client().PredictHash(RemoteItemKind.WebDownload, link)!;
        return AsksOnlyIfCachedAndCreatesNothingAsync(RemoteItemKind.WebDownload, link, hash);
    }
}
