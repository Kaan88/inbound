using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DownloadManager.AvaloniaUi;

/// <summary>
/// The status colors rows bind to, as in the WinUI InboundTheme.cs: rows hold these objects, and the colors change
/// here when the theme does.
/// </summary>
internal static class StatusBrushes
{
    public static readonly SolidColorBrush Active = new();
    public static readonly SolidColorBrush Done = new();
    public static readonly SolidColorBrush Failed = new();
    public static readonly SolidColorBrush Waiting = new();
    public static readonly SolidColorBrush Neutral = new();

    static StatusBrushes() => UseTheme(ThemeVariant.Dark);

    public static void UseTheme(ThemeVariant theme)
    {
        Active.Color = ColorOf("InboundStatusActiveBrush", theme);
        Done.Color = ColorOf("InboundStatusDoneBrush", theme);
        Failed.Color = ColorOf("InboundStatusFailedBrush", theme);
        Waiting.Color = ColorOf("InboundStatusWaitingBrush", theme);
        Neutral.Color = ColorOf("InboundStatusNeutralBrush", theme);
    }

    public static Color ColorOf(string key, ThemeVariant theme)
        => Application.Current?.TryGetResource(key, theme, out var value) == true && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.Gray;
}
