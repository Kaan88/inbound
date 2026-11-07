using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The one time a file choice opens by itself: straight after an add, or after Enter on a copied row, when exactly one
/// of what that added can ask for its files and it is ready to choose within <see cref="Window"/>, with the window in
/// front and nobody doing anything in it meanwhile. Otherwise the row carries the choice, and Enter, a double-click or
/// the menu opens it. In memory only, so a restart forgets it, and nothing opens late or one after another.
/// Every member belongs to the UI thread.
/// </summary>
internal sealed class AutoOpenWatch
{
    /// <summary>Provisional: long enough for an item already on the account or cached. A real cached item's time to ready isn't measured yet.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly HashSet<Guid> _waiting = [];
    private DateTimeOffset _armedAt;

    public bool IsArmed => _waiting.Count > 0;

    /// <summary>Whether a job, as it was just added, released or found again, may yet ask for a choice of files.</summary>
    public static bool CouldAsk(RemoteJobDto job) => job.State == "awaitingSelection" || RemoteRules.CanChooseEarly(job);

    /// <summary>
    /// When an add or a release is over, messages and all, with the jobs it added or found again. It replaces whatever
    /// was armed before. Jobs that can't ask don't count, so an add with nothing that can ask arms nothing.
    /// </summary>
    public void Arm(IEnumerable<RemoteJobDto> jobs, DateTimeOffset now)
    {
        _waiting.Clear();
        foreach (var job in jobs)
        {
            if (CouldAsk(job)) _waiting.Add(job.Id);
        }

        _armedAt = now;
    }

    /// <summary>A key or pointer press in the window, or the window going to the back: whatever comes next is the user's.</summary>
    public void Disarm() => _waiting.Clear();

    /// <summary>
    /// The job whose files to show now, or null. Run on arming and on every change to the jobs, with the jobs as they
    /// are now, so the state it decides on is the latest. It disarms when it says to open, so nothing chains, and when
    /// it never can: the time is up, the window is behind another, or a popup such as a menu or dialog is open when
    /// the files are ready. While two or more could still ask, none opens.
    /// </summary>
    /// <param name="popupOpen">A menu, dialog or flyout is open over the window. A tooltip doesn't count.</param>
    public Guid? Check(IEnumerable<RemoteJobDto> jobs, DateTimeOffset now, bool foreground, bool popupOpen)
    {
        if (_waiting.Count == 0) return null;
        if (!foreground || now - _armedAt > Window)
        {
            Disarm();
            return null;
        }

        // Still on the list and still able to ask. Removed, started with its one file, or chosen elsewhere is done with.
        var current = jobs.Where(job => _waiting.Contains(job.Id) && CouldAsk(job)).ToList();
        _waiting.IntersectWith(current.Select(job => job.Id));
        if (current is not [{ State: "awaitingSelection" } ready]) return null;

        Disarm();
        return popupOpen ? null : ready.Id;
    }
}

/// <summary>What follows an add on the list itself.</summary>
internal static class AddFollowUp
{
    /// <summary>The row to select and show after an add: there is one only when the add came to exactly one item and no direct download.</summary>
    public static Guid? RowToShow(IReadOnlyList<RemoteJobDto> jobs, int downloads)
        => downloads == 0 && jobs.Select(job => job.Id).Distinct().ToList() is [var only] ? only : null;
}
