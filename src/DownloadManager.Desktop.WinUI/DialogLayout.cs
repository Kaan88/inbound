using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T13: a dialog's content in a scroller as tall as the window lets it be (<see cref="WindowSizeRules.DialogContentMaxHeight"/>),
/// so its title and buttons always stay in the window and the content scrolls instead. WinUI's dialog doesn't scroll its
/// content and cuts it off at the bottom. The height follows the window while the dialog is open.
/// </summary>
internal static class DialogLayout
{
    /// <param name="root">The dialog's window (its <see cref="ContentDialog.XamlRoot"/>).</param>
    /// <param name="fitted">Told the content's new most height (DIPs), for content with a list of its own to size.</param>
    public static ScrollViewer ScrollingContent(UIElement content, XamlRoot root, double most = WindowSizeRules.DialogMostContent,
        Action<double>? fitted = null)
    {
        var scroller = new ScrollViewer
        {
            Padding = InboundTheme.DialogScrollPadding,
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        void Fit()
        {
            var height = WindowSizeRules.DialogContentMaxHeight(root.Size.Height, most);
            if (scroller.MaxHeight != height) scroller.MaxHeight = height;
            fitted?.Invoke(height);
        }

        void WindowChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Fit();
        Fit();
        scroller.Loaded += (_, _) =>
        {
            Fit();
            root.Changed -= WindowChanged;
            root.Changed += WindowChanged;
        };
        scroller.Unloaded += (_, _) => root.Changed -= WindowChanged;
        return scroller;
    }
}
