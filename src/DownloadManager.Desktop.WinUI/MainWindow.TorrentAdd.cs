using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T1 step 5, the ways a torrent comes in: Tasks › Add Magnet… / Import Torrent File…, Add URL, drag and drop and Ctrl+V
/// on the list all lead to <see cref="TorrentAddDialog"/>. With torrents off, the provider's own flow takes them as
/// before when an account can.
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
        if (!TorrentsCanBeAdded) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add torrents";
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!TorrentsCanBeAdded) return;
            var deferral = e.GetDeferral();
            var inputs = new List<string>();
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
                    inputs.AddRange(TorrentInputsIn(await e.DataView.GetTextAsync(), out offThisPc));
                }
            }
            finally
            {
                deferral.Complete();
            }

            if (inputs.Count == 0)
            {
                ShowPassingStatus(offThisPc ? TorrentRules.OnlyLocalTorrentFiles : "Only magnet links and .torrent files can be dropped here.");
                return;
            }

            await AddTorrentsAsync("mixed", inputs);
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
        if (e.Key != Windows.System.VirtualKey.V || !TorrentsCanBeAdded) return false;
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
                    ShowPassingStatus("The clipboard holds no magnet link or .torrent file.");
                    return;
                }

                text = await clip.GetTextAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                ShowPassingStatus("The clipboard couldn't be read.");
                return;
            }

            var inputs = TorrentInputsIn(text, out var offThisPc);
            if (inputs.Count == 0)
            {
                ShowPassingStatus(offThisPc ? TorrentRules.OnlyLocalTorrentFiles : "The clipboard holds no magnet link or .torrent file.");
                return;
            }

            await AddTorrentsAsync("mixed", inputs);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Pasting torrents failed: {ex.GetType().Name}");
        }
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
    private bool _waitingForDialogClose;

    private async Task OpenQueuedTorrentsAsync()
    {
        try
        {
            TorrentOpenRequests.Hold(_heldTorrentInputs, TorrentOpenRequests.Drain(App.TorrentQueueDirectory(), DateTimeOffset.UtcNow));
            if (_heldTorrentInputs.Count == 0) return;
            if (DialogGuard.IsOpen(Content.XamlRoot))
            {
                WaitForDialogClose();
                return;
            }

            var queued = _heldTorrentInputs.ToList();
            _heldTorrentInputs.Clear();
            var inputs = queued.Where(input => !TorrentRules.IsNetworkOrDevicePath(input)).ToList();
            if (inputs.Count == 0)
            {
                ShowPassingStatus(TorrentRules.OnlyLocalTorrentFiles);
                return;
            }

            if (await AddTorrentsAsync(TorrentOpenRequests.Focus(inputs), inputs, holdWhenBlocked: true))
            {
                // Another dialog opened first after all: keep them for when it closes.
                TorrentOpenRequests.Hold(_heldTorrentInputs, inputs);
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
                await ShowMessageAsync("Add torrents", "This Host doesn't download torrents.");
            return false;
        }

        var connection = _connection;
        await RefreshSettingsAsync();
        // Another server may have been chosen while the Host answered, or torrents turned off: then this add is dropped.
        if (!ReferenceEquals(connection, _connection) || _settings is not { } settings || !LocalTorrentsApply) return false;
        await _connection.ProbeTorrentControlsAsync();
        if (!ReferenceEquals(connection, _connection)) return false;
        var dialog = new TorrentAddDialog(_remote, settings, TorrentProviderName(), RootGrid, WinRT.Interop.WindowNative.GetWindowHandle(this),
            withControls: TorrentControls);
        var choice = await dialog.ShowAsync(Content.XamlRoot, RootGrid.ActualTheme, focus, inputs);
        if (choice is null)
        {
            if (dialog.CouldNotOpen && holdWhenBlocked) return true;
            if (dialog.CouldNotOpen) ShowPassingStatus("The torrent dialog couldn't open while another dialog is open.");
            return false;
        }

        if (choice.ShowJobId is { } listed)
        {
            ShowTorrentRow(listed);
            return false;
        }

        await RunTorrentChoiceAsync(choice);
        return false;
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
        if (status is not null) ShowPassingStatus(status);
        if (last is { } id && _jobsById.TryGetValue(id, out var row)) JobsList.ScrollIntoView(row);

        // The provider's lines go through its own dialog, which checks them and asks about their files.
        var toProvider = choice.Picks.Where(pick => pick.Route == TorrentLineRoute.Provider).Select(pick => pick.Input).ToList();
        if (toProvider.Count > 0)
        {
            await ShowProviderAddAsync(TorrentRules.ProviderFocus(toProvider), toProvider);
        }

        if (problems.Count > 0) await ShowMessageAsync("Add torrents", string.Join(Environment.NewLine, problems));
    }
}
