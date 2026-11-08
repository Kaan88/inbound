using System.Diagnostics;
using DownloadManager.Core;

namespace DownloadManager.Core;

/// <summary>
/// FFmpeg-backed muxer. Arguments are passed through ArgumentList (never a
/// shell), the output is validated, and a timeout kills a stuck process.
/// </summary>
public sealed class FfmpegMediaMuxer : IMediaMuxer
{
    private readonly Func<string?> _pathProvider;

    public FfmpegMediaMuxer(Func<string?> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public async Task<MediaMuxResult> MuxAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(videoPath) || !File.Exists(audioPath))
        {
            return new MediaMuxResult(false, "A media track is missing before muxing.");
        }

        var executable = ResolveFfmpeg(_pathProvider());
        if (executable is null)
        {
            return new MediaMuxResult(false,
                "FFmpeg is required for audio/video muxing. Set the ffmpeg path in Settings or PATH.");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-i", videoPath,
                     "-i", audioPath,
                     "-map", "0:v:0", "-map", "1:a:0",
                     "-c", "copy",
                     "-movflags", "+faststart",
                     "-map_metadata", "-1",
                     "-fflags", "+bitexact",
                     "-flags", "+bitexact",
                     outputPath,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            return new MediaMuxResult(false, "FFmpeg could not be started.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // process already exited
            }

            return new MediaMuxResult(false,
                cancellationToken.IsCancellationRequested
                    ? "Muxing was cancelled."
                    : "FFmpeg muxing timed out.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr)
                ? $"exit code {process.ExitCode}"
                : stderr.Trim();
            if (detail.Length > 800)
            {
                detail = detail[^800..];
            }

            return new MediaMuxResult(false, $"FFmpeg muxing failed: {detail}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            return new MediaMuxResult(false, "FFmpeg reported success but produced no output.");
        }

        return new MediaMuxResult(true, null);
    }

    internal static string? ResolveFfmpeg(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        return FindOnPath(Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>The first FFmpeg in the folders of a <c>PATH</c> value. Linux programs have no extension: an image installs /usr/bin/ffmpeg.</summary>
    internal static string? FindOnPath(string? path)
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        path ??= string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
