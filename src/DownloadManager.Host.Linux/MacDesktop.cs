using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Host;

/// <summary>
/// MAC.5: the desktop services the Linux Host reaches over D-Bus (logind, notifications, the keyring), on macOS through
/// the system's own command-line tools, which every Mac has: <c>caffeinate</c>, <c>osascript</c>, <c>security</c>.
/// </summary>
internal static class MacDesktop
{
    /// <summary>
    /// A notification through AppleScript's <c>display notification</c>. The title and text go in as arguments, never
    /// into the script, so no text can change what it runs. Never throws and never waits.
    /// </summary>
    public static void Notify(string title, string message, Action<string> warn)
    {
        try
        {
            using var _ = Process.Start(NotifyStart(title, message));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            warn($"Notifications are unavailable: {ex.GetType().Name}");
        }
    }

    public static ProcessStartInfo NotifyStart(string title, string message)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
                     title, message,
                 })
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// The completion action's power-off, restart or sign-out, as Apple events to loginwindow: the "really" ones, which
    /// ask open apps to quit, as Windows' shutdown.exe does, without a confirmation dialog of their own. Unlike System
    /// Events, loginwindow takes them without an Automation permission prompt.
    /// </summary>
    public static (string Program, List<string> Arguments, string What) Shutdown(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "restart" => ("/usr/bin/osascript", ["-e", "tell application \"loginwindow\" to «event aevtrrst»"], "restart"),
            "logoff" => ("/usr/bin/osascript", ["-e", "tell application \"loginwindow\" to «event aevtrlgo»"], "sign out"),
            _ => ("/usr/bin/osascript", ["-e", "tell application \"loginwindow\" to «event aevtrsdn»"], "shut down"),
        };
}

/// <summary>
/// MAC.5: keeps the Mac awake while downloads run, as Windows' power request and Linux's logind inhibitor do:
/// <c>caffeinate -i</c> (no idle sleep; the display may still turn off) for as long as it is wanted, tied to this Host by
/// <c>-w</c>, so it ends with the Host even if the Host is killed.
/// </summary>
internal sealed class MacSleepPrevention(Action<string> log) : ISleepPrevention
{
    private readonly object _gate = new();
    private Process? _caffeinate;
    private bool _warned;

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (active == (_caffeinate is { HasExited: false })) return;
            if (!active)
            {
                Stop();
                log("Sleep prevention released.");
                return;
            }

