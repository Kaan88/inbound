using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T1 step 5, the ways a torrent comes in: Tasks › Add Magnet… / Import Torrent File…, Add URL, drag and drop and Ctrl+V
/// on the list all lead to <see cref="TorrentAddDialog"/>. With torrents off, the provider's own flow takes them as
/// before when an account can. Review 5:A6: http and https links dropped or pasted the same way go to Add Links.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The shown Host has the torrent engine and torrents are turned on in its Options.</summary>
    private bool LocalTorrentsApply => TorrentRules.Applies(_remote.State.TorrentsOffered, _settings);

    /// <summary>A provider account with a usable key that takes torrents, or null.</summary>
    private RemoteAccountDto? TorrentProviderAccount()
        => _remote.State.RemoteSupported
            ? _remote.State.Accounts.FirstOrDefault(account => RemoteRules.HasUsableKey(account) &&
                account.Capabilities.Contains("torrents", StringComparer.OrdinalIgnoreCase))
            : null;

    private string? TorrentProviderName() => TorrentProviderAccount() is { } account ? RemoteText.ProviderName(account) : null;

    private const string TorrentsOffText = "Torrents are turned off. Turn them on in Options › Torrents, or add this magnet to TorBox if you have an account.";

    /// <summary>The Tasks items, the sidebar view and the chip follow the setting and the Host's capability.</summary>
    internal void SyncTorrentEntryPoints()
    {
        _remoteTasksOffers = string.Empty;
        SyncRemoteEntryPoints();
        SyncTorrentViews();
    }

    private void InitializeTorrentDrop()
    {
        RootGrid.AllowDrop = true;
        RootGrid.DragOver += RootGrid_DragOver;
        RootGrid.Drop += RootGrid_Drop;
    }

    private bool TorrentsCanBeAdded => LocalTorrentsApply || TorrentProviderAccount() is not null;

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_settings is null) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add downloads";
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (_settings is null) return;
            var deferral = e.GetDeferral();
            var inputs = new List<string>();
            var links = new List<string>();
            var offThisPc = false;
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    foreach (var item in await e.DataView.GetStorageItemsAsync())
                    {
                        if (item is not Windows.Storage.IStorageFile file || !Path.IsPathFullyQualified(file.Path) ||
                            !LinkExtractor.IsTorrentPath(file.Path)) continue;
                        // A file on a network share isn't read: even opening it sends this account's credentials there.
                        if (TorrentRules.IsNetworkOrDevicePath(file.Path)) offThisPc = true;
                        else inputs.Add(file.Path);
                    }
                }
                else if (e.DataView.Contains(StandardDataFormats.Text))
                {
                    var text = await e.DataView.GetTextAsync();
                    inputs.AddRange(TorrentInputsIn(text, out offThisPc));
                    links.AddRange(LinkExtractor.HttpLinks(text));
                }
            }
            finally
            {
                deferral.Complete();
            }

            await TakeInputsAsync(inputs, links, offThisPc, dropped: true);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Dropping torrents failed: {ex.GetType().Name}");
            ShowPassingStatus("What was dropped couldn't be read.");
        }
    }

    /// <summary>Ctrl+V on the list: magnets and .torrent paths on the clipboard go to the torrent dialog.</summary>
    private bool HandleTorrentPaste(KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.V || _settings is null) return false;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if ((ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return false;
        if (DialogGuard.IsOpen(Content.XamlRoot)) return false;
        e.Handled = true;
        _ = PasteTorrentsAsync();
        return true;
    }

    private async Task PasteTorrentsAsync()
    {
        try
        {
            string text;
            try
            {
                var clip = Clipboard.GetContent();
                if (!clip.Contains(StandardDataFormats.Text))
                {
                    ShowPassingStatus(TorrentRules.NothingOnClipboard);
                    return;
                }

                text = await clip.GetTextAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                ShowPassingStatus("The clipboard couldn't be read.");
                return;
            }

            await TakeInputsAsync(TorrentInputsIn(text, out var offThisPc), LinkExtractor.HttpLinks(text), offThisPc, dropped: false);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Pasting torrents failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// A drop's or a paste's torrents go to the torrent dialog, its links to Add Links (after the torrent dialog when
    /// there are both); <see cref="TorrentRules.Intake"/> decides, and says why when nothing goes anywhere.
    /// </summary>
    private async Task TakeInputsAsync(List<string> torrents, List<string> links, bool offThisPc, bool dropped)
    {
        var (takeTorrents, takeLinks, problem) = TorrentRules.Intake(torrents.Count, links.Count, TorrentsCanBeAdded, offThisPc, dropped);
        if (problem is not null)
        {
            ShowPassingStatus(problem);
            return;
        }

        if (takeTorrents) await AddTorrentsAsync("mixed", torrents);
        if (takeLinks) await ShowAddLinksAsync(links);
    }

    /// <summary>
    /// The magnets and .torrent paths in the text. Paths on a network share or a device are left out, never read;
    /// <paramref name="offThisPc"/> says that was all there was.
    /// </summary>
    private static List<string> TorrentInputsIn(string text, out bool offThisPc)
    {
        var found = LinkExtractor.Inputs(text).Where(TorrentRules.IsTorrentInput).Distinct(StringComparer.Ordinal).ToList();
        var kept = found.Where(input => !TorrentRules.IsNetworkOrDevicePath(input)).ToList();
        offThisPc = kept.Count == 0 && found.Count > 0;
        return kept;
    }

    private bool _pendingTorrentOpen;

    /// <summary>
    /// T1 step 6b: Windows opened a magnet or .torrent file with Inbound, and the launch queued it
    /// (<see cref="TorrentOpenRequests"/>). The torrent dialog opens with it once the Host has answered.
    /// </summary>
    public void RequestQueuedTorrents()
    {
        ShowFromTray();
        if (_settings is null) _pendingTorrentOpen = true;
        else _ = OpenQueuedTorrentsAsync();
    }

    // Torrents Windows opened while another dialog was showing: held here (not in the queue folder, where they would
    // expire) and opened when that dialog closes, so none is lost.
    private readonly List<string> _heldTorrentInputs = new();
    // T10: those of them this PC's Host sent for its finished .torrent downloads.
    private readonly HashSet<string> _heldFromDownloads = new(StringComparer.Ordinal);
    private bool _waitingForDialogClose;

    private async Task OpenQueuedTorrentsAsync()
    {
        try
        {
            TorrentOpenRequests.Hold(_heldTorrentInputs, TorrentOpenRequests.Drain(App.TorrentQueueDirectory(), DateTimeOffset.UtcNow, _heldFromDownloads));
            if (_heldTorrentInputs.Count == 0) return;
            if (DialogGuard.IsOpen(Content.XamlRoot))
            {
                WaitForDialogClose();
                return;
            }

            var queued = _heldTorrentInputs.ToList();
            var fromDownloads = _heldFromDownloads.ToHashSet(StringComparer.Ordinal);
            _heldTorrentInputs.Clear();
            _heldFromDownloads.Clear();
            var inputs = TorrentRules.QueuedToOpen(queued, fromDownloads, _connection.IsRemote, TorrentFileJobFor, out var keptOnThisPc, out _);
            if (inputs.Count == 0)
            {
                ShowPassingStatus(keptOnThisPc ? TorrentRules.TorrentFilesStayOnThisPc : TorrentRules.OnlyLocalTorrentFiles);
                return;
            }

            if (await AddTorrentsAsync(TorrentOpenRequests.Focus(inputs), inputs, holdWhenBlocked: true))
            {
                // Another dialog opened first after all: keep them for when it closes.
                TorrentOpenRequests.Hold(_heldTorrentInputs, inputs);
                _heldFromDownloads.UnionWith(inputs.Where(fromDownloads.Contains));
                WaitForDialogClose();
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Opening a torrent from Windows failed: {ex.GetType().Name}");
        }
    }

    private void WaitForDialogClose()
    {
        if (_waitingForDialogClose) return;
        _waitingForDialogClose = true;
        DialogGuard.Closed += OnDialogClosedForHeldTorrents;
    }

    private void OnDialogClosedForHeldTorrents(XamlRoot root)
    {
        if (!ReferenceEquals(root, Content?.XamlRoot)) return;
        DialogGuard.Closed -= OnDialogClosedForHeldTorrents;
        _waitingForDialogClose = false;
        // Runs after the closing dialog's own caller has moved on; a dialog it opens next is waited for again.
        DispatcherQueue.TryEnqueue(() => _ = OpenQueuedTorrentsAsync());
    }

    private async Task AddMagnetAsync()
    {
        if (LocalTorrentsApply)
        {
            var clip = await ClipboardInputsAsync(AddFocus.Magnet);
            await AddTorrentsAsync("magnet", clip is null ? [] : TorrentInputsIn(clip, out _).Where(LinkExtractor.IsMagnet).ToList());
        }
        else
        {
            await ShowProviderAddAsync(AddFocus.Magnet);
        }
    }

    private async Task ImportTorrentFilesForAnyRouteAsync()
    {
        if (!LocalTorrentsApply)
        {
            await ImportTorrentFilesAsync();
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), [".torrent"], multiple: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Import torrent file", "The file picker couldn't open.");
            return;
        }

        if (files.Count > 0) await AddTorrentsAsync("torrentFiles", files.ToList());
    }

    /// <summary>
    /// The torrent dialog for these inputs, then each line where it was sent. With torrents off here, the provider
    /// takes them when an account can; otherwise the user is told how to turn torrents on.
    /// </summary>
    /// <returns>True only when <paramref name="holdWhenBlocked"/> is set and the torrent dialog couldn't open because
    /// another dialog was open; the caller keeps the inputs for later.</returns>
    private async Task<bool> AddTorrentsAsync(string focus, IReadOnlyList<string> inputs, bool holdWhenBlocked = false)
    {
        if (_settings is null) return false;
        if (!LocalTorrentsApply)
        {
            if (TorrentProviderAccount() is not null)
            {
                await ShowProviderAddAsync(TorrentRules.ProviderFocus(inputs), inputs);
                return false;
            }

            if (_remote.State.TorrentsOffered && await ConfirmAsync("Add torrents", TorrentsOffText, "Open Options"))
                await OpenOptionsAsync();
            else if (!_remote.State.TorrentsOffered)
                await ShowMessageAsync("Add torrents", "Torrents aren't available here.");
            return false;
        }

        var connection = _connection;
        await RefreshSettingsAsync();
        // Another server may have been chosen while the Host answered, or torrents turned off: then this add is dropped.
        if (!ReferenceEquals(connection, _connection) || _settings is not { } settings || !LocalTorrentsApply) return false;
        await _connection.ProbeTorrentControlsAsync();
        if (!ReferenceEquals(connection, _connection)) return false;
        var dialog = new TorrentAddDialog(_remote, settings, TorrentProviderName(), RootGrid, WinRT.Interop.WindowNative.GetWindowHandle(this),
            withControls: TorrentControls, jobFor: TorrentFileJobFor);
        var choice = await dialog.ShowAsync(Content.XamlRoot, RootGrid.ActualTheme, focus, inputs);
        if (choice is null)
        {
            if (dialog.CouldNotOpen && holdWhenBlocked) return true;
            if (dialog.CouldNotOpen) ShowPassingStatus("The torrent dialog couldn't open while another dialog is open.");
            return false;
        }

        // T10: a finished .torrent download whose torrent is already in the list is a copy: it goes, as an add would.
        await RemoveListedTorrentFilesAsync(choice.ListedInputs);
        if (choice.ShowJobId is { } listed)
        {
            ShowTorrentRow(listed);
            return false;
        }

        await RunTorrentChoiceAsync(choice);
        return false;
    }

    /// <summary>
    /// T10: the finished .torrent download this dialog line's path is the file of, or null. On another server only for a
    /// path one of its rows' Open as Torrent is adding (<see cref="_openedServerRows"/>, T10.B2).
    /// </summary>
    private Guid? TorrentFileJobFor(string input)
        => TorrentRules.TorrentFileJobFor(_jobsById.Values.Select(row => row.Dto).OfType<JobDto>(), input, _connection.IsRemote, _openedServerRows.Keys);

    // T10.B2: paths of the shown server's rows whose Open as Torrent is being added, counted. Only these are the server's
    // own files; the same path from anywhere else is a file on this PC. UI thread only.
    private readonly Dictionary<string, int> _openedServerRows = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// T10: finished .torrent downloads whose torrent turned out to be in the list already: each is sent as an add from
    /// its id, which the Host answers with Existing and removes the download and its file.
    /// </summary>
    private async Task RemoveListedTorrentFilesAsync(IReadOnlyList<string> inputs)
    {
        foreach (var input in inputs)
        {
            if (TorrentFileJobFor(input) is not { } id) continue;
            try
            {
                // Only removes the copy: when the torrent has left the list meanwhile, nothing is added and the row stays.
                var result = await _remote.AddTorrentAsync(new TorrentAddRequest { Input = input, FromJobId = id, OnlyIfListed = true, StartMode = "later" });
                if (!result.Ok) UiTrace.Write("A listed .torrent download stays: the Host refused it");
            }
            catch (Exception ex)
            {
                UiTrace.Write($"A listed .torrent download stays: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>T10: a finished .torrent download a provider took is removed with its file.</summary>
    private async Task RemoveTorrentFileDownloadAsync(Guid id)
    {
        try
        {
            await _connection.RemoveAsync(id, deleteFile: true);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"The .torrent download stays after the provider took it: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// T10: a row's Open as Torrent: the torrent dialog with its file as a line, as the Host opens it when the download
    /// finishes. With torrents off and no account, the usual note with Open Options.
    /// </summary>
    private async Task OpenAsTorrentAsync()
    {
        if (SelectedJob?.Dto is not { } job || !TorrentRules.OpensAsTorrent(job)) return;
        var path = job.FinalFilePath.Trim();
        if (!_connection.IsRemote)
        {
            await AddTorrentsAsync("torrentFiles", [path]);
            return;
        }

        // T10.B2: on another server the file is the server's own: it is read there by the download's id (This PC) or by
        // its path (the provider, with the server's own account, as its web UI does), never looked for on this PC.
        _openedServerRows[path] = _openedServerRows.GetValueOrDefault(path) + 1;
        try
        {
            using var mark = _remote.UseServerFiles([path]);
            await AddTorrentsAsync("torrentFiles", [path]);
        }
        finally
        {
            if (_openedServerRows.TryGetValue(path, out var count) && count > 1) _openedServerRows[path] = count - 1;
            else _openedServerRows.Remove(path);
        }
    }

    /// <summary>A job already on the list: selected and in view, on All Downloads if this view or the search hides it.</summary>
    private void ShowTorrentRow(Guid id)
    {
        if (!_jobsById.TryGetValue(id, out var row))
        {
            ShowPassingStatus("That torrent is no longer in the list.");
            return;
        }

        if (!_visibleJobs.Contains(row))
        {
            if (!MatchesSearch(row)) _search = string.Empty;
            _category = "all";
            CategoryList.SelectedIndex = 0;
        }

        RebuildVisible(selectedId: id);
        if (!_visibleJobs.Contains(row)) return;
        JobsList.ScrollIntoView(row);
        JobsList.UpdateLayout();
        if (JobsList.ContainerFromItem(row) is Microsoft.UI.Xaml.Controls.Control item) item.Focus(FocusState.Keyboard);
    }

    private async Task RunTorrentChoiceAsync(TorrentAddChoice choice)
    {
        var problems = new List<string>();
        var added = 0;
        var existing = 0;
        Guid? last = null;
        foreach (var pick in choice.Picks.Where(pick => pick.Route == TorrentLineRoute.ThisPc))
        {
            var result = await _remote.AddTorrentAsync(new TorrentAddRequest
            {
                Input = pick.Input,
                // T10: a finished .torrent download is added by its id; the Host then removes it and its file.
                FromJobId = TorrentFileJobFor(pick.Input),
                Destination = choice.Destination,
                QueueId = choice.QueueId,
                StartMode = choice.StartMode,
                Files = pick.Files,
                FileIndexes = pick.FileIndexes?.ToList(),
                Priorities = pick.Priorities?.ToList(),
            });
            if (!result.Ok)
            {
                problems.Add($"{pick.Name}: {result.Error ?? "It couldn't be added."}");
                continue;
            }

            if (result.Existing) existing++;
            else added++;
            last = result.Job?.Id ?? last;
        }

        var status = (added, existing) switch
        {
            (0, 0) => null,
            (1, 0) => "Added 1 torrent.",
            (_, 0) => $"Added {added} torrents.",
            (0, 1) => "That torrent is already in the list.",
            (0, _) => $"Those {existing} torrents are already in the list.",
            _ => $"Added {added} torrents; {existing} were already in the list.",
        };
        if (last is { } id && _jobsById.TryGetValue(id, out var row)) JobsList.ScrollIntoView(row);

        // T9 W2: the provider's lines are sent from here with the torrent dialog's folder, start and queue; no second dialog.
        var toProvider = choice.Picks.Where(pick => pick.Route == TorrentLineRoute.Provider).ToList();
        var jobs = new List<RemoteJobDto>();
        var sent = 0;
        var provider = ProviderWordForAdding();
        if (toProvider.Count > 0) ShowPassingStatus($"Adding to {provider}…");
        foreach (var pick in toProvider)
        {
            var fromJob = TorrentFileJobFor(pick.Input);
            var result = await _remote.AddAsync(TorrentAddRequests.ForProvider(pick, choice.Destination, choice.StartMode, choice.QueueId, _settings?.DefaultQueueId ?? 0));
            if (result is { Ok: true, Job: { } job }) jobs.Add(job);
            // T10: the provider read the .torrent file as it was added, so the finished download and its file can go.
            if (result.Ok && fromJob is { } finished) await RemoveTorrentFileDownloadAsync(finished);
            if (!result.Ok) problems.Add(RemoteText.Refused(pick.Name, result.Error, provider));
            else if (result.Existing) problems.Add(RemoteText.AlreadyListed(result.Job, pick.Name, result.Note));
            else if (result.Job is not null) sent++;
        }

        var said = string.Join(" ", new[] { status ?? string.Empty, RemoteText.AddedWords(0, sent, provider) }.Where(part => part.Length > 0));
        if (problems.Count > 0)
        {
            await ShowMessageAsync("Add torrents", RemoteText.AddReport(said, [], problems));
            EndPassingStatus();
        }
        else if (said.Length > 0)
        {
            ShowPassingStatus(said);
        }
        else
        {
            EndPassingStatus();
        }

        // One provider item and nothing for This PC: its row is shown, and a cached one's file choice may open by itself.
        if (jobs.Count > 0) FollowAdd(jobs, added + existing);
    }
}
