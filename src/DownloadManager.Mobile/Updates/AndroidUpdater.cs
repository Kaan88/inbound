using System.Security.Cryptography;
using System.Text.Json;

namespace DownloadManager.Mobile.Updates;

public enum AndroidUpdateStage { Idle, Checking, Downloading, Current, Ready, Error }
public sealed record AndroidUpdateStatus(AndroidUpdateStage Stage, string Text, string? Version = null, long Bytes = 0, long Total = 0);
public sealed record AndroidUpdateReady(string Path, AndroidUpdateManifest Manifest);

/// <summary>One process, one operation. Signed metadata and immutable versioned APKs; a ready file is rechecked before every install.</summary>
public sealed class AndroidUpdater
{
    private readonly string _root, _publicKey;
    private readonly long _installed;
    private readonly IAndroidUpdateFeed _feed;
    private readonly Func<string, AndroidUpdateManifest, CancellationToken, Task> _packageCheck;
    private readonly object _sync = new();
    private Operation? _operation;
    private AndroidUpdateReady? _ready;
    private AndroidUpdateStatus _status = new(AndroidUpdateStage.Idle, "Checks daily and when Inbound starts.");

    public AndroidUpdater(string root, string publicKey, long installed, IAndroidUpdateFeed feed,
        Func<string, AndroidUpdateManifest, CancellationToken, Task> packageCheck)
    { _root = root; _publicKey = publicKey; _installed = installed; _feed = feed; _packageCheck = packageCheck; }

    public AndroidUpdateStatus Status { get { lock (_sync) return _status; } }
    public AndroidUpdateReady? ReadyOffer { get { lock (_sync) return _ready; } }
    public event Action<AndroidUpdateStatus>? Changed;

