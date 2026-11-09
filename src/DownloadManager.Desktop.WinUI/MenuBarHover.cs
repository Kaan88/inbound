using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// While one menu of the menu bar is open, pointing at another title opens that one, as WinUI's own menu bar does on
/// Windows. Uno's menu bar doesn't, so on Uno this does it through the titles' expand/collapse automation.
/// </summary>
internal static class MenuBarHover
{
    public static void Attach(MenuBar bar)
    {
#if HAS_UNO
        foreach (var item in bar.Items)
            item.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) => Entered(bar, item)), true);
#endif
    }

    private static void Entered(MenuBar bar, MenuBarItem item)
    {
        if (bar.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(bar.XamlRoot).Count == 0) return;
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
        if (anyOpen) target.Expand();
    }

    private static IExpandCollapseProvider? Expander(MenuBarItem item) =>
        FrameworkElementAutomationPeer.FromElement(item) is { } peer
            ? peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
            : (FrameworkElementAutomationPeer.CreatePeerForElement(item)?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider);
}
