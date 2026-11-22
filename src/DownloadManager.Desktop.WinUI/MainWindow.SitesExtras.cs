using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.13 on the desktop: the place and sort kept with each remote, Preview (Space), the Sync dialog with its schedule choice,
/// and copying between two remotes from the two panes. The Host does the work; this file asks and shows.
/// </summary>
public sealed partial class MainWindow
{
    // ---- Remember the folder and the sort ---------------------------------------------------------------

    // What was last sent for each remote (id to "folder, sort, direction"), so a change the Host echoes back is not sent again.
    private readonly Dictionary<string, string> _rememberSent = [];

    /// <summary>
    /// Keeps the folder shown and the sort order with the remote (<c>sites.remember</c>), once the folder has been read. Quiet: a
    /// failure only means the place is not kept. Not for an address opened once.
    /// </summary>
    private void RememberPlace(SitesPanel panel)
    {
        if (panel.Site is not { Unsaved: false } site || !panel.IsListed || panel.Path.Length == 0) return;
        var sort = panel.SortKey is SiteSortKeys.Name or SiteSortKeys.Size or SiteSortKeys.Modified ? panel.SortKey : SiteSortKeys.Name;
        var descending = !panel.SortAscending;
        var key = $"{panel.Path}\n{sort}\n{descending}";
        var saved = $"{site.LastFolder}\n{site.Sort ?? SiteSortKeys.Name}\n{site.SortDescending}";
        var known = _rememberSent.TryGetValue(site.Id, out var sent) ? sent : $"{site.LastFolder ?? SitesBrowseRules.Root}\n{site.Sort ?? SiteSortKeys.Name}\n{site.SortDescending}";
        if (known == key || saved == key) return;
        _rememberSent[site.Id] = key;
        _ = SendRememberAsync(new SiteRememberRequest { Id = site.Id, LastFolder = panel.Path, Sort = sort, SortDescending = descending });
    }

    private async Task SendRememberAsync(SiteRememberRequest request)
    {
        try
        {
            await _remote.RememberSiteAsync(request);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            UiTrace.Write($"Keeping the place of a remote failed: {ex.GetType().Name}");
        }
    }

    // ---- Preview ----------------------------------------------------------------------------------------

