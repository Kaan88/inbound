#if INBOUND_RCLONE_PROBE
using System.Diagnostics;
using Android.Util;
using DownloadManager.Sites;

namespace Inbound.App;

// Emulator feasibility only. Uses no account, stored remote or user configuration.
internal static class RcloneProbe
{
    private const string Tag = "InboundRcloneProbe";
    private static int _running;

    public static void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var app = Android.App.Application.Context;
                var binary = Path.Combine(app.ApplicationInfo!.NativeLibraryDir!, "librclone.so");
                var state = Path.Combine(app.CacheDir!.AbsolutePath, "rclone-probe");
                Directory.CreateDirectory(state);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var ct = timeout.Token;
                var version = await Capture(binary, ct, "version");
                if (!version.Contains("os/type: android") || !version.Contains("os/arch: amd64"))
                    throw new InvalidOperationException("Unexpected rclone version output: " + version);
                Log.Info(Tag, version);
                var listing = await Capture(binary, ct, "lsf", ":http:", "--http-url",
                    "https://downloads.rclone.org/v1.76.0/", "--config", "/dev/null", "--contimeout", "10s", "--retries", "1");
                if (string.IsNullOrWhiteSpace(listing)) throw new InvalidOperationException("Empty HTTPS listing");
                Log.Info(Tag, "HTTPS listing succeeded");
                await using var process = await RcloneProcess.StartAsync(binary, state, cancellationToken: ct);
                await process.Client.CallAsync("rc/noop", cancellationToken: ct);
                if (!await process.StopAsync(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("rclone process did not exit");
                Log.Info(Tag, $"PASS: authenticated rc/noop and child {process.ProcessId} exited");
            }
            catch (Exception ex) { Log.Error(Tag, "FAIL: " + ex); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    private static async Task<string> Capture(string binary, CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch rclone");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"rclone exited {process.ExitCode}: {error}");
            return await stdout;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }
}
#endif
