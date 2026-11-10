using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// While one menu of the menu bar is open, pointing at another title opens that one, as WinUI's own menu bar does on
/// Windows. Uno's menu bar doesn't, and while its menu is open the light-dismiss layer takes every pointer event, so on
/// Uno this listens at the top of the open menu's popup and compares the pointer with the titles' bounds.
/// </summary>
internal static class MenuBarHover
{
    public static void Attach(MenuBar bar)
    {
#if HAS_UNO
        foreach (var item in bar.Items)
            item.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => WatchSoon(bar)), true);
#endif
    }

    private static void WatchSoon(MenuBar bar) =>
        bar.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Watch(bar));

    private static readonly HashSet<UIElement> Watched = [];

    /// <summary>
    /// Hooks the open menu's popup: the full-window panel that takes the pointer, and the top of its tree. A Popup has no
    /// visual parent, so the walk starts at its content.
    /// </summary>
    private static void Watch(MenuBar bar)
    {
        if (bar.XamlRoot is null) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(bar.XamlRoot))
        {
            if (popup.Child is not DependencyObject child) continue;
            var panel = VisualTreeHelper.GetParent(child);
            DependencyObject top = child;
            for (var parent = VisualTreeHelper.GetParent(top); parent is not null; parent = VisualTreeHelper.GetParent(parent)) top = parent;
            foreach (var element in new[] { panel, top })
            {
                if (element is not UIElement target || !Watched.Add(target)) continue;
                target.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Moved(bar, e)), true);
            }
        }
    }

    private static void Moved(MenuBar bar, PointerRoutedEventArgs e)
    {
        if (bar.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(bar.XamlRoot).Count == 0) return;
        var point = e.GetCurrentPoint(null).Position;
        foreach (var item in bar.Items)
        {
            if (item.ActualWidth <= 0 || !Contains(item, point)) continue;
            Switch(bar, item);
            return;
        }
    }

    private static bool Contains(FrameworkElement element, Point point)
    {
        var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Contains(point);
    }

    private static void Switch(MenuBar bar, MenuBarItem item)
    {
        var target = Expander(item);
        if (target is null || target.ExpandCollapseState == ExpandCollapseState.Expanded) return;
        var anyOpen = false;
        foreach (var other in bar.Items)
        {
            if (ReferenceEquals(other, item) || Expander(other) is not { ExpandCollapseState: ExpandCollapseState.Expanded } open) continue;
            open.Collapse();
            anyOpen = true;
        }

        // Another popup (a context menu, a dialog) is not this bar's menu: leave it.
        if (!anyOpen) return;
        // The closing menu goes away a moment later, and an expand before that is ignored: wait for its popup to go.
        var timer = bar.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        var tries = 0;
        timer.Tick += (_, _) =>
        {
            if (bar.XamlRoot is not null && VisualTreeHelper.GetOpenPopupsForXamlRoot(bar.XamlRoot).Count > 0 && ++tries < 30) return;
            timer.Stop();
            target.Expand();
            WatchSoon(bar);
        };
        timer.Start();
    }

    private static IExpandCollapseProvider? Expander(MenuBarItem item) =>
        (FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item))
            ?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
}
