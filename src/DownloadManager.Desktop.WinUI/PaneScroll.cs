using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The file panes read a folder again after a delete, a rename, a new folder or F5, which empties and fills their list.
/// That puts the list back at the top; this keeps it where it was, as Explorer does.
/// </summary>
internal static class PaneScroll
{
    /// <summary>How far down the list is scrolled now (0 when it has no scroller yet).</summary>
    public static double Offset(ListViewBase list) => Find(list)?.VerticalOffset ?? 0;

    /// <summary>
    /// Scrolls the refilled list back to <paramref name="offset"/>, then, on a later turn, brings <paramref name="row"/> into
    /// view only if it is not in view already, and runs <paramref name="then"/>. Nothing happens once
    /// <paramref name="stillWanted"/> says another folder or load took over.
    /// </summary>
    public static void Restore(ListViewBase list, double offset, object? row, Func<bool> stillWanted, Action? then = null)
    {
        list.UpdateLayout();
        if (offset > 0 && Find(list) is { } scroller) scroller.ChangeView(null, offset, null, disableAnimation: true);
        list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!stillWanted()) return;
            list.UpdateLayout();
            if (row is not null && !InView(list, row)) list.ScrollIntoView(row);
            then?.Invoke();
        });
    }

    private static bool InView(ListViewBase list, object row)
    {
        if (list.ContainerFromItem(row) is not FrameworkElement container || Find(list) is not { } scroller) return false;
        try
        {
            var top = container.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            return top >= 0 && top + container.ActualHeight <= scroller.ViewportHeight + 0.5;
        }
        catch (ArgumentException)
        {
            // Not in the same tree any more (the row was recycled meanwhile).
            return false;
        }
    }

    private static ScrollViewer? Find(DependencyObject parent)
    {
        for (var at = 0; at < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); at++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, at);
            if (child is ScrollViewer viewer) return viewer;
            if (Find(child) is { } found) return found;
        }

        return null;
    }
}
