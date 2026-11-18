using System.Security.Cryptography;
using System.Text;
using DownloadManager.Mobile.Updates;
using Xunit;

namespace DownloadManager.Mobile.Tests;

public sealed class AndroidUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-update-test-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private readonly byte[] _apk = Encoding.UTF8.GetBytes("test apk bytes");
    private readonly FakeFeed _feed = new();
    private int _packageChecks;
    private bool _rejectPackage;

    public AndroidUpdaterTests() => Directory.CreateDirectory(_root);
    public void Dispose() { _key.Dispose(); Directory.Delete(_root, recursive: true); }

    private AndroidUpdateManifest Offer(long code = 2, string name = "2.0")
    {
        var m = new AndroidUpdateManifest { VersionCode = code, VersionName = name,
            ApkUrl = "https://github.com/Kaan88/inbound/releases/download/android-latest/remote-name.apk",
            Size = _apk.Length, Sha256 = Convert.ToHexString(SHA256.HashData(_apk)).ToLowerInvariant() };
        Sign(m);
        return m;
    }
    private void Sign(AndroidUpdateManifest m) => m.Signature = Convert.ToBase64String(_key.SignData(
        Encoding.UTF8.GetBytes(AndroidUpdateTrust.Canonical(m)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    private AndroidUpdater Create(long installed = 1) => new(_root, _key.ExportSubjectPublicKeyInfoPem(), installed, _feed,
        (path, manifest, ct) => { Interlocked.Increment(ref _packageChecks); if (_rejectPackage) throw new InvalidDataException("wrong package"); return Task.CompletedTask; });
    private void Serve(AndroidUpdateManifest m) { _feed.Manifest = m; _feed.Bytes = _apk; }

    [Fact] public async Task NoFeedIsCurrentWithoutDownload()
    {
        var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Current, updater.Status.Stage); Assert.Equal(0, _feed.Downloads);
    }
    [Fact] public async Task NewerSignedOfferDownloadsToPrivateVersionedPath()
    {
        Serve(Offer()); var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Ready, updater.Status.Stage);
        var ready = await updater.ReadyForInstallAsync(CancellationToken.None);
        Assert.NotNull(ready); Assert.Equal(Path.Combine(_root, "update-2.apk"), ready.Path);
        Assert.Equal(_apk, await File.ReadAllBytesAsync(ready.Path)); Assert.Equal(1, _feed.Downloads);
    }
    [Fact] public async Task BadSignatureNeverDownloads()
    {
        var m = Offer(); m.VersionName = "3.0"; Serve(m); var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Error, updater.Status.Stage); Assert.Equal(0, _feed.Downloads);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WrongHashOrSizeNeverBecomesReady(bool wrongSize)
    {
        var m = Offer(); if (wrongSize) m.Size++; else m.Sha256 = new string('0', 64); Sign(m); Serve(m);
        var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Error, updater.Status.Stage); Assert.Null(await updater.ReadyForInstallAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "download.part")));
    }
    [Fact] public async Task PackageRejectionNeverBecomesReady()
    {
        Serve(Offer()); _rejectPackage = true; var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Error, updater.Status.Stage); Assert.Null(await updater.ReadyForInstallAsync(CancellationToken.None));
    }
    [Fact] public async Task CachedApkIsRecheckedBeforeInstallAndTamperingIsRejected()
    {
        Serve(Offer()); var updater = Create(); await updater.CheckAsync();
        var ready = await updater.ReadyForInstallAsync(CancellationToken.None); Assert.NotNull(ready);
        Assert.True(_packageChecks >= 2);
        await File.WriteAllBytesAsync(ready.Path, Encoding.UTF8.GetBytes("tampered apk bytes"));
        Assert.Null(await updater.ReadyForInstallAsync(CancellationToken.None)); Assert.Equal(AndroidUpdateStage.Error, updater.Status.Stage);
    }
    [Fact] public async Task CorruptCachedJsonRecoversThroughFreshCheck()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ready.json"), "{ broken"); Serve(Offer());
        var updater = Create(); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Ready, updater.Status.Stage); Assert.NotNull(await updater.ReadyForInstallAsync(CancellationToken.None));
    }
    [Fact] public async Task CancellationCleansPartialFile()
    {
        Serve(Offer()); _feed.HoldDownload = true; var updater = Create(); using var cts = new CancellationTokenSource();
        var task = updater.CheckAsync(cts.Token); await _feed.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(AndroidUpdateStage.Error, updater.Status.Stage); Assert.False(File.Exists(Path.Combine(_root, "download.part")));
        Assert.Null(await updater.ReadyForInstallAsync(CancellationToken.None));
    }
    [Fact] public async Task ConcurrentChecksCoalesce()
    {
        Serve(Offer()); _feed.HoldDownload = true; var updater = Create(); using var cts = new CancellationTokenSource();
        var first = updater.CheckAsync(cts.Token); await _feed.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = updater.CheckAsync(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _feed.DownloadGate.TrySetResult(); await second; Assert.Equal(AndroidUpdateStage.Ready, updater.Status.Stage);
        Assert.Equal(1, _feed.Reads); Assert.Equal(1, _feed.Downloads);
    }
    [Theory] [InlineData(2)] [InlineData(3)]
    public async Task InstalledVersionAtLeastOfferDoesNotDownload(long installed)
    {
        Serve(Offer()); var updater = Create(installed); await updater.CheckAsync();
        Assert.Equal(AndroidUpdateStage.Current, updater.Status.Stage); Assert.Equal(0, _feed.Downloads);
    }

    [Fact] public async Task CachedReadyCanBeInstalledWhileRefreshReadStalls()
    {
        var old = Offer(); Serve(old); var first = Create(); await first.CheckAsync();
        _feed.HoldRead = true; _feed.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = Create(); var refresh = restarted.CheckAsync(); await _feed.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, restarted.ReadyOffer?.Manifest.VersionCode);
        var ready = await restarted.ReadyForInstallAsync(2, old.Sha256, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(ready); _feed.ReadGate.TrySetResult(); await refresh;
    }
    [Fact] public async Task SelectedOldVersionRemainsAvailableAfterNewerOffer()
    {
        var old = Offer(); Serve(old); var updater = Create(); await updater.CheckAsync();
        Serve(Offer(3, "3.0")); await updater.CheckAsync();
        var selected = await updater.ReadyForInstallAsync(2, old.Sha256, CancellationToken.None);
        Assert.NotNull(selected); Assert.Equal(2, selected.Manifest.VersionCode); Assert.Equal(3, updater.ReadyOffer?.Manifest.VersionCode);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrCorruptSidecarFallsBackToMatchingReadyAfterRestart(bool corrupt)
    {
        var offer = Offer(); Serve(offer); await Create().CheckAsync(); var sidecar = Path.Combine(_root, "update-2.json");
        if (corrupt) await File.WriteAllTextAsync(sidecar, "{ broken"); else File.Delete(sidecar);
        var restarted = Create(); var ready = await restarted.ReadyForInstallAsync(2, offer.Sha256, CancellationToken.None);
        Assert.NotNull(ready); Assert.Equal(2, ready.Manifest.VersionCode);
    }
    [Fact] public async Task StaleSelectionFailurePreservesNewerReady()
    {
        var old = Offer(); Serve(old); var updater = Create(); await updater.CheckAsync();
        Serve(Offer(3, "3.0")); await updater.CheckAsync(); File.Delete(Path.Combine(_root, "update-2.apk"));
        Assert.Null(await updater.ReadyForInstallAsync(2, old.Sha256, CancellationToken.None));
        Assert.Equal(3, updater.ReadyOffer?.Manifest.VersionCode); Assert.Equal(AndroidUpdateStage.Ready, updater.Status.Stage);
    }
    [Fact] public async Task CleanupRemovesOldAppliedAndOrphanFilesButPreservesActiveAndRecent()
    {
        var active = Offer(4, "4.0"); Serve(active); var writer = Create(); await writer.CheckAsync();
        foreach (var code in new[] { 1, 2, 3 }) { var path = Path.Combine(_root, $"update-{code}.apk"); await File.WriteAllBytesAsync(path, _apk); if (code != 3) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-15)); }
        File.SetLastWriteTimeUtc(Path.Combine(_root, "update-4.apk"), DateTime.UtcNow.AddDays(-15));
        _feed.Manifest = null; var updater = Create(installed: 1); await updater.CheckAsync();
        Assert.False(File.Exists(Path.Combine(_root, "update-1.apk"))); Assert.False(File.Exists(Path.Combine(_root, "update-2.apk")));
        Assert.True(File.Exists(Path.Combine(_root, "update-3.apk"))); Assert.True(File.Exists(Path.Combine(_root, "update-4.apk")));
    }
    [Fact] public async Task SelectingOldApkRefreshesRetentionBeforeSuperseding()
    {
        var old = Offer(); Serve(old); var updater = Create(); await updater.CheckAsync();
        var oldPath = Path.Combine(_root, "update-2.apk"); File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-15));
        Assert.NotNull(await updater.ReadyForInstallAsync(2, old.Sha256, CancellationToken.None));
        Assert.True(File.GetLastWriteTimeUtc(oldPath) > DateTime.UtcNow.AddMinutes(-1));
        Serve(Offer(3, "3.0")); await updater.CheckAsync(); await updater.CheckAsync(); Assert.True(File.Exists(oldPath));
    }

    private sealed class FakeFeed : IAndroidUpdateFeed
    {
        public AndroidUpdateManifest? Manifest; public byte[] Bytes = []; public int Reads, Downloads; public bool HoldDownload;
        public TaskCompletionSource DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldRead; public TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AndroidUpdateManifest?> ReadAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); ct.ThrowIfCancellationRequested(); ReadStarted.TrySetResult(); if (HoldRead) await ReadGate.Task.WaitAsync(ct); return Manifest; }
        public async Task DownloadAsync(AndroidUpdateManifest manifest, string path, Action<long> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Downloads); await File.WriteAllBytesAsync(path, Bytes, ct); progress(Bytes.Length); DownloadStarted.TrySetResult();
            if (HoldDownload) await DownloadGate.Task.WaitAsync(ct);
        }
    }
}
