#if !HAS_UNO
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R2c: drag and drop for one pane's list. It only finds out what is dragged and what is under the pointer; what a
/// drop means is decided by <see cref="SitesCommandRules.PlanDrop"/> (the window's planner) and carried out by the window
/// with the same commands as the gutter arrows. A cancelled drag changes nothing (the selection is never touched).
/// Out of scope: dragging server files out to Explorer (it would need virtual files) and moving items within one pane.
/// Not built on the Uno (Linux) window, whose drag support is unverified: these files are not compiled there.
/// </summary>
internal sealed class PaneDrag
{
    private const string Key = "inbound.drag";
    private readonly ListView _list;
    private readonly string _kind;
    private readonly Func<object?, SitesBrowseRules.PaneLine?> _lineOf;
    private readonly Func<(string? SiteId, string Folder)> _where;
    private ListViewItem? _lit;

    public PaneDrag(ListView list, FrameworkElement host, string kind, Func<object?, SitesBrowseRules.PaneLine?> lineOf, Func<(string? SiteId, string Folder)> where)
    {
        _list = list;
        _kind = kind;
        _lineOf = lineOf;
        _where = where;
        list.CanDragItems = true;
        list.DragItemsStarting += OnStarting;
        list.DragItemsCompleted += (_, _) =>
        {
            Started = null;
            Clear();
        };
        // The host (the list's grid) also takes a drop, so an empty folder, where the list is hidden, can be dropped on.
        foreach (var target in new FrameworkElement[] { list, host })
        {
            target.AllowDrop = true;
            target.DragOver += OnOver;
            target.DragLeave += (_, _) => Clear();
            target.Drop += OnDrop;
        }

        if (host is Grid grid && grid.Background is null) grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>The planner (the window's): what a drag of a source over a row (null: the pane's own folder) does.</summary>
    public Func<SitesCommandRules.DragFrom, SitesBrowseRules.PaneLine?, SitesCommandRules.DropPlan>? Planner { get; set; }

    /// <summary>A drop that was planned; the paths are Explorer's items (empty otherwise). Raised after the drop has completed.</summary>
    public Action<SitesCommandRules.DragFrom, SitesBrowseRules.PaneLine?, SitesCommandRules.DropPlan, IReadOnlyList<string>>? Dropped { get; set; }

    /// <summary>
    /// This pane's drag in progress, as it was when it started: the site, the folder and the names (never ".."). A drop
    /// compares it with what the pane shows then (<see cref="SitesCommandRules.DragMoved"/>). Null when none.
    /// </summary>
    public SitesCommandRules.DragStart? Started { get; private set; }

    private void OnStarting(object sender, DragItemsStartingEventArgs e)
    {
        var lines = e.Items.Select(_lineOf).OfType<SitesBrowseRules.PaneLine>();
        var picked = SitesBrowseRules.SelectedForAction(lines);
        if (picked.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var (siteId, folder) = _where();
        Started = new SitesCommandRules.DragStart(siteId, folder, [.. picked.Select(line => new SiteTransferNameDto { Name = line.Name, IsFolder = line.IsFolder })]);
        e.Data.Properties[Key] = _kind;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private static SitesCommandRules.DragFrom SourceOf(DataPackageView view)
    {
        if (view.Properties.TryGetValue(Key, out var value) && value is string kind)
            return kind == "server" ? SitesCommandRules.DragFrom.Server : kind == "thispc" ? SitesCommandRules.DragFrom.ThisPc : SitesCommandRules.DragFrom.None;
        return view.Contains(StandardDataFormats.StorageItems) ? SitesCommandRules.DragFrom.Explorer : SitesCommandRules.DragFrom.None;
    }

    /// <summary>The row under the pointer, its container and line; all null over empty space.</summary>
    private (ListViewItem? Container, SitesBrowseRules.PaneLine? Line) RowAt(DragEventArgs e)
    {
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), _list))
        {
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem item && _list.ItemFromContainer(item) is { } data && _lineOf(data) is { } line) return (item, line);
            }
        }

        return (null, null);
    }

    private void OnOver(object sender, DragEventArgs e)
    {
        var (container, line) = RowAt(e);
        var plan = Planner?.Invoke(SourceOf(e.DataView), line) ?? SitesCommandRules.DropPlan.No;
        e.Handled = true;
        if (plan.Kind == SitesCommandRules.DropKind.NoDrop)
        {
            // The no-drop cursor; a short caption says why when there is one (another server). No Drop event follows.
            e.AcceptedOperation = DataPackageOperation.None;
            if (plan.Caption.Length > 0)
            {
                e.DragUIOverride.Caption = plan.Caption;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = false;
            }

            Clear();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = plan.Caption;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = plan.Kind == SitesCommandRules.DropKind.Do;
        Light(plan.Kind == SitesCommandRules.DropKind.Do && line is not null ? container : null);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // Async void: nothing may escape, so every failure becomes a refused drop with its reason in the status line.
        try
        {
            e.Handled = true;
            Clear();
            var source = SourceOf(e.DataView);
            var (_, line) = RowAt(e);
            var plan = Planner?.Invoke(source, line) ?? SitesCommandRules.DropPlan.No;
            if (plan.Kind == SitesCommandRules.DropKind.NoDrop) return;
            var paths = new List<string>();
            var deferral = e.GetDeferral();
            try
            {
                if (source == SitesCommandRules.DragFrom.Explorer && plan.Kind == SitesCommandRules.DropKind.Do)
                {
                    foreach (var item in await e.DataView.GetStorageItemsAsync())
                    {
                        // An item with no path (inside a zip, on a phone, the Recycle Bin) can't be uploaded: the whole drop is refused.
                        if (string.IsNullOrEmpty(item.Path))
                        {
                            plan = SitesCommandRules.Refused(SitesCommandRules.NotFilesOnThisPc);
                            paths.Clear();
                            break;
                        }

                        paths.Add(item.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                UiTrace.Write($"Reading the dropped items failed: {ex.GetType().Name}");
                plan = SitesCommandRules.Refused($"The dropped items could not be read: {ex.Message}");
                paths.Clear();
            }
            finally
            {
                // The drag ends here; a conflict question or a dialog must not hold Explorer's drag operation open.
                deferral.Complete();
            }

            Dropped?.Invoke(source, line, plan, paths);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"A drop failed: {ex.GetType().Name}");
        }
    }

    /// <summary>The folder row under the pointer is lit across its whole width, as a selected row is.</summary>
    private void Light(ListViewItem? container)
    {
        if (container is null)
        {
            Clear();
            return;
        }

        if (ReferenceEquals(container, _lit)) return;
        Clear();
        container.Background = InboundTheme.Brush("InboundCategorySelectedBrush", container);
        _lit = container;
    }

    private void Clear()
    {
        if (_lit is null) return;
        _lit.ClearValue(Control.BackgroundProperty);
        _lit = null;
    }
}
#endif