    /// <summary>
    /// Space and "Preview": the start of the file selected in <paramref name="from"/> (the right pane when null), as text or
    /// an image, in a dialog.
    /// </summary>
    private async Task PreviewSelectedAsync(SitesPanel? from = null)
    {
        if ((from ?? _sitesPanel) is not { Site: { } site } panel || panel.SelectedFileName is not { } name) return;
        var path = SitesBrowseRules.Join(panel.Path, name);
        using var cancel = new CancellationTokenSource();
        var body = new Grid { MinWidth = 420, MaxWidth = 720 };
        var busy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 8, 0, 8) };
        busy.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        busy.Children.Add(new TextBlock { Text = "Loading…", VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(busy);
        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = name, Content = body, CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close,
        };
        dialog.Closed += (_, _) => cancel.Cancel();
        var shown = DialogGuard.ShowAsync(dialog);
        if (shown.IsCompleted)
        {
            // Another dialog is open: nothing was shown, so the Host is not asked to read the file.
            SetSitesStatus("Another dialog is open; close it and try again.");
            return;
        }

        UIElement content;
        try
        {
            var preview = await _remote.PreviewSiteFileAsync(new SitePreviewRequest { Id = site.Id, Path = path }, cancel.Token);
            content = await PreviewContentAsync(preview);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            content = new TextBlock { Text = "The preview could not be read: " + ex.Message, TextWrapping = TextWrapping.Wrap };
        }

        if (cancel.IsCancellationRequested) return;
        body.Children.Clear();
        body.Children.Add(content);
        await shown;
    }

    private static async Task<UIElement> PreviewContentAsync(SitePreviewDto preview)
    {
        if (!preview.Ok || preview.Data is not { Length: > 0 } data || preview.Kind == SitePreviewKinds.None)
        {
            var words = !preview.Ok ? preview.Message : preview.Message ?? "This kind of file can't be previewed.";
            return new TextBlock { Text = string.IsNullOrWhiteSpace(words) ? "This kind of file can't be previewed." : words, TextWrapping = TextWrapping.Wrap };
        }

        var panel = new StackPanel { Spacing = 8 };
        if (preview.Kind == SitePreviewKinds.Image)
        {
            try
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var writer = System.IO.WindowsRuntimeStreamExtensions.AsStreamForWrite(stream);
                await writer.WriteAsync(data);
                await writer.FlushAsync();
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                panel.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 460, HorizontalAlignment = HorizontalAlignment.Left });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                panel.Children.Add(new TextBlock { Text = "The image could not be drawn.", TextWrapping = TextWrapping.Wrap });
            }
        }
        else
        {
            var text = new TextBlock
            {
                Text = System.Text.Encoding.UTF8.GetString(data), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12,
            };
            panel.Children.Add(new ScrollViewer { Content = text, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        var size = preview.Size >= 0 ? UiFormat.Bytes(preview.Size) : "";
        var note = preview.Truncated
            ? $"Only the start is shown ({UiFormat.Bytes(data.Length)}{(size.Length > 0 ? " of " + size : "")})."
            : size;
        if (note.Length > 0)
        {
            var line = new TextBlock { Text = note, FontSize = 12 };
            InboundUi.Follow(line, "InboundSecondaryTextBrush", brush => line.Foreground = brush);
            panel.Children.Add(line);
        }

        return panel;
    }

    // ---- Sync -------------------------------------------------------------------------------------------

    private sealed record SyncWhen(string Label, bool Wait, bool Every);

    private static readonly SyncWhen[] SyncChoices =
    [
        new("Now", false, false),
        new("At the next schedule start", true, false),
        new("At the start of every schedule", false, true),
    ];

    /// <summary>
    /// "Sync linked folder…": the plan (what is new or changed, with sizes and totals), then Start now, at the next schedule's
    /// start, or at the start of every schedule. Only new and changed files are copied; nothing is deleted.
    /// </summary>
    private async Task SyncLinkedFolderAsync()
    {
        if (_sitesPanel?.Site is not { } site) return;
        if (_connection.IsRemote)
        {
            SetSitesStatus("Sync works for this PC's own remotes only.");
            return;
        }

        if (string.IsNullOrWhiteSpace(site.LocalFolder))
        {
            await ShowSitesMessageAsync("Sync linked folder", $"{site.Name} has no linked folder. Choose one under Edit remote, then sync.");
            return;
        }

        var remoteFolder = string.IsNullOrWhiteSpace(site.RemoteFolder) ? SitesBrowseRules.Root : SitesBrowseRules.Normalize(site.RemoteFolder);
        var scheduled = site.ScheduledSync;
        var direction = new ComboBox { Header = "Direction", HorizontalAlignment = HorizontalAlignment.Stretch };
        direction.Items.Add($"Download: {site.Name} {remoteFolder} to {site.LocalFolder}");
        direction.Items.Add($"Upload: {site.LocalFolder} to {site.Name} {remoteFolder}");
        direction.SelectedIndex = scheduled?.Direction == "upload" ? 1 : 0;
        var when = new ComboBox { Header = "Start", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in SyncChoices) when.Items.Add(choice.Label);
        when.SelectedIndex = 0;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.None };
        var scheduleNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = scheduled is null ? Visibility.Collapsed : Visibility.Visible };
        InboundUi.Follow(scheduleNote, "InboundSecondaryTextBrush", brush => scheduleNote.Foreground = brush);
        if (scheduled is not null)
            scheduleNote.Text = $"A sync ({(scheduled.Direction == "upload" ? "upload" : "download")}) of this folder is set to run at the start of every schedule that has Remote files transfers on.";
        var body = new StackPanel { Spacing = 10, MinWidth = 440 };
        body.Children.Add(direction);
        body.Children.Add(summary);
        body.Children.Add(list);
        body.Children.Add(when);
        body.Children.Add(scheduleNote);
        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = "Sync linked folder", Content = body,
            PrimaryButtonText = "Start", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false,
            SecondaryButtonText = scheduled is null ? string.Empty : "Stop the scheduled sync",
        };

        string Dir() => direction.SelectedIndex == 1 ? "upload" : "download";
        SyncWhen Choice() => SyncChoices[Math.Max(0, when.SelectedIndex)];
        SiteSyncPlanDto? plan = null;
        CancellationTokenSource? planCancel = null;

        void SyncPrimary()
        {
            dialog.PrimaryButtonText = Choice().Every ? "Schedule" : "Start";
            dialog.IsPrimaryButtonEnabled = plan is { Ok: true } && (plan.Count > 0 || Choice().Every);
        }

        async Task PlanAsync()
        {
            planCancel?.Cancel();
            var cancel = planCancel = new CancellationTokenSource();
            plan = null;
            list.ItemsSource = null;
            summary.Text = "Comparing the folders…";
            SyncPrimary();
            SiteSyncPlanDto result;
            try
            {
                result = await _remote.PlanSiteSyncAsync(new SiteSyncRequest { SiteId = site.Id, Direction = Dir(), RemoteFolder = remoteFolder, LocalFolder = site.LocalFolder }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                result = new SiteSyncPlanDto { Ok = false, Message = ex.Message };
            }

            if (cancel.IsCancellationRequested) return;
            plan = result;
            if (!result.Ok)
            {
                summary.Text = string.IsNullOrWhiteSpace(result.Message) ? "The folders could not be compared." : result.Message;
                SyncPrimary();
                return;
            }

            var text = result.Count == 0
                ? $"Nothing to copy: the folders match ({result.Same:N0} files)."
                : $"{result.Count:N0} files to copy ({UiFormat.Bytes(result.Bytes)}); {result.Same:N0} already the same.";
            if (result.NewerThere > 0) text += $" {result.NewerThere:N0} are newer there and are left alone.";
            text += " Nothing is deleted.";
            if (result.Count > result.Files.Count) text += $" The first {result.Files.Count:N0} are listed.";
            summary.Text = text;
            list.ItemsSource = result.Files.Select(file => $"{file.Path}  ·  {(file.Size >= 0 ? UiFormat.Bytes(file.Size) : "")}  ·  {(file.Reason == SiteSyncReasons.Changed ? "changed" : "new")}").ToList();
            SyncPrimary();
        }

        direction.SelectionChanged += (_, _) => _ = PlanAsync();
        when.SelectionChanged += (_, _) => SyncPrimary();
        dialog.Closed += (_, _) => planCancel?.Cancel();
        var planning = PlanAsync();
        var answer = await DialogGuard.ShowAsync(dialog);
        planCancel?.Cancel();
        _ = planning;

        if (answer == ContentDialogResult.Secondary)
        {
            var stopped = await _remote.ScheduleSiteSyncAsync(new SiteSyncScheduleRequest { SiteId = site.Id, Enabled = false, Direction = scheduled?.Direction ?? "download", RemoteFolder = remoteFolder });
            SetSitesStatus(stopped.Ok ? "The scheduled sync is off." : stopped.Message);
            return;
        }

        if (answer != ContentDialogResult.Primary || plan is not { Ok: true }) return;
        var picked = Choice();
        if (picked.Every)
        {
            var reply = await _remote.ScheduleSiteSyncAsync(new SiteSyncScheduleRequest
            {
                SiteId = site.Id, Enabled = true, Direction = Dir(), RemoteFolder = remoteFolder, LocalFolder = site.LocalFolder,
            });
            if (reply.Ok) SetSitesStatus("The sync runs at the start of every schedule that has Remote files transfers on.");
            else await ShowSitesMessageAsync("Sync linked folder", reply.Message ?? "The sync could not be scheduled.");
            return;
        }

        var started = await _remote.RunSiteSyncAsync(new SiteSyncRequest
        {
            SiteId = site.Id, Direction = Dir(), RemoteFolder = remoteFolder, LocalFolder = site.LocalFolder, WaitForSchedule = picked.Wait,
        });
        if (!started.Ok)
        {
            await ShowSitesMessageAsync("Sync linked folder", started.Message ?? "The sync could not start.");
            return;
        }

        if (started.Transfer is { } batch)
        {
            if (_siteTransfers.All(t => t.Id != batch.Id)) ApplySiteTransfers([.. _siteTransfers, batch]);
            if (!picked.Wait) TransfersBar.OpenOnRunning();
            SetSitesStatus(picked.Wait ? "The sync waits for the start of a schedule." : "The sync started.");
        }
        else
        {
            SetSitesStatus(started.Message);
        }
    }

    // ---- Copy between two remotes -----------------------------------------------------------------------

    private Task CopyBetweenAsync(SitesPanel from, SitesPanel to) => OnceAsync(() => CopyBetweenCoreAsync(from, to));

    /// <summary>
    /// The items selected in <paramref name="from"/> go to the folder shown in <paramref name="to"/> as one batch per choice
    /// (<c>between</c>). Names already there are asked about, as for an upload.
    /// </summary>
    private async Task CopyBetweenCoreAsync(SitesPanel from, SitesPanel to)
    {
        if (from.Site is not { } source || to.Site is not { } target) return;
        var names = from.SelectedNames;
        if (names.Count == 0 || from.IsLoading || to.IsLoading) return;
        if (source.Id == target.Id && SitesBrowseRules.Normalize(from.Path) == SitesBrowseRules.Normalize(to.Path))
        {
            SetSitesStatus("Both panes show the same folder. Open another folder in one of them first.");
            return;
        }

        var existing = to.ShownItems.ToList();
        var targets = existing.Select(item => new KeyValuePair<string, SitesCommandRules.ConflictSide>(item.Name, new SitesCommandRules.ConflictSide(item.Size, item.Modified))).ToList();
        var sources = new Dictionary<string, SitesCommandRules.ConflictSide>(StringComparer.Ordinal);
        foreach (var item in from.ShownItems) sources[item.Name] = new SitesCommandRules.ConflictSide(item.IsFolder ? -1 : item.Size, item.Modified);
        var pairs = SitesCommandRules.ConflictPairs(names, sources, targets, ignoreCase: true);
        if (await AskConflictsAsync(pairs, upload: true, $"{to.Path} on {target.Name}", names.Any(name => name.IsFolder), to.ListingComplete,
                [.. targets.Select(t => t.Key)], SitesCommandRules.TimesArePrecise(target.Kind)) is not { } answer) return;

        var sourceFolder = from.Path;
        var targetFolder = to.Path;
        await StartConflictBatchesAsync(names, answer.Choices, answer.Other, (conflict, batch) => new SiteTransferStartRequest
        {
            SiteId = source.Id, Direction = "between", RemoteFolder = sourceFolder, TargetSiteId = target.Id, TargetFolder = targetFolder,
            Names = batch, Conflict = conflict,
        });
    }
}
