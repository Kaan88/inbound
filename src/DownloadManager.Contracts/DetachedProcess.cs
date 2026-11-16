using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DownloadManager.Contracts;

/// <summary>
/// Starts a program that must outlive its starter (the Host, started by the window, the CLI or the native host), with no
/// standard handles of the starter's. Linux: <c>setsid -f</c>. macOS has no setsid(1): the shell puts the program in the
/// background and the program calls <see cref="LeaveSession"/> itself (the Host does, first thing).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public static class DetachedProcess
{
    public static void Start(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(OperatingSystem.IsMacOS()
            ? "\"$0\" \"$@\" >/dev/null 2>&1 </dev/null &"
            : "exec setsid -f \"$0\" \"$@\" >/dev/null 2>&1 </dev/null");
        start.ArgumentList.Add(program);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        process?.WaitForExit(TimeSpan.FromSeconds(5));
    }

    /// <summary>Makes this process the leader of a new session; a no-op when it already leads one.</summary>
    public static void LeaveSession() => _ = SetSid();

    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int SetSid();
}
