using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Code-behind access to the shared palette in App.xaml, resolved for a specific element theme
/// (windows carry their own RequestedTheme, so Application-level lookup alone would pick the wrong variant).
/// </summary>
internal static class InboundTheme
{
    public static Brush Brush(string key, ElementTheme theme)
    {
        var dictionaryKey = theme == ElementTheme.Light ? "Light"
            : theme == ElementTheme.Dark ? "Dark"
            : Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Dark";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionaryKey, out var value) &&
            value is ResourceDictionary dictionary && dictionary.TryGetValue(key, out var brush) && brush is Brush resolved)
        {
            return resolved;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public static Brush Brush(string key, FrameworkElement element) => Brush(key, element.ActualTheme);

    /// <summary>Applies the shared progress-bar look to a bar created in code.</summary>
    public static ProgressBar Styled(ProgressBar bar)
    {
        if (Application.Current.Resources.TryGetValue("InboundProgressBarStyle", out var style) && style is Style s)
        {
            bar.Style = s;
        }
        return bar;
    }
}
