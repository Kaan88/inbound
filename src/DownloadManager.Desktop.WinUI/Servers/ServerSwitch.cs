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
