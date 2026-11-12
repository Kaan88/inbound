using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Applies <see cref="MenuSeparatorRules"/> to a menu's items (UI.B25).</summary>
internal static class MenuSeparators
{
    /// <summary>Hides the separators left with nothing on one side. Runs last, after every rule has shown or hidden its
    /// items; a separator a rule hid stays hidden.</summary>
    public static void Collapse(IList<MenuFlyoutItemBase> items)
    {
        var isSeparator = items.Select(item => item is MenuFlyoutSeparator).ToList();
        var wanted = items.Select(item => item.Visibility == Visibility.Visible).ToList();
        var shown = MenuSeparatorRules.Visible(isSeparator, wanted);
        for (var i = 0; i < items.Count; i++)
        {
            if (!isSeparator[i]) continue;
            var visibility = shown[i] ? Visibility.Visible : Visibility.Collapsed;
            if (items[i].Visibility != visibility) items[i].Visibility = visibility;
        }
    }
}
