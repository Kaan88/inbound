using System.Security.Cryptography;
using System.Text.Json;
using DownloadManager.Contracts;
using Xunit;

namespace DownloadManager.Integration.Tests;

public sealed class BrowserBridgeTests
{
    [Theory]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("ifoagddcenmjpbklkhghbdmgplldcdci")]
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
    [InlineData("chrome-extension://ifoagddcenmjpbklkhghbdmgplldcdci/")]
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

    [Theory]
    [InlineData("scripts", "register-native-host.ps1")]
    [InlineData("extensions", "chrome", "src", "connection-problem.ts")]
    public void Edge_store_id_is_listed_where_the_ids_are(params string[] path) =>
        Assert.Contains(BrowserBridge.EdgeStoreExtensionId, RepoFile(path));

    [Fact]
    public void Edge_store_id_is_in_the_installer_manifest() =>
        Assert.Contains("EdgeStoreExtensionId \"" + BrowserBridge.EdgeStoreExtensionId + "\"", RepoFile("installer", "DownloadManager.iss"));

    [Fact]
    public void The_Edge_public_key_gives_the_trusted_Edge_id() =>
        Assert.Equal(BrowserBridge.EdgeStoreExtensionId, IdFromKey(
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAohOOQgwjEzgy/q20y+BvyDM0qpYk/nIO4cMELSzqa0vxKqcAZ917r+U5+KfLVebxtcIRI3nPoxMy/7uIrmwY5nsh9VKxOArUopwR3b0PP0Mosl3HKZjCiVX7y8dlqRalCxKcshcPXpTw3UBORndrYg/e7rmJdAK6yiOYpxqAIA7e5vBwsDQRg6bHhimU+3sMox8WfQEcZhwSFyxWW9O2PXpSmbLC4q2PwmkWv9uUGpQY0CqqCM/Egu8Tu3egx26JrYLfjDOdLaNcr8Wsml1cNmJvN74xR4RUS6K3O1wEKAaRSJGeDvqn3cjV2LoLlwS095teNE+KGSfkkRweyHZ4lQIDAQAB"));

    [Theory]
    [InlineData("installer", "DownloadManager.iss")]
    [InlineData("scripts", "register-native-host.ps1")]
    [InlineData("extensions", "chrome", "src", "connection-problem.ts")]
    [InlineData("extensions", "chrome", "scripts", "build.mjs")]
    public void Every_place_that_names_the_Firefox_id_agrees(params string[] path) =>
        Assert.Contains($"\"{BrowserBridge.FirefoxExtensionId}\"", RepoFile(path));

    [Fact]
    public void The_Firefox_id_is_trusted_for_handoffs_exactly() // EXT.E1
    {
        Assert.True(BrowserBridge.IsTrustedExtensionId("inbound-bridge@kaan88"));
        Assert.False(BrowserBridge.IsTrustedExtensionId("Inbound-Bridge@kaan88"));
        Assert.False(BrowserBridge.IsTrustedExtensionId("inbound-bridge@kaan88 "));
    }
}
