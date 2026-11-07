namespace DownloadManager.Desktop.WinUI;

/// <summary>A timer that ticks once, a set time after it was last started.</summary>
internal interface IOneShotTimer
{
    /// <summary>Starts it again from now. A tick still to come from an earlier start is dropped.</summary>
    void Restart();

    void Stop();
}

/// <summary>
/// A status bar message about something just done. It is on show until <see cref="Time"/> after the latest one, and
/// then the usual line comes back. Its one timer is made once and kept here: a timer held only by the method that
/// started it was let go and never ticked, so a message stayed on the bar until something else happened to redraw it,
/// and after an add whose downloads were done within the time, that was never.
/// </summary>
internal sealed class PassingLine
{
    public static readonly TimeSpan Time = TimeSpan.FromSeconds(5);

    private readonly Func<Action, IOneShotTimer> _makeTimer;
    private IOneShotTimer? _timer;

    /// <param name="makeTimer">Makes the timer, given what its tick does. Called once, for the first message.</param>
    public PassingLine(Func<Action, IOneShotTimer> makeTimer) => _makeTimer = makeTimer;

    /// <summary>The message on show, or null for the usual line.</summary>
    public string? Words { get; private set; }

    /// <summary>A message's time is up, so the usual line is drawn again.</summary>
    public event Action? Ended;

    public void Show(string words)
    {
        Words = words;
        (_timer ??= _makeTimer(TimeUp)).Restart();
    }

    /// <summary>Ends the message now, as when what comes next says more.</summary>
    public void End()
    {
        _timer?.Stop();
        Words = null;
    }

    // Whenever the tick comes. Comparing clocks here instead would keep a message for good if it came a moment early.
    private void TimeUp()
    {
        Words = null;
        Ended?.Invoke();
    }
}
