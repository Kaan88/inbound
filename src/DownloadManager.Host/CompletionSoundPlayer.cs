using System.Media;

namespace DownloadManager.Host;

/// <summary>Plays configured per-event system or WAV sounds without launching processes.</summary>
public sealed class CompletionSoundPlayer(Action<string>? log = null) : IDisposable
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string QueueFinished = "queueFinished";
    public const string SystemSound = "@system";

    private const long MaximumWaveBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan MaximumPlayback = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action<string>? _log = log;
    private bool _disposed;
    private int _activeCalls;

    /// <summary>
    /// Plays the configured event sound. Missing completion settings use the
    /// legacy system sound; missing failure/queue settings remain silent.
    /// An empty value disables playback; invalid custom files fall back.
    /// </summary>
    public async Task PlayAsync(
        string eventKey,
        IReadOnlyDictionary<string, string>? eventSoundFiles,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCalls++;
        }

        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var token = lifetime.Token;
            if (!TryGetDefaultSound(eventKey, out var systemSound))
            {
                return;
            }

            string? setting = null;
            if (eventSoundFiles is not null)
            {
                foreach (var pair in eventSoundFiles)
                {
                    if (string.Equals(pair.Key, eventKey, StringComparison.OrdinalIgnoreCase))
                    {
                        setting = pair.Value;
                        break;
                    }
                }
            }

            if (setting is null)
            {
                // Existing users only had the completed sound preference. Its
                // PlayCompletionSound gate remains the caller's responsibility.
                if (string.Equals(eventKey, Completed, StringComparison.OrdinalIgnoreCase)) systemSound.Play();
                return;
            }

            if (string.Equals(setting, SystemSound, StringComparison.OrdinalIgnoreCase))
            {
                systemSound.Play();
                return;
            }

            if (setting.Length == 0)
            {
                return;
            }

            if (!IsUsableWave(setting))
            {
                _log?.Invoke($"Configured {eventKey} sound is unavailable or invalid; using the system sound.");
                systemSound.Play();
                return;
            }

            if (!await _playGate.WaitAsync(0, token).ConfigureAwait(false))
            {
                _log?.Invoke("A configured event sound is already playing; the new sound was skipped.");
                return;
            }

            try
            {
                await PlayWaveAsync(setting, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not play configured {eventKey} sound ({ex.GetType().Name}); using the system sound.");
                systemSound.Play();
            }
            finally
            {
                _playGate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Disposal cancels work quietly; caller cancellation remains observable.
        }
        finally
        {
            lock (_sync)
            {
                _activeCalls--;
                if (_disposed && _activeCalls == 0)
                {
                    DisposeResources();
                }
            }
        }
    }

    private static async Task PlayWaveAsync(string path, CancellationToken cancellationToken)
    {
        using var player = new SoundPlayer(path);
        await Task.Run(player.Load, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var stopOnCancellation = cancellationToken.Register(() =>
        {
            try { player.Stop(); }
            catch { /* The playback task observes any remaining failure and falls back. */ }
        });
        var playTask = Task.Run(player.PlaySync, CancellationToken.None);
        var timer = Task.Delay(MaximumPlayback, cancellationToken);
        if (await Task.WhenAny(playTask, timer).ConfigureAwait(false) == playTask)
        {
            await playTask.ConfigureAwait(false);
            return;
        }

        player.Stop();
        await playTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool IsUsableWave(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) &&
                   string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaximumWaveBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log?.Invoke($"Could not inspect configured sound file ({ex.GetType().Name}).");
            return false;
        }
    }

    private static bool TryGetDefaultSound(string eventKey, out SystemSound sound)
    {
        if (string.Equals(eventKey, Completed, StringComparison.OrdinalIgnoreCase))
        {
            sound = SystemSounds.Asterisk;
            return true;
        }

        if (string.Equals(eventKey, Failed, StringComparison.OrdinalIgnoreCase))
        {
            sound = SystemSounds.Hand;
            return true;
        }

        if (string.Equals(eventKey, QueueFinished, StringComparison.OrdinalIgnoreCase))
        {
            sound = SystemSounds.Exclamation;
            return true;
        }

        sound = SystemSounds.Asterisk;
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            if (_activeCalls == 0) DisposeResources();
        }
    }

    private void DisposeResources()
    {
        _shutdown.Dispose();
        _playGate.Dispose();
    }
}
