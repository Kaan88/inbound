using System.Diagnostics;
using System.Text;

namespace DownloadManager.Runtime;

/// <summary>
/// Renders a page with an installed Chromium browser (Edge or Chrome) in headless mode and
/// returns the DOM after scripts ran (<c>--dump-dom</c>). No browser is bundled: the path
/// comes from <c>DM_RENDER_BROWSER</c> or the standard Edge/Chrome install locations.
/// The browser makes its own unauthenticated, direct requests; callers must only render
/// pages they already fetched and cleared (robots, scope) themselves.
/// </summary>
public sealed class HeadlessRenderer
{
    public const string BrowserEnvironmentVariable = "DM_RENDER_BROWSER";

    private readonly string _browserPath;
    private readonly string _profileDirectory;
    private readonly TimeSpan _timeout;

    private HeadlessRenderer(string browserPath, string profileDirectory, TimeSpan timeout)
    {
        _browserPath = browserPath;
        _profileDirectory = profileDirectory;
        _timeout = timeout;
    }

    public string BrowserPath => _browserPath;

    public static string? FindBrowser()
    {
        var configured = Environment.GetEnvironmentVariable(BrowserEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static HeadlessRenderer? TryCreate(string profileDirectory, TimeSpan? timeout = null)
    {
        var browser = FindBrowser();
        return browser is null
            ? null
            : new HeadlessRenderer(browser, profileDirectory, timeout ?? TimeSpan.FromSeconds(30));
    }

    public async Task<string> RenderAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only http and https pages can be rendered.", nameof(url));
        }

        Directory.CreateDirectory(_profileDirectory);
        var start = new ProcessStartInfo(_browserPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-sync", "--mute-audio",
                     "--disable-crash-reporter", "--disable-breakpad",
                     // In-memory cache/cookies: nothing from the crawl persists in the profile.
                     "--incognito",
                     "--enable-logging=stderr", "--v=0",
                     // Lets timers/fetches settle without a wall-clock wait.
                     "--virtual-time-budget=5000",
                     $"--user-data-dir={_profileDirectory}",
                     "--dump-dom",
                     url.AbsoluteUri,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // Collect output incrementally and wait for the *process* to exit. Process.WaitForExitAsync
        // also waits for redirected streams to reach EOF, and Chromium helper processes (crashpad
        // etc.) can inherit the pipe and outlive the browser, which would hang until the timeout.
        var output = new StringBuilder();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    if (errors.Length < 64 * 1024)
                    {
                        errors.AppendLine(e.Data);
                    }
                }
            }
        };
        process.Exited += (_, _) => exited.TrySetResult();
        if (!process.Start())
        {
            throw new InvalidOperationException("The headless browser did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await exited.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            cancellationToken.ThrowIfCancellationRequested();
            string tail;
            lock (errors)
            {
                var text = errors.ToString();
                tail = text.Length > 1500 ? text[^1500..] : text;
            }

            throw new TimeoutException(
                $"Rendering {url} took longer than {_timeout.TotalSeconds:0} s. Browser log tail: {tail.Trim()}");
        }

        // Give the async reader a moment to deliver the final lines, but do not wait for EOF.
        string html;
        var settle = Stopwatch.StartNew();
        while (true)
        {
            lock (output)
            {
                html = output.ToString();
            }

            if (html.Contains("</html>", StringComparison.OrdinalIgnoreCase) || settle.ElapsedMilliseconds > 2000)
            {
                break;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException(
                $"The headless browser exited with code {process.ExitCode} and no DOM output.");
        }

        return html;
    }
}
