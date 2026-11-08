using DownloadManager.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The one theme every window of this process follows (UI.R23). Its source is the settings' Theme value (System, Light
/// or Dark; owner, 2026-11-08: "Make it the source"). System follows Windows' app theme and changes with it. Until the
/// Host's settings arrive, the last theme seen is used from ui-state.json, so a window does not open in the wrong one.
/// Windows get an explicit Light or Dark, never Default: a Default element follows the app's theme from when it started,
/// not a change Windows makes later.
/// </summary>
internal static class AppTheme
{
    private static readonly List<(WeakReference<FrameworkElement> Root, DispatcherQueue Queue)> Roots = new();
    private static readonly object Gate = new();
    private static UISettings? _windowsSettings;
    private static string _choice = InitialChoice();

    /// <summary>The settings' value: System, Light or Dark.</summary>
    public static string Choice => _choice;

    /// <summary>The theme windows use now, with System resolved to Windows' app theme.</summary>
    public static ElementTheme Element => ThemeChoices.IsDark(_choice, WindowsUsesDark()) ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>Raised on the UI thread of the window that changed the choice, or Windows' theme, after every tracked
    /// root has been told.</summary>
    public static event Action? Changed;

    /// <summary>Gives <paramref name="root"/> the current theme now and every later one, for as long as it lives.</summary>
    public static void Track(FrameworkElement root)
    {
        root.RequestedTheme = Element;
        lock (Gate)
        {
            Roots.RemoveAll(entry => !entry.Root.TryGetTarget(out _));
            Roots.Add((new WeakReference<FrameworkElement>(root), root.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread()));
        }
        WatchWindows();
    }

    /// <summary>Uses <paramref name="choice"/> from now on. Saving it in the settings is the caller's part.</summary>
    public static void Use(string? choice)
    {
        var normalized = ThemeChoices.Normalize(choice);
        if (normalized == _choice) return;
        _choice = normalized;
        Remember(normalized);
        ApplyToAll();
    }

    private static bool? _migrationPending;

    /// <summary>For a window in a process of its own (a download's or a package's): follows the Theme setting from the
    /// Host's settings, unless the main window has yet to move an old View > Dark Mode value into it. That one keeps
    /// the old value until then.</summary>
    public static void FollowSettings(SettingsDto settings)
    {
        _migrationPending ??= UiStateStore.Load().DarkMode is not null;
        if (_migrationPending == true) return;
        Use(settings.Theme);
    }

    private static void ApplyToAll()
    {
        var theme = Element;
        List<(WeakReference<FrameworkElement> Root, DispatcherQueue Queue)> roots;
        lock (Gate)
        {
            Roots.RemoveAll(entry => !entry.Root.TryGetTarget(out _));
            roots = Roots.ToList();
        }

        foreach (var (reference, queue) in roots)
        {
            queue.TryEnqueue(() =>
            {
                if (reference.TryGetTarget(out var root) && root.RequestedTheme != theme) root.RequestedTheme = theme;
            });
        }

        var current = DispatcherQueue.GetForCurrentThread();
        if (current is not null) current.TryEnqueue(() => Changed?.Invoke());
        else roots.FirstOrDefault().Queue?.TryEnqueue(() => Changed?.Invoke());
    }

    private static void WatchWindows()
    {
        if (_windowsSettings is not null) return;
        try
        {
            _windowsSettings = new UISettings();
            // Raised on a background thread, also for accent and contrast changes; ApplyToAll only changes a root
            // whose theme differs, on that root's own thread.
            _windowsSettings.ColorValuesChanged += (_, _) =>
            {
                if (_choice == ThemeChoices.System) ApplyToAll();
            };
        }
        catch (Exception)
        {
            // Without it, System keeps the theme Windows had when the window opened.
        }
    }

    /// <summary>Windows' app theme: a dark window background means dark mode.</summary>
    private static bool WindowsUsesDark()
    {
        try
        {
            var background = (_windowsSettings ?? new UISettings()).GetColorValue(UIColorType.Background);
            return background.R + background.G + background.B < 3 * 128;
        }
        catch (Exception)
        {
            return Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        }
    }

    private static string InitialChoice()
    {
        var state = UiStateStore.Load();
        return ThemeChoices.Normalize(state.Theme ?? ThemeChoices.FromDarkMode(state.DarkMode));
    }

    // Only a start-up hint for the next launch; the settings stay the source.
    private static void Remember(string choice)
    {
        try
        {
            var state = UiStateStore.Load();
            if (state.Theme == choice) return;
            state.Theme = choice;
            UiStateStore.Save(state);
        }
        catch (Exception)
        {
            // The hint is cosmetic.
        }
    }
}
