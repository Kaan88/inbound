using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>Options in 0.4.0 and 0.5.0 saved only the first line of the update key (UP.B1).</summary>
public sealed class UpdateKeySettingsTests
{
    private static StoragePaths NewPaths() =>
        new(Path.Combine(AppContext.BaseDirectory, "test-work", "update-key-" + Guid.NewGuid().ToString("N"), "state"));

    [Theory]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    [InlineData("not a key")]
    public async Task AKeyCutToItsFirstLineIsReplacedByTheBuiltInOne(string broken)
    {
        var paths = NewPaths();
        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(new AppSettings { UpdatePublicKey = broken, UpdateManifestUrl = ReleaseUpdates.ManifestUrl });

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Equal(ReleaseUpdates.PublicKey, loaded.UpdatePublicKey);
        Assert.Equal(ReleaseUpdates.ManifestUrl, loaded.UpdateManifestUrl);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task AWholeKeyOfItsOwnStaysWhateverItsLineBreaks(string lineBreak)
    {
        using var rsa = RSA.Create(2048);
        var own = rsa.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n").Replace("\n", lineBreak);
        var paths = NewPaths();
        await new JsonSettingsStore(paths).SaveAsync(new AppSettings { UpdatePublicKey = own, UpdateManifestUrl = "https://example.test/manifest.json" });

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Equal(own, loaded.UpdatePublicKey);
    }
}
