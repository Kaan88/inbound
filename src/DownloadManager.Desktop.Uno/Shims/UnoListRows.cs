using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Uno's ListViewItem uses the expanded template (no ListViewItemPresenter). Its reorder-hint text and placeholder
/// take part in layout even when unused and make a 27 px row 46 px tall, so they are collapsed here.
/// </summary>
internal static class UnoListRows
{
    private static readonly string[] Unused = ["MultiArrangeOverlayText", "PlaceholderTextBlock", "PlaceholderRect", "MultiSelectSquare"];

    public static void Trim(ListViewItem container)
    {
        if (VisualTreeHelper.GetChildrenCount(container) == 0) return;
        Visit(VisualTreeHelper.GetChild(container, 0), 0);
    }

    /// <summary>The row's template root. Uno leaves ContentTemplateRoot empty here; the content presenter holds it.</summary>
    public static Grid? Root(ListViewItem container)
    {
        if (container.ContentTemplateRoot is Grid root) return root;
        return Find(container, 0);
    }

    private static Grid? Find(DependencyObject node, int depth)
    {
        if (depth > 6) return null;
        if (node is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is Grid grid) return grid;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), depth + 1) is { } found) return found;
        return null;
    }

    private static void Visit(DependencyObject node, int depth)
    {
        if (depth > 3) return;
        if (node is FrameworkElement { Name: var name } element && Array.IndexOf(Unused, name) >= 0) element.Visibility = Visibility.Collapsed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i), depth + 1);
    }
}
