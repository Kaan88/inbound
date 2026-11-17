#if !HAS_UNO
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R2c: drag and drop between the two panes and from Explorer onto the server pane. A drop runs the same commands as the
/// gutter arrows and Upload/Download (UploadCoreAsync, DownloadNamesAsync: the same conflict question and in-flight guard); what
/// may drop where is SitesCommandRules.PlanDrop. While the window manages another server, Explorer items are no-drop with a short
/// caption. Every failure of a drop is said in the status line. Not compiled on the Uno (Linux) window.
/// </summary>
public sealed partial class MainWindow
{
    partial void WireServerDrag(SitesPanel panel)
    {
        if (panel.Drag is not { } drag) return;
        drag.Planner = (source, row) => PlanPaneDrop(SitesCommandRules.DropPane.Server, source, row);
        drag.Dropped = (source, row, plan, paths) =>
        {
            // Read now: the source pane forgets its drag when the drag operation completes.
            var start = _localPane?.Drag?.Started;
            _ = ReportedAsync(DropOnServerAsync(source, plan, paths, start), "Drop");
        };
    }

    partial void WireLocalDrag(LocalPane local)
    {
        if (local.Drag is not { } drag) return;
        drag.Planner = (source, row) => PlanPaneDrop(SitesCommandRules.DropPane.ThisPc, source, row);
        drag.Dropped = (source, row, plan, paths) =>
        {
            var start = _sitesPanel?.Drag?.Started;
            _ = ReportedAsync(DropOnLocalAsync(source, plan, start), "Drop");
        };
    }

    private SitesCommandRules.DropPlan PlanPaneDrop(SitesCommandRules.DropPane pane, SitesCommandRules.DragFrom source, SitesBrowseRules.PaneLine? row)
    {
        var serverReady = _sitesPanel is { SiteId: not null, IsLoading: false };
        if (pane == SitesCommandRules.DropPane.Server)
            return SitesCommandRules.PlanDrop(source, pane, row, _sitesPanel?.Path ?? "", serverReady, _connection.IsRemote);
        var ready = TwoPanesShown && serverReady && _localPane is { Path.Length: > 0 };
        return SitesCommandRules.PlanDrop(source, pane, row, _localPane?.Path ?? "", ready, _connection.IsRemote);
    }

    private async Task DropOnServerAsync(SitesCommandRules.DragFrom source, SitesCommandRules.DropPlan plan, IReadOnlyList<string> paths,
        SitesCommandRules.DragStart? start)
    {
        if (plan.Kind == SitesCommandRules.DropKind.Refuse)
        {
            SetSitesStatus(plan.Why);
            return;
        }

        if (plan.Kind != SitesCommandRules.DropKind.Do || plan.TargetFolder is not { } target || _sitesPanel is not { Site: { } site, IsLoading: false }) return;
        if (source == SitesCommandRules.DragFrom.ThisPc)
        {
            if (_localPane is not { } local || start is not { Names.Count: > 0 } started) return;
            // The This PC pane moved to another folder while the items were dragged: they are not the ones there now.
            if (SitesCommandRules.DragMoved(started, null, local.Path, local: true) is { } moved)
            {
                SetSitesStatus(moved);
                return;
            }

            await UploadDroppedAsync(site, target, started.Folder, [.. started.Names]);
            return;
        }

        if (source != SitesCommandRules.DragFrom.Explorer) return;
        // What each path is (file, folder, a link to a folder) is asked off the UI thread (a share or a sleeping disk can wait).
        var (folder, dropped, problem) = await Task.Run(() => SitesCommandRules.ExplorerNames(paths, AttributesOf));
        if (problem is not null || folder is null)
        {
            SetSitesStatus(problem);
            return;
        }

        await UploadDroppedAsync(site, target, folder, dropped);
    }

    private async Task UploadDroppedAsync(SiteDto site, string target, string localFolder, List<SiteTransferNameDto> names)
    {
        if (!await OnceAsync(() => UploadCoreAsync(site, target, localFolder, names, serverPaths: false))) SetSitesStatus(SitesCommandRules.TransferStartingText);
    }

    private static FileAttributes? AttributesOf(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private async Task DropOnLocalAsync(SitesCommandRules.DragFrom source, SitesCommandRules.DropPlan plan, SitesCommandRules.DragStart? start)
    {
        if (plan.Kind == SitesCommandRules.DropKind.Refuse)
        {
            SetSitesStatus(plan.Why);
            return;
        }

        if (source != SitesCommandRules.DragFrom.Server || plan.Kind != SitesCommandRules.DropKind.Do || plan.TargetFolder is not { } target) return;
        if (_sitesPanel is not { Site: { } site, IsLoading: false } panel || start is not { Names.Count: > 0 } started) return;
        // The server pane moved to another folder or remote while the items were dragged.
        if (SitesCommandRules.DragMoved(started, site.Id, panel.Path, local: false) is { } moved)
        {
            SetSitesStatus(moved);
            return;
        }

        if (!await OnceAsync(() => DownloadNamesAsync(site, started.Folder, [.. started.Names], target)))
            SetSitesStatus(SitesCommandRules.TransferStartingText);
    }
}
#endif