    public Task CheckAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_operation is { Work.IsCompleted: false } && _operation.Cancellation.IsCancellationRequested)
                return RetryAfterCancellationAsync(_operation.Work, ct);
            if (_operation is null || _operation.Work.IsCompleted)
            {
                _operation = new Operation();
                var token = _operation.Cancellation.Token;
                _operation.Work = Task.Run(() => CheckCoreAsync(token), CancellationToken.None);
            }
            _operation.Users++;
            return AwaitLeaseAsync(_operation, ct);
        }
    }

    private async Task AwaitLeaseAsync(Operation operation, CancellationToken ct)
    {
        try { await operation.Work.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            var waitForCleanup = false;
            lock (_sync)
            {
                if (--operation.Users == 0)
                {
                    if (!operation.Work.IsCompleted) { operation.Cancellation.Cancel(); waitForCleanup = true; }
                    else operation.Cancellation.Dispose();
                }
            }
            if (waitForCleanup)
            {
                await operation.Work.ConfigureAwait(false);
                operation.Cancellation.Dispose();
            }
        }
    }

    private async Task RetryAfterCancellationAsync(Task previous, CancellationToken ct)
    {
        await previous.WaitAsync(ct).ConfigureAwait(false);
        await CheckAsync(ct).ConfigureAwait(false);
    }

    private sealed class Operation
    {
        public Task Work = Task.CompletedTask;
        public readonly CancellationTokenSource Cancellation = new();
        public int Users;
    }

    private async Task CheckCoreAsync(CancellationToken caller)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        AndroidUpdateReady? cached = null;
        string? part = null;
        try
        {
            Directory.CreateDirectory(_root);
            try { cached = await ReadReadyAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt/interrupted cache must not prevent fetching a replacement forever.
                File.Delete(Path.Combine(_root, "ready.json"));
            }
            lock (_sync) _ready = cached;
            if (cached is not null) Set(ReadyStatus(cached)); // usable offline while a refresh is in progress
            CleanupOwnedFiles(cached?.Path);
            Set(new(AndroidUpdateStage.Checking, "Checking GitHub for an Android update…"));
            var manifest = await _feed.ReadAsync(ct).ConfigureAwait(false);
            if (manifest is null)
            {
                Set(cached is null ? new(AndroidUpdateStage.Current, "No Android update has been published yet.") : ReadyStatus(cached));
                return;
            }
            AndroidUpdateTrust.Validate(manifest, _publicKey);
            if (cached is not null && manifest.VersionCode <= cached.Manifest.VersionCode) { Set(ReadyStatus(cached)); return; }
            if (manifest.VersionCode <= _installed) { Set(new(AndroidUpdateStage.Current, "Inbound is up to date.")); return; }
            part = Path.Combine(_root, "download.part");
            File.Delete(part); // only this updater's known temporary file
            Set(new(AndroidUpdateStage.Downloading, "Downloading the Android update…", manifest.VersionName, 0, manifest.Size));
            await _feed.DownloadAsync(manifest, part,
                bytes => Set(new(AndroidUpdateStage.Downloading, "Downloading the Android update…", manifest.VersionName, bytes, manifest.Size)), ct).ConfigureAwait(false);
            await ValidateApkAsync(part, manifest, ct).ConfigureAwait(false);
            var final = ApkPath(manifest.VersionCode);
            File.Move(part, final, overwrite: true);
            part = null;
            await SaveManifestAsync(manifest, ct).ConfigureAwait(false);
            var ready = new AndroidUpdateReady(final, manifest);
            lock (_sync) _ready = ready;
            Set(ReadyStatus(ready));
        }
        catch (OperationCanceledException)
        {
            Set(cached is null ? new(AndroidUpdateStage.Error, "Update check paused. It will try again next time.") : ReadyStatus(cached));
        }
        catch (Exception)
        {
            // Network errors can contain signed CDN query strings. They never reach logs or the screen.
            Set(cached is null ? new(AndroidUpdateStage.Error, "The update could not be checked or verified. Try again later.") : ReadyStatus(cached));
        }
        finally { if (part is not null) { try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } } }
    }

    public async Task<AndroidUpdateReady?> ReadyForInstallAsync(CancellationToken ct)
    {
        var selected = ReadyOffer;
        return await ReadyForInstallAsync(selected?.Manifest.VersionCode, selected?.Manifest.Sha256, ct).ConfigureAwait(false);
    }

    public async Task<AndroidUpdateReady?> ReadyForInstallAsync(long? versionCode, string? expectedHash, CancellationToken ct)
    {
        // Never wait on the network. The exact version chosen remains immutable while a newer update downloads.
        try
        {
            AndroidUpdateReady? ready;
            try { ready = await ReadReadyAsync(ct, versionCode).ConfigureAwait(false); }
            catch (Exception e) when (versionCode is not null && e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { ready = null; }
            // The independently signed ready pointer is also sufficient when a sidecar
            // was lost during a crash. It may only stand in for that exact selection.
            ready ??= await ReadReadyAsync(ct).ConfigureAwait(false);
            if (ready is not null && versionCode is { } selected && ready.Manifest.VersionCode != selected)
                throw new InvalidDataException("The selected version is no longer cached.");
            if (ready is not null && expectedHash is not null && !ready.Manifest.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected update changed.");
            if (ready is not null) File.SetLastWriteTimeUtc(ready.Path, DateTime.UtcNow); // installer-safe retention starts at handoff
            return ready;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            AndroidUpdateReady? current;
            lock (_sync)
            {
                if (_ready?.Manifest.VersionCode == versionCode || versionCode is null) _ready = null;
                current = _ready;
            }
            Set(current is null ? new(AndroidUpdateStage.Error, "The saved update could not be verified. Check for updates again.") : ReadyStatus(current));
            return null;
        }
    }

    private async Task<AndroidUpdateReady?> ReadReadyAsync(CancellationToken ct, long? versionCode = null)
    {
        if (versionCode is <= 0) throw new InvalidDataException("Invalid selected version.");
        var path = Path.Combine(_root, versionCode is { } code ? $"update-{code}.json" : "ready.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Update information too large.");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(bytes, AndroidUpdateJson.Default.AndroidUpdateManifest)
            ?? throw new InvalidDataException("Update information missing.");
        AndroidUpdateTrust.Validate(manifest, _publicKey);
        if (versionCode is { } selected && manifest.VersionCode != selected) throw new InvalidDataException("Wrong selected version.");
        if (manifest.VersionCode <= _installed) return null;
        var apk = ApkPath(manifest.VersionCode);
        await ValidateApkAsync(apk, manifest, ct).ConfigureAwait(false);
        return new(apk, manifest);
    }

    private async Task ValidateApkAsync(string path, AndroidUpdateManifest manifest, CancellationToken ct)
    {
        if (new FileInfo(path).Length != manifest.Size) throw new InvalidDataException("Wrong update size.");
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            var hash = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
            if (!Convert.ToHexString(hash).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Wrong update hash.");
        }
        await _packageCheck(path, manifest, ct).ConfigureAwait(false);
    }

    private async Task SaveManifestAsync(AndroidUpdateManifest manifest, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AndroidUpdateJson.Default.AndroidUpdateManifest);
        await SaveAtomicAsync(Path.Combine(_root, $"update-{manifest.VersionCode}.json"), bytes, ct).ConfigureAwait(false);
        await SaveAtomicAsync(Path.Combine(_root, "ready.json"), bytes, ct).ConfigureAwait(false);
    }

    private static async Task SaveAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        { await output.WriteAsync(bytes, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true); }
        File.Move(temp, path, overwrite: true);
    }

    private void CleanupOwnedFiles(string? active)
    {
        foreach (var path in Directory.EnumerateFiles(_root, "update-*.apk"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (path == active || !long.TryParse(name.AsSpan("update-".Length), out var code) || code <= 0) continue;
            // Applied versions are no longer installer inputs. Other orphans get two weeks
            // of grace so an installer still reading a previously selected version keeps working.
            if (code > _installed && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-14)) continue;
            try { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".json")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private string ApkPath(long code) => Path.Combine(_root, $"update-{code}.apk");
    private static AndroidUpdateStatus ReadyStatus(AndroidUpdateReady ready) =>
        new(AndroidUpdateStage.Ready, $"Inbound {ready.Manifest.VersionName} is ready to install.", ready.Manifest.VersionName, ready.Manifest.Size, ready.Manifest.Size);
    private void Set(AndroidUpdateStatus status)
    {
        lock (_sync) _status = status;
        var listeners = Changed;
        if (listeners is null) return;
        foreach (Action<AndroidUpdateStatus> listener in listeners.GetInvocationList())
            try { listener(status); } catch (Exception) { /* a UI listener cannot fail the update */ }
    }
}
