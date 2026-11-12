using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

// UPD.N2: the update package comes through the download engine, in segments when the server allows.
public sealed partial class RuntimeServiceTests
{
    private const int UpdateSize = 4 * 1024 * 1024;

    [Fact]
    public async Task UpdateDownloadUsesSegmentsBehindARedirectAndPassesTheHash()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-seg.bin", UpdateSize, 901);
        // Like a GitHub release asset: the manifest's address redirects, and every range goes to where it leads.
        var file = $"/file/upd-seg.bin?size={UpdateSize}&seed=901&bps={1024 * 1024}";
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/redirect?to={Uri.EscapeDataString(file)}",
            bytes.Length, Sha256Of(bytes), connections: 4);
        var stages = new List<UpdateProgressDto>();
        ctx.Service.UpdateProgressChanged += (_, progress) => { lock (stages) stages.Add(progress); };
        _server.ResetConcurrency();

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.True(download.Ok, download.Error);
        Assert.Equal(Sha256Of(bytes), download.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(download.Path!));
        Assert.True(_server.PeakConcurrentRangeRequests >= 2, $"peak ranges {_server.PeakConcurrentRangeRequests}");
        AssertNoUpdatePartFiles(ctx);
        lock (stages)
        {
            Assert.Contains(stages, p => p is { Stage: "downloading", ReceivedBytes: > 0 } && p.TotalBytes == bytes.Length);
            Assert.All(stages, p => Assert.True(p.ReceivedBytes <= bytes.Length));
            Assert.Equal("verifying", stages[^1].Stage);
        }

        // The update is internal: it is never a job in the list.
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task UpdateDownloadFallsBackToOneStreamWithoutRanges()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-norange.bin", UpdateSize, 902);
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/norange/upd-norange.bin?size={UpdateSize}&seed=902",
            bytes.Length, Sha256Of(bytes), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.True(download.Ok, download.Error);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(download.Path!));
        AssertNoUpdatePartFiles(ctx);
    }

    [Fact]
    public async Task UpdateDownloadRetriesSegmentsThatAreCutShort()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-cut.bin", UpdateSize, 903);
        // Every response stops after 400 KB, so each 1 MB range takes several requests.
        var root = await OfferPackageAsync(ctx,
            $"{_server.BaseAddress}/truncate/upd-cut.bin?size={UpdateSize}&seed=903&cut={400 * 1024}",
            bytes.Length, Sha256Of(bytes), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.True(download.Ok, download.Error);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(download.Path!));
        AssertNoUpdatePartFiles(ctx);
    }

    [Fact]
    public async Task UpdateDownloadThatKeepsFailingReportsTheStatusAndLeavesNothing()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-fail.bin", UpdateSize, 1);
        var root = await OfferPackageAsync(ctx,
            $"{_server.BaseAddress}/flaky/upd-fail-{Guid.NewGuid():N}.bin?size={UpdateSize}&failures=1000",
            bytes.Length, Sha256Of(bytes), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.False(download.Ok);
        Assert.Equal("Installer download returned HTTP 500.", download.Error);
        AssertNoUpdateFiles(ctx);
    }

    [Fact]
    public async Task SegmentedUpdateWithTheWrongHashIsDeletedAndReported()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-hash.bin", UpdateSize, 904);
        var wrong = Sha256Of([1, 2, 3]);
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/file/upd-hash.bin?size={UpdateSize}&seed=904",
            bytes.Length, wrong, connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.False(download.Ok);
        Assert.Equal("Installer size or SHA-256 did not match the signed manifest.", download.Error);
        AssertNoUpdateFiles(ctx);
    }

    [Fact]
    public async Task UpdateOfAnotherSizeThanSignedIsRefused()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-size.bin", UpdateSize, 905);
        // The server's file is one byte longer than the manifest says: refused before a byte is written.
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/file/upd-size.bin?size={UpdateSize}&seed=905",
            bytes.Length - 1, Sha256Of(bytes[..^1]), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.False(download.Ok);
        Assert.Equal("The installer is larger than the signed size.", download.Error);
        AssertNoUpdateFiles(ctx);
    }

    [Fact]
    public async Task UpdateWithoutALengthStillStopsPastTheSignedSize()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-unknown.bin", UpdateSize, 906);
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/unknown/upd-unknown.bin?size={UpdateSize}&seed=906",
            UpdateSize / 2, Sha256Of(bytes[..(UpdateSize / 2)]), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.False(download.Ok);
        Assert.Equal("The installer is larger than the signed size.", download.Error);
        AssertNoUpdateFiles(ctx);
    }

    [Fact]
    public async Task UpdateRedirectToPlainHttpIsNotFollowed()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = await OfferPackageAsync(ctx,
            $"{_server.BaseAddress}/redirect?to={Uri.EscapeDataString("http://update.example.invalid/package.zip")}",
            UpdateSize, new string('0', 64), connections: 4);

        var download = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());

        Assert.False(download.Ok);
        Assert.Equal("The installer redirected to a non-HTTPS host.", download.Error);
        AssertNoUpdateFiles(ctx);
    }

    [Fact]
    public async Task CancelledUpdateDownloadLeavesNothingAndTheNextOneCompletes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-cancel.bin", UpdateSize, 907);
        var root = await OfferPackageAsync(ctx,
            $"{_server.BaseAddress}/file/upd-cancel.bin?size={UpdateSize}&seed=907&bps={2 * 1024 * 1024}",
            bytes.Length, Sha256Of(bytes), connections: 4);
        using var cancellation = new CancellationTokenSource();
        ctx.Service.UpdateProgressChanged += (_, progress) =>
        {
            if (progress is { Stage: "downloading", ReceivedBytes: > 256 * 1024 }) cancellation.Cancel();
        };

        await WithInstallRootAsync(root, async () =>
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Service.DownloadUpdateAsync(cancellation.Token));
            AssertNoUpdateFiles(ctx);
            return 0;
        });

        var again = await WithInstallRootAsync(root, () => ctx.Service.DownloadUpdateAsync());
        Assert.True(again.Ok, again.Error);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(again.Path!));
        AssertNoUpdatePartFiles(ctx);
    }

    [Fact]
    public async Task TwoUpdateDownloadsAtOnceBothGetTheVerifiedPackage()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bytes = _server.ContentFor("upd-twice.bin", UpdateSize, 908);
        var root = await OfferPackageAsync(ctx, $"{_server.BaseAddress}/file/upd-twice.bin?size={UpdateSize}&seed=908&bps={4 * 1024 * 1024}",
            bytes.Length, Sha256Of(bytes), connections: 4);

        var (first, second) = await WithInstallRootAsync(root, async () =>
        {
            var a = ctx.Service.DownloadUpdateAsync();
            var b = ctx.Service.DownloadUpdateAsync();
            return (await a, await b);
        });

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path!));
        AssertNoUpdatePartFiles(ctx);
    }

    [Fact]
    public void UpdateConnectionsFollowTheSettingsWithinTheCap()
    {
        Assert.Equal(1, DownloadManagerService.UpdateConnections(new AppSettings { DefaultConnections = 1, MaxConnectionsPerHost = 8 }));
        Assert.Equal(4, DownloadManagerService.UpdateConnections(new AppSettings { DefaultConnections = 16, MaxConnectionsPerHost = 4 }));
        Assert.Equal(DownloadManagerService.MaxUpdateConnections,
            DownloadManagerService.UpdateConnections(new AppSettings { DefaultConnections = 32, MaxConnectionsPerHost = 32 }));
    }

    [Fact]
    public void UpdateDownloadFailuresKeepTheUpdatersWords()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Installer download returned HTTP 404.",
            DownloadManagerService.UpdateDownloadError(new JobFailure(FailureKind.NotFound, "The server returned HTTP 404.", false, false, now, 404)));
        Assert.Equal("Installer size or SHA-256 did not match the signed manifest.",
            DownloadManagerService.UpdateDownloadError(new JobFailure(FailureKind.ExpiredUrl, "another size", false, true, now, 200)));
        Assert.Equal("The connection was lost while reading.",
            DownloadManagerService.UpdateDownloadError(new JobFailure(FailureKind.Network, "The connection was lost while reading.", true, true, now)));
    }

    private static async Task<T> WithInstallRootAsync<T>(string root, Func<Task<T>> action)
    {
        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
        }
    }

    private static void AssertNoUpdatePartFiles(ServiceContext ctx)
    {
        var updates = Path.Combine(ctx.Paths.Root, "updates");
        Assert.Empty(Directory.Exists(updates) ? Directory.GetFiles(updates, "*.part") : []);
    }

    private static void AssertNoUpdateFiles(ServiceContext ctx)
    {
        var updates = Path.Combine(ctx.Paths.Root, "updates");
        Assert.Empty(Directory.Exists(updates) ? Directory.GetFiles(updates, "installer-*") : []);
    }

    // Offers version 1.0.1 at installerUrl with this size and hash, signed, and sets the download's connections.
    private async Task<string> OfferPackageAsync(ServiceContext ctx, string installerUrl, long size, string sha256, int connections)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"), "install");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "install.json"), """{"version":"1.0.0","channel":"stable"}""");

        using var rsa = RSA.Create(2048);
        var manifest = new UpdateManifest
        {
            Version = "1.0.1",
            Channel = "stable",
            InstallerUrl = installerUrl,
            Size = size,
            Sha256 = sha256,
        };
        manifest.Signature = Convert.ToBase64String(rsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(UpdateManifestVerifier.Canonical(manifest)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        _server.SetUpdatePayload(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, IpcJson.Options), []);

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.UpdateManifestUrl = $"{_server.BaseAddress}/update/manifest";
        settings.UpdatePublicKey = rsa.ExportSubjectPublicKeyInfoPem();
        settings.UpdateChannel = "stable";
        settings.DefaultConnections = connections;
        settings.MaxConnectionsPerHost = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        return root;
    }
}
