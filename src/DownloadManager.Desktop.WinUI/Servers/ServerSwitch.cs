using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One line of the server switcher: This PC or a saved server, whether it is the one shown, and its shortcut.</summary>
internal sealed record ServerChoice(string? Id, string Name, bool IsCurrent, int? Number)
{
    public bool IsLocal => Id is null;

    /// <summary>"Ctrl+0" for This PC, "Ctrl+1" to "Ctrl+9" for the first nine saved servers; null after those.</summary>
    public string? ShortcutText => Number is { } number ? $"Ctrl+{number}" : null;
}

/// <summary>
/// The server switcher (UI.R26): which servers it lists, in what order, which one is shown, and the Ctrl+number keys that
/// switch straight to one. The same list fills the toolbar switcher, the status-bar switcher and Tasks > Show Downloads Of.
/// Nothing here touches the UI.
/// </summary>
internal static class ServerSwitch
{
    /// <summary>The keys go up to Ctrl+9; servers after the ninth are still listed, without a key.</summary>
    public const int MostNumbered = 9;

    public const string WindowTitle = "Inbound Download Manager";

    /// <summary>This PC first, then the saved servers in the order they were added.</summary>
    public static IReadOnlyList<ServerChoice> Choices(IEnumerable<SavedServer> saved, string? shownId)
    {
        var choices = new List<ServerChoice> { new(null, ServerTarget.Local.Name, shownId is null, 0) };
        var number = 1;
        foreach (var server in saved)
        {
            var current = shownId is not null && string.Equals(server.Id, shownId, StringComparison.OrdinalIgnoreCase);
            choices.Add(new ServerChoice(server.Id, server.Name, current, number <= MostNumbered ? number : null));
            number++;
        }

        return choices;
    }

    /// <summary>The choice that Ctrl+<paramref name="number"/> goes to, or null when there is none.</summary>
    public static ServerChoice? ForNumber(IReadOnlyList<ServerChoice> choices, int number) =>
        number is < 0 or > MostNumbered ? null : choices.FirstOrDefault(choice => choice.Number == number);

    /// <summary>
    /// The window's title. This PC keeps the plain name (the UI checks find the window by it); a server is named first,
    /// so the taskbar says which one the window shows.
    /// </summary>
    public static string TitleFor(ServerTarget target) => target.IsLocal ? WindowTitle : $"{target.Name} — {WindowTitle}";

    /// <summary>The switcher's tooltip and help text.</summary>
    public static string SwitcherHelp(ServerTarget target) =>
        target.IsLocal
            ? "Showing the downloads of this PC. Select to show another server's (Ctrl+Shift+O; Ctrl+0 to Ctrl+9 switch straight away)."
            : $"Showing the downloads of {target.Name}. Select to show this PC's or another server's (Ctrl+Shift+O; Ctrl+0 shows this PC).";
}

/// <summary>What the bar above the list does while another server is shown (UI.B18, U-WR-1, U-WR-3).</summary>
internal enum ServerBannerAction
{
    None,
    /// <summary>Try to connect again now, rather than at the next scheduled try.</summary>
    Retry,
    /// <summary>The server refused the pairing: pair again in the server's Edit dialog.</summary>
    PairAgain,
    /// <summary>Something only the server's Edit dialog can change (an http:// address not yet allowed).</summary>
    EditServer,
}

/// <param name="Visible">False for this PC: the bar is only about another server.</param>
/// <param name="Text">The bar's words.</param>
/// <param name="IsProblem">Shown in the error colour.</param>
/// <param name="Action">The bar's own button besides Back to This PC, which it always has.</param>
/// <param name="Stale">The rows on the list are from before the connection was lost: dimmed, and said so.</param>
/// <param name="EmptyText">What the empty list says instead of "No downloads in this view", or null for the usual words.</param>
/// <param name="Connected">The counts in the status bar are the server's.</param>
/// <param name="Status">What the status bar says instead of the counts, or null while they are the server's (or this PC's).</param>
internal sealed record ServerBanner(bool Visible, string Text, bool IsProblem, ServerBannerAction Action, bool Stale, string? EmptyText, bool Connected, string? Status = null)
{
    public string? ActionText => Action switch
    {
        ServerBannerAction.Retry => "Try again",
        ServerBannerAction.PairAgain => "Pair again…",
        ServerBannerAction.EditServer => "Edit server…",
        _ => null,
    };
}

