using System.Text.Json;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Remembers where each kind of window was last placed. Kept in its own file (not ui-state.json)
/// because progress windows run in separate processes; each save re-reads the file and changes
/// only its own key, so windows never overwrite one another's placement or the main UI state.
/// </summary>
internal static class WindowPlacement
{
    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            var isolated = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
            return Path.Combine(string.IsNullOrWhiteSpace(isolated) ? AppFolders.DataRoot() : isolated, "window-placement.json");
        }
    }

    /// <summary>
    /// Restores the window's last position (and size when <paramref name="restoreSize"/>) if it
    /// is still on a connected display, then saves placement whenever the window moves or resizes.
    /// Windows whose height follows their content keep only their position.
    /// </summary>
    public static void Track(Window window, string key, bool restoreSize = true)
    {
        var appWindow = window.AppWindow;
        if (Load().TryGetValue(key, out var saved) && IsVisible(saved))
        {
            if (restoreSize && saved.Width >= 200 && saved.Height >= 150)
                appWindow.MoveAndResize(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height));
            else
                appWindow.Move(new PointInt32(saved.X, saved.Y));
            if (restoreSize && saved.Maximized && appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }

        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Save(key, appWindow);
        appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) { timer.Stop(); timer.Start(); }
        };
        window.Closed += (_, _) => { timer.Stop(); Save(key, appWindow); };
    }

    // Windows moved off another of the app's windows when they opened: where to, and where from (one per process here).
    private static readonly Dictionary<ulong, (PointInt32 To, PointInt32 From)> Cascaded = new();

    /// <summary>
    /// Shows a window of which several can be open at once, each in its own process (progress, package, completion),
    /// off the app's other windows (<see cref="WindowCascade"/>, U-WL-4). Several finishing together start together,
    /// so one at a time places and shows itself, under a lock all of them share, and each sees the ones shown before it.
    /// The move is not remembered: while the window stays where it was put, the place it came from is what is saved.
    /// </summary>
    public static void ActivateCascaded(Window window)
    {
        var appWindow = window.AppWindow;
        Mutex? gate = null;
        var owned = false;
        try
        {
            gate = new Mutex(false, $"Local\\DownloadManager.UI.Cascade.{PipeNames.CurrentUserKey()}");
            try
            {
                owned = gate.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            // On the main window's screen first; that place, not the cascade's step from it, is what is remembered.
            WindowCascade.OntoMainScreen(window);
            var from = appWindow.Position;
            WindowCascade.Apply(window);
            var to = appWindow.Position;
            if (to.X != from.X || to.Y != from.Y) Cascaded[appWindow.Id.Value] = (to, from);
            window.Activate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            window.Activate();
        }
        finally
        {
            if (owned) gate?.ReleaseMutex();
            gate?.Dispose();
        }
    }

    private static void Save(string key, AppWindow appWindow)
    {
        try
        {
            if (!appWindow.IsVisible) return;
            var presenter = appWindow.Presenter as OverlappedPresenter;
            if (presenter?.State == OverlappedPresenterState.Minimized) return;
            var maximized = presenter?.State == OverlappedPresenterState.Maximized;
            var all = Load();
            // Still where the cascade put it: the place it came from, so windows don't drift down the screen.
            var position = Cascaded.TryGetValue(appWindow.Id.Value, out var moved) && moved.To.X == appWindow.Position.X && moved.To.Y == appWindow.Position.Y
                ? moved.From
                : appWindow.Position;
            // A maximized window keeps its last normal bounds so un-maximizing restores them.
            var bounds = maximized && all.TryGetValue(key, out var previous)
                ? previous with { Maximized = true }
                : new Placement(position.X, position.Y, appWindow.Size.Width, appWindow.Size.Height, maximized);
            all[key] = bounds;
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Placement is a convenience; never fail a window over it.
        }
    }

    private static Dictionary<string, Placement> Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(path), Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    /// <summary>True when the title-bar area is on a display that is connected now.</summary>
    private static bool IsVisible(Placement placement)
    {
        var probe = new PointInt32(placement.X + Math.Min(120, Math.Max(1, placement.Width / 2)), placement.Y + 16);
        var area = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.None);
        if (area is null) return false;
        var work = area.WorkArea;
        return probe.X >= work.X && probe.X < work.X + work.Width && probe.Y >= work.Y && probe.Y < work.Y + work.Height;
    }
}
