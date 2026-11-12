using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

internal sealed record ToolInvocation(string ToolPath, IReadOnlyList<string> Arguments)
{
    /// <summary>Written once, then cleared. Stdin is closed either way, because an open one hangs the tool at a prompt.</summary>
    public byte[]? StandardInput { get; init; }

    /// <summary>Characters of stdout to keep. Null keeps only a tail, for a run whose output is read as it arrives.</summary>
    public long? KeepOutputCharacters { get; init; }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public Action<string>? OnOutput { get; init; }

    public Action<Process>? Started { get; init; }

    /// <summary>Asked while the tool runs, a few times a second. A failure it returns stops the tool and is thrown.</summary>
    public Func<ExtractionException?>? Check { get; init; }
}

internal sealed record ToolRun(int ExitCode, string Output, string Errors)
{
    /// <summary>The archive the run was about, as the tool was given it. Its name is left out when messages are read.</summary>
    public string Subject { get; init; } = string.Empty;
}

/// <summary>
/// Runs the tool with an argument list and no shell. Both pipes are always drained and only a
/// bounded amount is kept, so a noisy process can neither hang on a full pipe nor grow memory.
/// </summary>
internal static class ToolProcess
{
    private const int TailCharacters = 32 * 1024;
    private static readonly TimeSpan PumpGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public static async Task<ToolRun> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(invocation.ToolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(invocation.ToolPath) ?? string.Empty,
        };
        foreach (var argument in invocation.Arguments) startInfo.ArgumentList.Add(argument);

        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (invocation.StandardInput is { } unused) CryptographicOperations.ZeroMemory(unused);
            throw new ExtractionException(ExtractionFailureKind.ToolUnavailable, $"The extractor could not be started: {exception.GetType().Name}.");
        }

        // If this process dies, the tool goes with it. Left running it would keep writing into a
        // staging folder the next start needs to wipe.
        using var lifetime = ToolLifetime.Tie(process);

        var lastActivity = Stopwatch.GetTimestamp();
        var stdout = new BoundedText(invocation.KeepOutputCharacters);
        var stderr = new BoundedText(null);
        using var pumpStop = new CancellationTokenSource();

        var outputPump = PumpAsync(process.StandardOutput, stdout, invocation.OnOutput, () => Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp()), pumpStop.Token);
        var errorPump = PumpAsync(process.StandardError, stderr, null, () => Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp()), pumpStop.Token);

        try
        {
            invocation.Started?.Invoke(process);
            await WriteStandardInputAsync(process, invocation.StandardInput);

            var exited = process.WaitForExitAsync(CancellationToken.None);
            var poll = TimeSpan.FromMilliseconds(Math.Clamp(invocation.StallTimeout.TotalMilliseconds / 4, 20, 250));
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(poll, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();

                if (stdout.Overflowed)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, "The archive's listing is larger than the limit allows.");
                }

                if (!exited.IsCompleted && invocation.Check?.Invoke() is { } stop)
                {
                    throw stop;
                }

                if (!exited.IsCompleted && Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity)) > invocation.StallTimeout)
                {
                    throw new ExtractionException(ExtractionFailureKind.Unknown, $"The extractor produced no output for {invocation.StallTimeout.TotalSeconds:0} seconds and was stopped.");
                }
            }

            pumpStop.CancelAfter(PumpGrace);
            await Task.WhenAll(outputPump, errorPump);

            if (stdout.Overflowed)
            {
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, "The archive's listing is larger than the limit allows.");
            }

            return new ToolRun(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch
        {
            await KillTreeAsync(process);
            await pumpStop.CancelAsync();
            await Task.WhenAll(outputPump, errorPump);
            throw;
        }
    }

    private static async Task WriteStandardInputAsync(Process process, byte[]? bytes)
    {
        try
        {
            if (bytes is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes);
                await process.StandardInput.BaseStream.FlushAsync();
            }
        }
        catch (IOException)
        {
            // The process stopped reading, which its exit code will explain.
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Same as above: already gone.
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, BoundedText sink, Action<string>? onText, Action touched, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer, cancellationToken);
                if (count == 0) return;
                touched();
                sink.Append(buffer, count);
                try
                {
                    onText?.Invoke(new string(buffer, 0, count));
                }
                catch (Exception)
                {
                    // A failing progress observer must not stop this loop: an undrained pipe
                    // would block the tool until the stall timeout.
                    onText = null;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopped on purpose, or the pipe broke because the process is gone.
        }
    }

    private static async Task KillTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var wait = new CancellationTokenSource(KillWait);
            await process.WaitForExitAsync(wait.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException or NotSupportedException)
        {
            // Already gone, or it will not die; removing staging will report the second case.
        }
    }

    /// <summary>Keeps all text up to a limit, or a rolling tail when no limit is given. Pump threads write, the runner reads.</summary>
    private sealed class BoundedText(long? keepAllUpTo)
    {
        private readonly StringBuilder _text = new();
        private volatile bool _overflowed;

        public bool Overflowed => _overflowed;

        public void Append(char[] buffer, int count)
        {
            lock (_text)
            {
                if (keepAllUpTo is { } limit)
                {
                    if (_text.Length + count > limit)
                    {
                        _overflowed = true;
                        return;
                    }

                    _text.Append(buffer, 0, count);
                    return;
                }

                _text.Append(buffer, 0, count);
                if (_text.Length > TailCharacters) _text.Remove(0, _text.Length - TailCharacters);
            }
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }
}
