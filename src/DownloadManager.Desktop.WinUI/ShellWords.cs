namespace DownloadManager.Desktop.WinUI;

/// <summary>Words the main window shows about its own state. Nothing here touches the UI, so it can be tested.</summary>
internal static class ShellWords
{
    /// <summary>U-WL-8: the bar over a list that Find has filtered, or null when nothing filters it.</summary>
    public static string? FindFilter(string? term, int shown)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        return shown == 0
            ? $"No downloads match “{term.Trim()}”."
            : $"Showing {Rows(shown)} that match “{term.Trim()}”.";
    }

    /// <summary>U-WL-8: what the status bar adds to its counts while Find filters the list, or null.</summary>
    public static string? FindCount(string? term, int shown)
        => string.IsNullOrWhiteSpace(term) ? null : $"{shown} shown by Find";

    /// <summary>U-WL-9: the browser part of the status bar, from the Host's integration status, or null when it has none.</summary>
    public static string? BrowserIntegration(string? status)
        => string.IsNullOrWhiteSpace(status) || status == "unknown" ? null
            : status == "native host not registered" ? "browser integration not registered"
            : $"browser integration {status}";

    /// <summary>U-WL-9: whether the status names a registration that Repair could put right. Off by choice isn't one.</summary>
    public static bool IntegrationNeedsRepair(string? status)
        => status is { Length: > 0 } text && (text == "native host not registered" || text.StartsWith("on; ", StringComparison.Ordinal));

    /// <summary>
    /// U-WL-9: what Repair would run. Setup puts <c>register-native-host.ps1</c> beside the <c>app</c> folder this runs
    /// from. A test or portable instance must never write the installed user's browser registration, so it gets a
    /// reason instead, as does a build that isn't installed.
    /// </summary>
    public static BrowserRepairPlan RepairPlan(string baseDirectory, string? dataOverride, string? instance, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(dataOverride) || !string.IsNullOrWhiteSpace(instance))
            return new BrowserRepairPlan("This is a separate test or portable copy of Inbound, so it doesn't change the browser registration of the installed app.", null, null);
        var appDirectory = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Path.GetDirectoryName(appDirectory);
        var script = installRoot is null ? null : Path.Combine(installRoot, "register-native-host.ps1");
        if (script is null || !fileExists(script) || !fileExists(Path.Combine(appDirectory, "DownloadManager.NativeHost.exe")))
            return new BrowserRepairPlan("This copy of Inbound wasn't installed by Setup, so there is nothing here to repair the registration with. Run Setup again to repair it.", null, null);
        return new BrowserRepairPlan(null, script, appDirectory);
    }

    private static string Rows(int count) => count == 1 ? "1 download" : $"{count} downloads";
}

/// <summary>What Repair runs: the script and the app folder to register, or why it won't run.</summary>
internal sealed record BrowserRepairPlan(string? Refusal, string? Script, string? AppDirectory);
