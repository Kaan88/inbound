using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The one way this app shows a <see cref="ContentDialog"/> (UI.B8). WinUI allows one open dialog per window, and a
/// second ShowAsync throws; one escaping an async void handler ends the window (0xc000027b). A dialog asked for while
/// another is open on the same window is refused: <see cref="ShowAsync"/> returns null without showing it, and every
/// caller treats null as cancel, never as the primary answer.
/// </summary>
internal static class DialogGuard
{
    // Windows with a dialog shown through here. UI thread only, so a plain list is enough; there are only a few windows.
    private static readonly List<XamlRoot> Showing = new();

    /// <summary>True while a ContentDialog is open on the window <paramref name="root"/> belongs to, whether or not it
    /// came through here.</summary>
    public static bool IsOpen(XamlRoot? root)
    {
        if (root is null) return false;
        if (Showing.Any(open => ReferenceEquals(open, root))) return true;
        try
        {
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => HoldsContentDialog(popup.Child, 4));
        }
        catch (Exception)
        {
            // A window that is closing has no popups to ask about.
            return false;
        }
    }

    /// <summary>Shows <paramref name="dialog"/> and returns its answer, or null without showing it while another
    /// ContentDialog is open on its window.</summary>
    public static async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog)
    {
        var root = dialog.XamlRoot;
        if (IsOpen(root)) return null;
        if (root is not null) Showing.Add(root);
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A dialog the check above could not see was open after all. Any exception type: WinUI does not document
            // which one a second ShowAsync throws.
            return null;
        }
        finally
        {
            if (root is not null) Showing.Remove(root);
        }
    }

    /// <summary>True only when the dialog was shown and its primary button chosen.</summary>
    public static async Task<bool> ConfirmAsync(ContentDialog dialog) =>
        await ShowAsync(dialog) == ContentDialogResult.Primary;

    // An open ContentDialog sits in a popup, as the popup's child or a few levels under it; flyouts and menus never hold one.
    private static bool HoldsContentDialog(DependencyObject? element, int depth)
    {
        if (element is null) return false;
        if (element is ContentDialog) return true;
        if (depth == 0) return false;
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            if (HoldsContentDialog(VisualTreeHelper.GetChild(element, i), depth - 1)) return true;
        }
        return false;
    }
}
