using System.Security.Cryptography;
using System.Text.Json;
using DownloadManager.Contracts;
using Xunit;

namespace DownloadManager.Integration.Tests;

public sealed class BrowserBridgeTests
{
    [Theory]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("pknddmneejadnnngffojnoecojhdfalc")]
    public void Trusts_the_store_and_unpacked_extension_ids(string id) =>
        Assert.True(BrowserBridge.IsTrustedExtensionId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-extension")]
    [InlineData("OLHBCDBJDNFOIINLLNLPANDGGADFABFM")]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabf")]
    public void Refuses_any_other_extension_id(string? id) =>
        Assert.False(BrowserBridge.IsTrustedExtensionId(id));

    [Theory]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/")]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("chrome-extension://pknddmneejadnnngffojnoecojhdfalc/")]
    public void Trusts_the_store_and_unpacked_origins(string origin) =>
        Assert.True(BrowserBridge.IsTrustedOrigin(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/evil")]
    [InlineData("https://olhbcdbjdnfoiinllnlpandggadfabfm/")]
    public void Refuses_any_other_origin(string? origin) =>
        Assert.False(BrowserBridge.IsTrustedOrigin(origin));

    // Chrome's rule: the first 16 bytes of the key's SHA-256, each half-byte written as a-p.
    private static string IdFromKey(string base64Key)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(base64Key));
        return string.Concat(hash.Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }));
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    [Fact]
    public void The_kept_store_key_gives_the_trusted_store_id() =>
        Assert.Equal(BrowserBridge.StoreExtensionId,
            IdFromKey(RepoFile("extensions", "chrome", "store-public-key.txt").Trim()));

    [Fact]
    public void The_unpacked_manifest_key_gives_the_trusted_unpacked_id()
    {
        using var manifest = JsonDocument.Parse(RepoFile("extensions", "chrome", "manifest.json"));
        Assert.Equal(BrowserBridge.UnpackedExtensionId,
            IdFromKey(manifest.RootElement.GetProperty("key").GetString()!));
    }

    [Theory]
    [InlineData("installer", "DownloadManager.iss")]
    [InlineData("scripts", "register-native-host.ps1")]
    [InlineData("extensions", "chrome", "src", "connection-problem.ts")]
    public void Every_place_that_lists_the_ids_agrees(params string[] path)
    {
        var text = RepoFile(path);
        Assert.Contains(BrowserBridge.StoreExtensionId, text);
        Assert.Contains(BrowserBridge.UnpackedExtensionId, text);
    }
}
