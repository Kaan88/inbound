using Android.Views;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace Inbound.App.Screens;

/// <summary>Getting to the file (mockups x7 to x10b, x33): the transfers button, the started snackbar, Show in folder with the file marked.</summary>
internal sealed partial class RemoteFilesScreen
{
    private (string Path, string? File)? _pendingShow;

    /// <summary>The marked file stays outlined this long (x9).</summary>
    private const int MarkMilliseconds = 3000;

    private bool CanShowTransferFolder(SiteTransferDto t) => t.Direction switch
    {
        "download" => _phoneHost,
        "upload" => _sites.Any(site => site.Id == t.SiteId),
        "copy" => _sites.Any(site => site.Id == t.DestinationSiteId),
        _ => false,
    };

    /// <summary>
    /// The folder a transfer went to, with its file marked: a download opens the This phone pane, an upload or a copy opens the
    /// destination location. <paramref name="itemPath"/> names one file of the batch (its folder button in the details sheet).
    /// </summary>
    private void ShowTransferFolder(SiteTransferDto t, string? itemPath)
    {
        if (!SameHost()) return;
        string? mark = itemPath;
        string subfolder = "";
        if (itemPath is not null)
        {
            int slash = itemPath.LastIndexOf('/');
            if (slash >= 0) { subfolder = itemPath[..slash]; mark = itemPath[(slash + 1)..]; }
        }
        else if (_transferPaths.TryGetValue(t.Id, out var paths))
        {
            var tops = paths.Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p).Distinct(StringComparer.Ordinal).ToList();
            mark = tops.Count == 1 ? tops[0] : null;
        }
        string Join(string baseFolder) => subfolder.Length == 0 ? baseFolder : baseFolder.TrimEnd('/') + "/" + subfolder;
        switch (t.Direction)
        {
            case "download" when _phoneHost:
                ShowPhoneFolderNow(Join(RemoteTransferText.PhonePath(t.LocalFolder)), mark, t.SiteId);
                break;
            case "upload" when _sites.FirstOrDefault(site => site.Id == t.SiteId) is { } uploadSite:
                OpenFolderAt(uploadSite, Join(t.RemoteFolder), mark);
                break;
            case "copy" when _sites.FirstOrDefault(site => site.Id == t.DestinationSiteId) is { } copySite:
                OpenFolderAt(copySite, Join(t.DestinationFolder ?? "/"), mark);
                break;
        }
    }

    /// <summary>Opens the This phone pane at <paramref name="phonePath"/> ("Download/Inbound/Music") with <paramref name="file"/> marked.</summary>
    public void ShowPhoneFolder(string phonePath, string? file)
    {
        _pendingShow = (phonePath, file);
        if (_listLoaded && !_busy) ApplyPendingShow();
    }

    private void ApplyPendingShow()
    {
        if (_pendingShow is not { } show || !_listLoaded || _closed) return;
        _pendingShow = null;
        ShowPhoneFolderNow(show.Path, show.File);
    }

    private void ShowPhoneFolderNow(string phonePath, string? file, string? leftSiteId = null) => OpenFolderAt(null, phonePath, file, leftSiteId);

    /// <summary>
    /// Shows the folder view with a folder of <paramref name="site"/> in the left tab, or (null site) the phone's folder in the right tab,
    /// and outlines <paramref name="mark"/> for a moment.
    /// </summary>
    private void OpenFolderAt(SiteDto? site, string path, string? mark, string? leftSiteId = null)
    {
        if (!SameHost()) return;
        _transfersGen++;
        _transferLoading = false;
        _showTransfers = false;
        _adding = false;
        _sheet?.Dismiss();
        _inFolder = true;
        _lastActiveTransfers = _transfers.Count(RemoteTransferText.IsActive);
        SiteDto? left = site ?? _sites.FirstOrDefault(s => s.Id == leftSiteId) ?? _sites.FirstOrDefault();
        ResetPane(_panes[0], left, site is null ? null : path);
        ResetPane(_panes[1], null, site is null ? path : "Download");
        _active = site is null ? 1 : 0;
        var shown = Active;
        shown.Mark = shown.PillMark = mark;
        shown.MarkShown = false;
        shown.MarkPath = null;
        shown.MarkSince = Environment.TickCount64;
        Render();
        _ = BrowsePaneAsync(_panes[0], _panes[0].Site is null ? "Download" : site is null ? null : path, false);
        _ = BrowsePaneAsync(_panes[1], site is null ? path : "Download", false);
    }

    /// <summary>After the rows are set: scroll to the marked file once and clear the mark after about 3 s.</summary>
    private void ApplyMark(Pane pane)
    {
        if (pane.Mark is not { } mark || pane.MarkShown) return;
        // The mark belongs to the folder whose rows arrived first; another folder never shows it.
        pane.MarkPath ??= pane.Path;
        if (pane.MarkPath != pane.Path) { pane.Mark = pane.PillMark = null; return; }
        int index = pane.Rows.FindIndex(row => row.Name == mark);
        if (index < 0)
        {
            // A just-finished file may not be in the phone's media index yet; give up after a while.
            if (!pane.Loading && Environment.TickCount64 - pane.MarkSince > 10000) pane.Mark = pane.PillMark = null;
            return;
        }
        pane.MarkShown = true;
        _folderList!.ScrollToPosition(index);
        _surface.PostDelayed(() =>
        {
            if (_closed || pane.Mark != mark) return;
            pane.Mark = null; // the outline fades; the Open pill stays until the person leaves the folder
            UpdateFolderUi();
        }, MarkMilliseconds);
    }

    /// <summary>Transfers button of the folder app bar: the glyph with a count badge while something runs (x7).</summary>
    private View TransfersButton()
    {
        int active = _transfers.Count(RemoteTransferText.IsActive);
        var frame = new FrameLayout(_activity);
        frame.AddView(IconButton(Resource.Drawable.ic_remote_transfers, active > 0 ? "Transfers, " + active + " active" : "Transfers", OpenTransfers),
            new FrameLayout.LayoutParams(Dp(48), Dp(48)));
        if (active > 0)
        {
            var badge = FolderText(active > 99 ? "99+" : active.ToString(), 11, Resource.Attribute.colorOnPrimary);
            badge.Gravity = Android.Views.GravityFlags.Center;
            badge.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);
            var disc = new Android.Graphics.Drawables.GradientDrawable();
            disc.SetCornerRadius(Dp(10));
            disc.SetColor(new Android.Graphics.Color(Attr(Resource.Attribute.colorPrimary)));
            badge.Background = disc;
            badge.SetPadding(Dp(5), 0, Dp(5), 0);
            badge.ImportantForAccessibility = Android.Views.ImportantForAccessibility.No;
            frame.AddView(badge, new FrameLayout.LayoutParams(-2, Dp(20), Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Right) { TopMargin = Dp(2), RightMargin = Dp(2) });
            badge.SetMinWidth(Dp(20));
        }
        return frame;
    }
}
