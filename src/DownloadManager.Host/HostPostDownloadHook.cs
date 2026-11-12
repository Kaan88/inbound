using System.Diagnostics;
using System.Text;
using DownloadManager.Core;

namespace DownloadManager.Host;

internal sealed class HostPostDownloadHook : IPostDownloadHook
{
    /// <summary>How long output is still read once the program has exited: a child it left running can hold the pipe open.</summary>
    internal static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private readonly Action<string> _log;

    public HostPostDownloadHook(Action<string> log)
    {
        _log = log;
    }

    public async Task RunAsync(string command, string filePath, CancellationToken cancellationToken)
    {
        var start = PostDownloadCommandLine.Create(command, filePath);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The post-download program could not be started.");
        }

        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = new CapturedOutput(process.StandardOutput, CapturedOutput.MaxChars, reading.Token);
        var stderr = new CapturedOutput(process.StandardError, CapturedOutput.MaxChars, reading.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw new TimeoutException("The post-download program timed out.");
        }

        // Both streams share one grace: waited for one after the other, a child holding both pipes open would hold the
        // download's completion for twice OutputGrace.
        var (output, error) = await CapturedOutput.FinishBothAsync(stdout, stderr, OutputGrace).ConfigureAwait(false);
        await reading.CancelAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The post-download program exited with {process.ExitCode}" +
                (error.Length == 0 ? "." : $": {error}"));
        }

        if (output.Length > 0)
        {
            _log($"Post-download program output: {output}");
        }
    }

    /// <summary>
    /// Reads one of the program's streams to its end, so the program never blocks on a full pipe, but keeps only the
    /// first <c>maxChars</c> characters (audit L12a): a chatty program can't fill the Host's memory or its log.
    /// </summary>
    internal sealed class CapturedOutput
    {
        public const int MaxChars = 2000;
        public const string TruncatedMark = " [output truncated]";

        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly int _maxChars;
        private readonly Task _reading;
        private bool _truncated;

        public CapturedOutput(TextReader reader, int maxChars, CancellationToken cancellationToken)
        {
            _maxChars = maxChars;
            _reading = Task.Run(() => ReadAsync(reader, cancellationToken), CancellationToken.None);
        }

        private async Task ReadAsync(TextReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                lock (_gate)
                {
                    var keep = Math.Min(read, _maxChars - _text.Length);
                    if (keep > 0) _text.Append(buffer, 0, keep);
                    if (keep < read) _truncated = true;
                }
            }
        }

        /// <summary>Both streams' text, trimmed, waiting at most <paramref name="grace"/> in all for the two to end.</summary>
        public static async Task<(string Output, string Error)> FinishBothAsync(CapturedOutput stdout, CapturedOutput stderr, TimeSpan grace)
        {
            var output = stdout.FinishAsync(grace);
            var error = stderr.FinishAsync(grace);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return ((await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
        }

        /// <summary>What was read, waiting at most <paramref name="grace"/> for the stream to end.</summary>
        public async Task<string> FinishAsync(TimeSpan grace)
        {
            var open = false;
            try
            {
                await _reading.WaitAsync(grace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still open (a child of the program holds it): keep what arrived.
                open = true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Keep what arrived.
            }

            lock (_gate)
            {
                return _text.ToString() + (_truncated || open ? TruncatedMark : "");
            }
        }
    }
}
