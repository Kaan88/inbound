#if !HAS_UNO
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

/// <summary>SITES.11 R2c: drag and drop of this pane's rows (see <see cref="PaneDrag"/>); not compiled on the Uno (Linux) window.</summary>
public sealed partial class LocalPane
{
    private PaneDrag? _drag;

    /// <summary>The pane's drag and drop: the window sets its planner and drop handler.</summary>
    internal PaneDrag? Drag => _drag;

    partial void WireDrag() => _drag = new PaneDrag(ItemsList, ListHost, "thispc", row => (row as LocalRow)?.Line, () => (null, _path));
}
#endif
