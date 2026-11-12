#if !HAS_UNO
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
#endif
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.B28: keeps a window opened in this process from another (the main window, a package's window) in front once the
/// double-click, Enter or menu click that opened it has ended (<see cref="WindowFrontRules"/>). Call it right after the
/// window was shown.
/// </summary>
internal static class WindowFront
{
    public static void KeepInFrontAfterInput(Window window, Window openedFrom)
    {
        // Win32 foreground calls; elsewhere the window manager decides.
        if (!OperatingSystem.IsWindows()) return;
#if !HAS_UNO
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var opener = WinRT.Interop.WindowNative.GetWindowHandle(openedFrom);
        var closed = false;
        // Removed again once the watch is over, so reopening the same window many times adds nothing that stays.
        TypedEventHandler<object, WindowEventArgs> onClosed = (_, _) => closed = true;
        window.Closed += onClosed;
        var started = Stopwatch.StartNew();
        Stopwatch? sinceInputEnded = null;
        var bringBacks = 0;
        var checkQueued = false;
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = WindowFrontRules.Poll;
        timer.IsRepeating = true;
        void Stop()
        {
            timer.Stop();
            window.Closed -= onClosed;
        }
        timer.Tick += (_, _) =>
        {
            if (sinceInputEnded is null)
            {
                if (!closed && WindowFrontRules.KeepWaiting(InputHeld(), started.Elapsed)) return;
                sinceInputEnded = Stopwatch.StartNew();
            }
            else if (!WindowFrontRules.KeepWatching(!closed && IsWindowVisible(hwnd), AnyInputHeld(), sinceInputEnded.Elapsed, bringBacks))
            {
                Stop();
                return;
            }
            if (checkQueued) return;
            // After whatever the opener still has to do with that input: it runs before a low-priority turn. The watch
            // goes on for a moment (WindowFrontRules.Settle), as a release the opener had not handled yet can still
            // take the front back after this turn.
            checkQueued = window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                checkQueued = false;
                var front = GetForegroundWindow();
                if (!WindowFrontRules.BringBack(!closed && IsWindowVisible(hwnd), front == hwnd, front == opener)) return;
                bringBacks++;
                UiTrace.Write("A window fell behind the window it was opened from; brought it forward again");
                window.Activate();
                SetForegroundWindow(hwnd);
            });
            if (!checkQueued) Stop();
        };
        timer.Start();
#endif
    }

#if !HAS_UNO
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkReturn = 0x0D;

    /// <summary>A mouse button or Enter is down now.</summary>
    private static bool InputHeld()
        => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkReturn) & 0x8000) != 0;

    private const int VkMiddleButton = 0x04;
    private const int VkTab = 0x09;
    private const int VkAlt = 0x12;
    private const int VkEscape = 0x1B;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;

    /// <summary>
    /// After the opening input: a button, Enter or a key that switches windows (Alt, Tab, Escape, Windows) is down, so
    /// the user is doing something new and the window they go to keeps the front.
    /// </summary>
    private static bool AnyInputHeld()
        => InputHeld()
        || (GetAsyncKeyState(VkMiddleButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkTab) & 0x8000) != 0
        || (GetAsyncKeyState(VkAlt) & 0x8000) != 0
        || (GetAsyncKeyState(VkEscape) & 0x8000) != 0
        || (GetAsyncKeyState(VkLeftWin) & 0x8000) != 0
        || (GetAsyncKeyState(VkRightWin) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
#endif
}
