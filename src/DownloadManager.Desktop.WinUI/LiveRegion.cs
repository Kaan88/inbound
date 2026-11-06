using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Tells screen readers that a polite live region has something new to say. Setting the words is not enough
/// when they are the same words as last time, so every announcement goes through here.
/// </summary>
internal static class LiveRegion
{
    public static void Announce(UIElement region) =>
        FrameworkElementAutomationPeer.FromElement(region)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}
