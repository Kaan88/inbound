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

    /// <summary>U-WL-18: a status line's name with its words, said once when the words already start with the name.</summary>
    public static string SpokenStatus(string name, string text)
    {
        if (text.Length == 0) return name;
        return text.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? text : $"{name}: {text}";
    }

    /// <summary>
    /// The folder that holds every one of a package's files, or null when they have none in common or none is known.
    /// U-WL-14: what Open Folder opens for a package row.
    /// </summary>
    public static string? CommonFolder(IEnumerable<string?> folders)
    {
        string[]? common = null;
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var parts = Path.TrimEndingDirectorySeparator(folder).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (common is null) { common = parts; continue; }
            var same = 0;
            while (same < common.Length && same < parts.Length && string.Equals(common[same], parts[same], StringComparison.OrdinalIgnoreCase)) same++;
            common = common[..same];
        }

        if (common is null || common.Length == 0) return null;
        var joined = string.Join(Path.DirectorySeparatorChar, common);
        return joined.EndsWith(':') ? joined + Path.DirectorySeparatorChar : joined;
    }

    /// <summary>
    /// U-WL-24: how a package's files are named in its window. A name two files share gets the folder it is in
    /// below the folder they all share, such as <c>subideo.bin</c>, so the two can be told apart.
    /// </summary>
    public static IReadOnlyList<string> PackageFileNames(IReadOnlyList<(string? Folder, string Name)> files)
    {
        var shared = files.GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (shared.Count == 0) return files.Select(file => file.Name).ToList();
        var root = CommonFolder(files.Select(file => file.Folder));
        return files.Select(file =>
        {
            if (!shared.Contains(file.Name) || string.IsNullOrWhiteSpace(file.Folder) || root is null) return file.Name;
            var relative = Path.GetRelativePath(root, file.Folder);
            return relative == "." ? file.Name : Path.Combine(relative, file.Name);
        }).ToList();
    }

    /// <summary>
    /// U-WL-16: the progress window's title. A percentage only while it moves and the size is known; otherwise the
    /// state, so a failed download doesn't read "0.0%".
    /// </summary>
    public static string ProgressTitle(string state, bool complete, double? percent, string name, string stateWords)
    {
        if (complete) return $"Download complete — {name}";
        if (percent is { } done && state is "downloading" or "connecting" or "verifying") return $"{done:0.0}% — {name}";
        return $"{stateWords} — {name}";
    }

    private static string Rows(int count) => count == 1 ? "1 download" : $"{count} downloads";
}

/// <summary>What Repair runs: the script and the app folder to register, or why it won't run.</summary>
internal sealed record BrowserRepairPlan(string? Refusal, string? Script, string? AppDirectory);
