using System.Runtime.InteropServices;
using System.Windows.Forms;
using DownloadManager.Core;

namespace DownloadManager.Host;

/// <summary>
/// Polls the Windows clipboard on the Host UI thread and raises HTTP(S) URLs.
/// Clipboard access requires STA, which the Host main thread provides.
/// </summary>
internal sealed class HostClipboardMonitor : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Action<string> _onUrl;
    private readonly Action<string> _log;
    private readonly System.Windows.Forms.Timer _timer;
    private string? _lastText;
    private string? _lastUrl;
    private DateTimeOffset _lastUrlAt;

    public HostClipboardMonitor(Func<bool> enabled, Action<string> onUrl, Action<string> log)
    {
        _enabled = enabled;
        _onUrl = onUrl;
        _log = log;
        _timer = new System.Windows.Forms.Timer { Interval = 600 };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private void Poll()
    {
        try
        {
            if (!_enabled())
            {
                return;
            }

            if (!Clipboard.ContainsText())
            {
                return;
            }

            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                return;
            }

            _lastText = text;
            var url = ClipboardUrlExtractor.FirstHttpUrl(text);
            if (url is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(url, _lastUrl, StringComparison.OrdinalIgnoreCase) &&
                now - _lastUrlAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _lastUrl = url;
            _lastUrlAt = now;
            _log($"Clipboard URL detected: {url}");
            _onUrl(url);
        }
        catch (ExternalException ex)
        {
            // Another process owns the clipboard; retry on the next tick.
            _log($"Clipboard read skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log($"Clipboard monitor error: {ex.Message}");
        }
    }
}
