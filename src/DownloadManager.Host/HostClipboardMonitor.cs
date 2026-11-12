using System.Runtime.InteropServices;
using System.Windows.Forms;
using DownloadManager.Core;

namespace DownloadManager.Host;

/// <summary>
/// Watches the Windows clipboard on the Host UI thread and raises HTTP(S) URLs. Windows tells a message-only
/// window when the clipboard changes (<c>WM_CLIPBOARDUPDATE</c>), so nothing wakes the Host while nothing is
/// copied (L6; this used to poll every 600 ms). Clipboard access requires STA, which the Host main thread provides.
/// </summary>
internal sealed class HostClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Func<bool> _enabled;
    private readonly Action<string> _onUrl;
    private readonly Func<string, string> _describe;
    private readonly Action<string> _log;
    private readonly ListenerWindow _window;
    // Only runs after a read found the clipboard held by another program (ClipboardRetrySchedule), then it stops.
    private readonly System.Windows.Forms.Timer _retry;
    private int _retryAttempt;
    private TimeSpan _retryWaited;
    private bool _listening;
    private string? _lastText;
    private string? _lastUrl;
    private DateTimeOffset _lastUrlAt;
    private bool _disposed;

    /// <param name="describe">What the log may say about a copied address.</param>
    public HostClipboardMonitor(Func<bool> enabled, Action<string> onUrl, Func<string, string> describe, Action<string> log)
    {
        _enabled = enabled;
        _onUrl = onUrl;
        _describe = describe;
        _log = log;
        _window = new ListenerWindow(this);
        _retry = new System.Windows.Forms.Timer();
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Read();
        };
    }

    /// <summary>Listens only while monitoring is on, so the Host doesn't wake for it otherwise. Call on the UI thread.</summary>
    public void Start() => Apply();

    /// <summary>Starts or stops listening to match the setting. Call on the UI thread.</summary>
    public void Apply()
    {
        if (_disposed) return;
        if (_enabled()) StartListening();
        else StopListening();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListening();
        _retry.Dispose();
    }

    private void StartListening()
    {
        if (_listening) return;
        try
        {
            if (_window.Handle == IntPtr.Zero)
                _window.CreateHandle(new CreateParams { Parent = HwndMessage });
            if (!AddClipboardFormatListener(_window.Handle))
            {
                _log($"Clipboard monitor could not start (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            _listening = true;
        }
        catch (Exception ex)
        {
            _log($"Clipboard monitor could not start: {ex.Message}");
            return;
        }

        // As the poll did on its first tick: what is already copied counts once.
        OnClipboardUpdate();
    }

    private void StopListening()
    {
        _retry.Stop();
        if (_listening && _window.Handle != IntPtr.Zero) RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
        if (_window.Handle != IntPtr.Zero) _window.DestroyHandle();
    }

    private void OnClipboardUpdate()
    {
        _retry.Stop();
        _retryAttempt = 0;
        _retryWaited = TimeSpan.Zero;
        Read();
    }

    private void Read()
    {
        try
        {
            if (_disposed || !_listening || !_enabled())
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
            _log($"Clipboard URL detected: {_describe(url)}");
            _onUrl(url);
        }
        catch (ExternalException ex)
        {
            // Another process owns the clipboard; try again, less often, for about half a minute.
            if (ClipboardRetrySchedule.Delay(_retryAttempt, _retryWaited) is { } delay)
            {
                _retryAttempt++;
                _retryWaited += delay;
                _retry.Interval = (int)delay.TotalMilliseconds;
                _retry.Start();
            }
            else
            {
                _log($"Clipboard read skipped: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _log($"Clipboard monitor error: {ex.Message}");
        }
    }

    private sealed class ListenerWindow(HostClipboardMonitor owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClipboardUpdate)
            {
                owner.OnClipboardUpdate();
                return;
            }

            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