/// <summary>
/// The bar above the list while another server is shown. It always offers the way back to this PC, and says plainly
/// when the server can't be reached, so an unreachable server never looks like an empty one and a list that stopped
/// updating never looks live. Nothing here touches the UI.
/// </summary>
internal static class ServerBannerRules
{
    public const string BackText = "Back to This PC";

    /// <param name="everConnected">The connection to this server was up at least once since it was shown.</param>
    /// <param name="pairingEnded">The server refused the token (<see cref="HostConnection.PairingEnded"/>).</param>
    /// <param name="stopped">Nothing more is tried until the user changes something (<see cref="HostConnection.StoppedTrying"/>).</param>
    /// <param name="error">The connection's last words, if it went down.</param>
    public static ServerBanner For(ServerTarget target, bool connected, bool everConnected, bool pairingEnded, bool stopped, string? error)
    {
        if (target.IsLocal) return new ServerBanner(false, string.Empty, false, ServerBannerAction.None, false, null, connected);
        var name = target.Name;
        if (connected)
            return new ServerBanner(true, $"Showing the downloads of {name}.", false, ServerBannerAction.None, false, null, true);

        var stale = everConnected ? " The list below is from before the connection was lost." : string.Empty;
        // While connecting there is nothing wrong yet; "Not connected" is for a connection that failed (U-WR-1).
        var notConnected = everConnected ? $"Not connected to {name} · the list is out of date" : $"Not connected to {name}";
        if (pairingEnded)
            return new ServerBanner(true, $"{name} no longer accepts this PC's pairing, so its downloads can't be shown or changed.{stale}",
                true, ServerBannerAction.PairAgain, everConnected, $"{name}'s downloads can't be shown until this PC is paired with it again.", false, notConnected);
        if (stopped)
            return new ServerBanner(true, (error ?? $"{name} can't be reached with its saved settings.") + stale,
                true, ServerBannerAction.EditServer, everConnected, $"{name}'s downloads can't be shown until its settings are changed.", false, notConnected);
        if (error is null)
            return new ServerBanner(true, $"Connecting to {name}…", false, ServerBannerAction.None, false, $"Connecting to {name}…", false, $"Connecting to {name}…");

        // "Couldn't reach the server." says which one only through the window; the bar names it.
        var reason = error.Contains(name, StringComparison.OrdinalIgnoreCase) ? error : $"{name}: {error}";
        return new ServerBanner(true, $"{reason}{stale}", true, ServerBannerAction.Retry, everConnected,
            $"{name} can't be reached, so its downloads can't be shown.\nThey are still on {name}.", false, notConnected);
    }
}

/// <summary>Names the server in the windows and dialogs that act on one (U-WR-5).</summary>
internal static class ServerNames
{
    /// <summary>
    /// "Download complete — file.zip" for this PC; "Download complete — file.zip — Test server" for a server. A title that
    /// ends with the app's name gets the server before it: "My TorBox — Test server — Inbound Download Manager".
    /// </summary>
    public static string ForWindow(string title, ServerTarget target)
    {
        if (target.IsLocal) return title;
        const string app = " — " + ServerSwitch.WindowTitle;
        return title.EndsWith(app, StringComparison.Ordinal) ? $"{title[..^app.Length]} — {target.Name}{app}" : $"{title} — {target.Name}";
    }

    /// <summary>The switcher's accessible name, which a screen reader says: which Host is shown, not only what it does.</summary>
    public static string SwitcherName(ServerTarget target) => $"Show downloads of: {target.Name}";

    /// <summary>The words under a finished download, which match the buttons that are there.</summary>
    public static string Completed(ServerTarget target) =>
        target.IsLocal ? "The file has been saved. Open it, or show it in its folder."
        : $"The file has been saved on {target.Name}. It is on that computer, so it can't be opened from this PC.";
}