            try
            {
                _caffeinate?.Dispose();
                _caffeinate = Process.Start(Start(Environment.ProcessId));
                log("Sleep prevention held.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _caffeinate = null;
                if (!_warned) log($"Sleep prevention is unavailable: {ex.GetType().Name}");
                _warned = true;
            }
        }
    }

    public static ProcessStartInfo Start(int hostProcessId)
    {
        var start = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add(hostProcessId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    private void Stop()
    {
        if (_caffeinate is null) return;
        try
        {
            if (!_caffeinate.HasExited) _caffeinate.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _caffeinate.Dispose();
        _caffeinate = null;
    }

    public void Dispose()
    {
        lock (_gate) Stop();
    }
}

/// <summary>
/// MAC.5: the secret key that seals saved passwords and keys, in the user's login Keychain, in place of DPAPI on
/// Windows and the Secret Service on Linux: a copy of the data folder alone opens nothing. One generic password per
/// data folder (service <see cref="Service"/>, account the folder's path), base64. The commands go to
/// <c>security -i</c> on its standard input, so the key never shows in a process list.
/// </summary>
internal sealed class MacKeychainKeyStore : ISecretKeyStore
{
    public const string Service = "Inbound Download Manager secret key";
    private const string Security = "/usr/bin/security";
    private readonly string _account;

    private MacKeychainKeyStore(string dataFolder) => _account = Path.GetFullPath(dataFolder);

    public string Describe => "the login Keychain";

    /// <summary>Null when there is no Keychain to use (no <c>security</c>, or no login keychain); the reason is logged.</summary>
    public static MacKeychainKeyStore? TryOpen(string dataFolder, Action<string> log)
    {
        try
        {
            var (code, output, _) = Run(["default-keychain"], null);
            if (code == 0 && output.Trim().Length > 0) return new MacKeychainKeyStore(dataFolder);
            log("No login Keychain: security default-keychain gave none.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log($"No login Keychain: {ex.GetType().Name}");
        }

        return null;
    }

    public byte[]? TryRead()
    {
        var (code, output, error) = Run(["find-generic-password", "-s", Service, "-a", _account, "-w"], null);
        // 44: errSecItemNotFound, "The specified item could not be found in the keychain."
        if (code == 44 || error.Contains("could not be found", StringComparison.OrdinalIgnoreCase)) return null;
        if (code != 0) throw new IOException($"The login Keychain could not be read (security exit {code}).");
        try
        {
            return Convert.FromBase64String(output.Trim());
        }
        catch (FormatException)
        {
            throw new IOException("The login Keychain's Inbound key is not in the expected form.");
        }
    }

    public void Create(byte[] key)
    {
        var encoded = Convert.ToBase64String(key);
        // security -i's exit code doesn't say whether the command in it worked: the read-back below does.
        var (code, _, _) = Run(["-i"], AddCommand(_account, encoded) + "\n");
        if (code != 0) throw new IOException($"The key could not be kept in the login Keychain (security exit {code}).");
        if (TryRead() is not { } back || !CryptographicOperations.FixedTimeEquals(back, key))
            throw new IOException("The key kept in the login Keychain did not read back the same.");
    }

    /// <summary>One <c>security -i</c> line: arguments in double quotes, as its own parser takes them.</summary>
    public static string AddCommand(string account, string encodedKey) =>
        $"add-generic-password -U -s {Quote(Service)} -a {Quote(account)} -w {Quote(encodedKey)}";

    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output, string Error) Run(string[] arguments, string? input)
    {
        var start = new ProcessStartInfo(Security)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("security did not start.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            // A locked Keychain asks the user to unlock it; two minutes, as the Secret Service prompt.
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new IOException("The login Keychain did not answer (locked, and not unlocked in time?).");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}

/// <summary>
/// MAC.5: the event sounds, by the Windows Host's rules (CompletionSoundPlayer): no setting plays the system sound for
/// a finished download only, <c>@system</c> the system sound, an empty one nothing, and a file that file, or the
/// system sound when it is gone. Played with <c>afplay</c>, one at a time, for at most 30 seconds.
/// </summary>
internal sealed class MacSounds(Action<string> log) : IDisposable
{
    public const string SystemSound = "@system";
    private readonly object _gate = new();
    private Process? _playing;

    /// <summary>The file to play for the event, or null for none.</summary>
    public static string? Resolve(string eventKey, IReadOnlyDictionary<string, string>? files, Func<string, bool> exists)
    {
        var system = eventKey.ToLowerInvariant() switch
        {
            "completed" => "/System/Library/Sounds/Glass.aiff",
            "failed" => "/System/Library/Sounds/Basso.aiff",
            "queuefinished" => "/System/Library/Sounds/Hero.aiff",
            _ => null,
        };
        if (system is null) return null;
        string? setting = null;
        if (files is not null)
            foreach (var pair in files)
                if (string.Equals(pair.Key, eventKey, StringComparison.OrdinalIgnoreCase)) { setting = pair.Value; break; }

        if (setting is null) return string.Equals(eventKey, "completed", StringComparison.OrdinalIgnoreCase) ? system : null;
        if (string.Equals(setting, SystemSound, StringComparison.OrdinalIgnoreCase)) return system;
        if (setting.Length == 0) return null;
        return exists(setting) ? setting : system;
    }

    public void Play(string eventKey, IReadOnlyDictionary<string, string>? files)
    {
        if (Resolve(eventKey, files, File.Exists) is not { } sound) return;
        lock (_gate)
        {
            if (_playing is { HasExited: false })
            {
                log("An event sound is already playing; the new sound was skipped.");
                return;
            }

            _playing?.Dispose();
            try
            {
                var start = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false };
                foreach (var argument in new[] { "-t", "30", sound }) start.ArgumentList.Add(argument);
                _playing = Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _playing = null;
                log($"Event sound failed: {ex.GetType().Name}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _playing?.Dispose();
            _playing = null;
        }
    }
}

/// <summary>
/// MAC.5: which copied text becomes a download, by the Windows Host's rules (HostClipboardMonitor): the first http(s)
/// address in text that changed, and not the same address again within a minute.
/// </summary>
internal sealed class CopiedUrlFilter
{
    private string? _lastText;
    private string? _lastUrl;
    private DateTimeOffset _lastUrlAt;

    public string? Accept(string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _lastText, StringComparison.Ordinal)) return null;
        _lastText = text;
        if (ClipboardUrlExtractor.FirstHttpUrl(text) is not { } url) return null;
        if (string.Equals(url, _lastUrl, StringComparison.OrdinalIgnoreCase) && now - _lastUrlAt < TimeSpan.FromMinutes(1)) return null;
        _lastUrl = url;
        _lastUrlAt = now;
        return url;
    }
}
