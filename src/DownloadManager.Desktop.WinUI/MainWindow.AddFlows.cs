using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Adding through a provider: which way each input goes, and what happens after the add dialog closes.
/// Adding is the decision: <c>remote.add</c> saves the intent with the go-ahead, and the rows on the
/// download list follow it from there. Nothing asks again after an add, except for a choice of files.
/// </summary>
public sealed partial class MainWindow
{
    private readonly AutoOpenWatch _autoOpen = new();
    private bool _isForeground;

    private enum AddressWay
    {
        Cancelled,
        /// <summary>An ordinary download: an ordinary link, or the user's explicit choice for one nobody could classify.</summary>
        Direct,
        Provider,
    }

    /// <param name="ChosenDirect">The Host couldn't tell what it is, or couldn't fully check it, and the user asked for a direct request anyway.</param>
    private sealed record CheckedAddress(AddressWay Way, string Input, RemoteClassifyResult? Result, bool ChosenDirect = false);

    /// <summary>
    /// The dialogs follow what the Host says about each input only when an account could take one. With
    /// no usable key they stay exactly as they are. If the accounts couldn't be read nobody knows, so the
    /// Host gets asked about each line, which gives the right answer either way.
    /// </summary>
    private async Task<bool> ProviderAddAppliesAsync()
    {
        var state = _remote.State;
        if (!state.IsReady) await _remote.WaitUntilReadyAsync(TimeSpan.FromSeconds(1.5));
        return RemoteRules.ProviderAddApplies(state);
    }

    /// <summary>
    /// For an address a command already has, with no dialog of its own to stay open in. Something the
    /// Host can't classify, or couldn't fully check, gets a retry and an explicit way past, and something it
    /// can't use is said here.
    /// </summary>
    private async Task<CheckedAddress> CheckGivenAddressAsync(string title, string input)
    {
        // UI.B27: an address on this PC or its local network is an ordinary download; no provider is asked about it.
        if (NetworkAddresses.IsLocalWebAddress(input)) return new CheckedAddress(AddressWay.Direct, input, null);
        while (true)
        {
            ShowPassingStatus("Checking this address…");
            var result = await _remote.ClassifyAsync(input);
            var line = new AddLine(input) { Result = result };
            switch (line.Route)
            {
                case AddRoute.Direct when !line.CheckIncomplete:
                    return new CheckedAddress(AddressWay.Direct, input, result);
                case AddRoute.Provider or AddRoute.FolderLink:
                    return new CheckedAddress(AddressWay.Provider, input, result);
            }

            var words = line.CheckIncomplete
                ? RemoteText.CheckIncompleteWords(result, ProviderNameFor(result))
                : RemoteText.AddLineWords(line, ProviderNameFor(result)).Detail;
            if (!line.CantTell && !line.CheckIncomplete)
            {
                await ShowMessageAsync(title, words);
                return new CheckedAddress(AddressWay.Cancelled, input, result);
            }

            switch (await AskAsync(title, words, "Try again", "Download directly"))
            {
                case ContentDialogResult.Secondary:
                    return new CheckedAddress(AddressWay.Direct, input, result, ChosenDirect: true);
                case ContentDialogResult.None:
                    return new CheckedAddress(AddressWay.Cancelled, input, result);
            }
        }
    }

    private string ProviderNameFor(RemoteClassifyResult? result)
        => string.IsNullOrWhiteSpace(result?.ProviderName) ? ProviderWordForAdding() : result.ProviderName;

    /// <summary>A question with two answers and Cancel. If another dialog is open, it counts as Cancel.</summary>
    private async Task<ContentDialogResult> AskAsync(string title, string words, string primary, string secondary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = words, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Refused while another dialog is open, which counts as Cancel.
        return await ShowDialogAsync(dialog) ?? ContentDialogResult.None;
    }

    private string ProviderWordForAdding()
    {
        var accounts = _remote.State.Accounts;
        var account = accounts.FirstOrDefault(RemoteRules.HasUsableKey) ?? accounts.FirstOrDefault();
        return account is null ? "the provider" : RemoteText.ProviderName(account);
    }

    /// <summary>The add dialog for this way in, and everything that follows from what it was told. False when it could not open.</summary>
    private async Task<bool> ShowProviderAddAsync(AddFocus focus, IReadOnlyList<string>? inputs = null, RemoteClassifyResult? known = null, IReadOnlyList<(string Input, RemoteClassifyResult Result)>? answered = null)
    {
        if (_settings is null) return false;
        AddLinksChoice? choice;
        bool couldNotOpen;

        // Closing it closes its connection: nothing is still being asked while the next steps run.
        using (var session = new AddLineSession(
            focus,
            () => _remote.OpenClassifier(withFiles: true),
            link => FindExistingJob(link) is not null,
            action => DispatcherQueue.TryEnqueue(() => action())))
        {
            if (inputs is [var only] && known is not null) session.Seed(only, known);
            else if (inputs is not null) session.SetInputs(inputs);
            foreach (var (input, result) in answered ?? []) session.Seed(input, result);

            // Given inputs are the lines and the box's text, so an edit in the box keeps them; the clipboard only seeds
            // an empty dialog.
            var initial = focus is not (AddFocus.Links or AddFocus.Magnet) ? null
                : inputs is not null ? string.Join(Environment.NewLine, inputs)
                : answered is null ? await ClipboardInputsAsync(focus)
                : null;
            var connection = _connection;
            await RefreshSettingsAsync();
            // Another server may have been chosen while the Host answered; this dialog belonged to the one before.
            if (!ReferenceEquals(connection, _connection) || _settings is not { } settings) return true;
            var dialog = new AddLinksDialog(
                session,
                focus,
                settings,
                ProviderWordForAdding(),
                _remote.State.ArchivesSupported,
                RootGrid,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            choice = await dialog.ShowAsync(Content.XamlRoot, RootGrid.ActualTheme, initial);
            couldNotOpen = dialog.CouldNotOpen;
        }

        if (choice is not null) await RunAddChoiceAsync(choice);
        return !couldNotOpen;
    }

    /// <summary>Like Add Links today: starts from the clipboard when it holds something that could be added.</summary>
    private static async Task<string?> ClipboardInputsAsync(AddFocus focus)
    {
        try
        {
            var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return null;
            var text = await clip.GetTextAsync();
            var found = LinkExtractor.Inputs(text);
            return (focus == AddFocus.Magnet ? found.Any(LinkExtractor.IsMagnet) : found.Count > 0) ? text : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The clipboard can be locked by another app; the box just starts empty.
            return null;
        }
    }

    private async Task ImportTorrentFilesAsync()
    {
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

        if (files.Count > 0) await ShowProviderAddAsync(AddFocus.TorrentFiles, files.ToList());
    }

    /// <summary>
    /// A picker, then the import dialog, then what became of each entry. The file's path only ever goes to the
    /// Host: nothing here looks at it, because even asking about a file on a network path sends this Windows
    /// account's credentials there. The Host refuses those, and the dialog says so.
    /// </summary>
    private async Task ImportJdContainerAsync()
    {
        if (_settings is null) return;
        // Another server reads a container by its own path, or by an upload where its hello offers that.
        if (RemoteDataRules.LocalFileProblem(_connection.IsRemote, "container", _connection.Target.Name, _remote.State.UploadsOffered) is { } problem)
        {
            await ShowMessageAsync("Import Link Container", problem);
            return;
        }

        string path;
        try
        {
            if (await Pickers.PickFilesAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), ContainerFiles.Types, multiple: false) is not [var file]) return;
            path = file;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Import Link Container", "The file picker couldn't open.");
            return;
        }

        // A file from a cloud or phone location has no path a program could read.
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowMessageAsync("Import Link Container", ContainerFiles.NoPathWords);
            return;
        }

        var provider = ProviderWordForAdding();
        var dialog = new ContainerImportDialog(_remote, _settings, provider, path, RootGrid, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var answer = await dialog.ShowAsync(Content.XamlRoot, RootGrid.ActualTheme);
        if (answer.CouldNotOpen)
        {
            ShowPassingStatus("Import Link Container couldn't open, because another dialog is open.");
            return;
        }

        if (answer.Stopped)
        {
            await _remote.ListAsync();
            ShowPassingStatus(ContainerFiles.StoppedWords);
        }

        if (answer.Outcomes is not { } outcomes) return;
        if (outcomes.AddedCount > 0 && answer.Folder is { } folder) RememberFolder(folder);
        var problems = outcomes.Problems;
        await ReportAddAsync("Import Link Container", 0, outcomes.NewCount, provider, problems.Count > 0 && outcomes.NewCount == 0 ? [outcomes.Summary(provider)] : [], problems);
    }

    private async Task RunAddChoiceAsync(AddLinksChoice choice)
    {
        var provider = ProviderWordForAdding();
        var problems = new List<string>();
        var messages = new List<string>();
        var downloads = 0;

        // Ordinary links first, the same way as always.
        var direct = choice.Picks.Where(pick => pick.Line.Route == AddRoute.Direct).Select(pick => pick.Line.Input).ToList();
        if (direct.Count > 0)
        {
            var added = await AddOrdinaryLinksAsync(direct, choice.KeepPackage, choice.Destination, choice.StartLater ? "later" : "now", choice.QueueId, choice.ConflictIndex);
            await RefreshAsync();

            // Its own summary only when one failed or was already queued. A clean add is a count with the rest.
            if (added.NeedsSaying) messages.Add(added.Summary);
            else downloads = added.Accepted;
            await SetPackagePasswordAsync(choice, added, problems);
        }

        choice.ArchivePassword = null;

        var providerPicks = await ChooseFilesBeforeAddingAsync(choice, provider, problems);

        // A folder link names an item already on the account. Nothing is asked here: its row asks for the files, as
        // anything else that asks does, and a Cancel there leaves the row rather than nothing.
        var picks = choice.Picks
            .Where(pick => pick.Line.Route == AddRoute.FolderLink)
            .Select(pick => (Pick: pick, Files: (AddFileChoice?)null, Folder: true))
            .Concat(providerPicks.Select(provided => (provided.Pick, provided.Files, Folder: false)))
            .ToList();

        // Every job this add came to, new or found again, for what follows it on the list.
        var jobs = new List<RemoteJobDto>();
        var sent = 0;

        // Passing, like every other status: set directly it stayed until something else wrote there.
        if (picks.Count > 0) ShowPassingStatus($"Adding to {provider}…");
        foreach (var (pick, files, folder) in picks)
        {
            var request = folder
                ? AddRequests.ForFolderLink(pick.Line, choice.Destination, choice.QueueId, choice.StartLater)
                : AddRequests.ForLine(pick.Line, choice.Destination, choice.QueueId, choice.StartLater, pick.LinkPassword, files, pick.ArchivePassword);
            pick.LinkPassword = null;
            pick.ArchivePassword = null;
            if (request is null) continue;
            var name = RemoteText.AddLineWords(pick.Line, provider).Name;
            // T10: a finished .torrent download (Open as Torrent, or the Host's offer with torrents off here).
            var torrentFileJob = TorrentFileJobFor(pick.Line.Input);
            var result = await _remote.AddAsync(request);
            if (result is { Ok: true, Job: { } job }) jobs.Add(job);
            // The provider read the .torrent file as it was added, so the finished download and its file can go.
            if (result.Ok && torrentFileJob is { } finished) await RemoveTorrentFileDownloadAsync(finished);

            // A refusal is the end of it for this link. Nothing here tries it any other way. Found again, it says what
            // of this add it didn't use, such as a folder.
            if (!result.Ok) problems.Add(RemoteText.Refused(name, result.Error, provider));
            else if (result.Existing) problems.Add(RemoteText.AlreadyListed(result.Job, name, result.Note));
            else if (result.Job is not null) sent++;
        }

        await ReportAddAsync("Add links", downloads, sent, provider, messages, problems);
        FollowAdd(jobs, direct.Count);
    }

    /// <summary>
    /// The end of an add, once everything it had to say has been said: with one item and no direct download, its row
    /// is selected and shown, and the file choice may open by itself for the one item that can ask.
    /// </summary>
    private void FollowAdd(IReadOnlyList<RemoteJobDto> jobs, int downloads)
    {
        if (AddFollowUp.RowToShow(jobs, downloads) is { } id) ShowRemoteRow(id);
        ArmAutoOpen(jobs);
    }

    /// <summary>After an add, or Enter on a copied row: the input clock starts now, and the files may already be ready.</summary>
    private void ArmAutoOpen(IReadOnlyList<RemoteJobDto> jobs)
    {
        _autoOpen.Arm(jobs, DateTimeOffset.UtcNow);
        CheckAutoOpen();
    }

    /// <summary>On arming and on every change to the jobs. The job is read again from the latest state just before it opens.</summary>
    private void CheckAutoOpen()
    {
        if (!_autoOpen.IsArmed) return;
        var popupOpen = Content?.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is not ToolTip);
        if (_autoOpen.Check(_remote.State.Jobs, DateTimeOffset.UtcNow, _isForeground, popupOpen) is not { } id) return;
        if (_remote.State.Jobs.FirstOrDefault(job => job.Id == id) is { State: "awaitingSelection" } ready) _ = SelectRemoteFilesAsync(ready);
    }

    /// <summary>
    /// Watches what disarms the file choice that opens by itself: any key or pointer press in the window, even one a
    /// control handles, and the window going behind another.
    /// </summary>
    private void InitializeAutoOpen()
    {
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _autoOpen.Disarm()), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _autoOpen.Disarm()), handledEventsToo: true);
        Activated += (_, args) =>
        {
#if HAS_UNO
            _isForeground = args.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated;
#else
            _isForeground = args.WindowActivationState != WindowActivationState.Deactivated;
#endif
            if (!_isForeground) _autoOpen.Disarm();
        };

        // After the change has been taken in everywhere, never in the middle of it.
        _remote.State.Jobs.CollectionChanged += (_, _) =>
        {
            if (_autoOpen.IsArmed) DispatcherQueue.TryEnqueue(CheckAutoOpen);
        };
    }

    /// <summary>The row of a job just added: selected, in view and focused, on All Downloads if this view or the search hides it.</summary>
    private void ShowRemoteRow(Guid id)
    {
        if (!_remoteRows.TryGetValue(id, out var row)) return;
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
        if (JobsList.ContainerFromItem(row) is Control item) item.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// The provider's lines and the files each takes. A .torrent file, or a cached magnet, with more than one file
    /// to choose shows them first, one after another, and all of the choosing is done before anything is sent.
    /// Skipping one leaves it out of the add. Any other line takes the add box's word or the Host's rule.
    /// </summary>
    private async Task<List<(AddLinksPick Pick, AddFileChoice? Files)>> ChooseFilesBeforeAddingAsync(AddLinksChoice choice, string provider, List<string> problems)
    {
        var picks = new List<(AddLinksPick, AddFileChoice?)>();
        foreach (var pick in choice.Picks.Where(pick => pick.Line.Route == AddRoute.Provider))
        {
            if (AddChoices.FilesToChoose(pick.Line) is not { } files)
            {
                picks.Add((pick, AddChoices.Default(pick.Line, choice.ChooseLater)));
                continue;
            }

            var name = RemoteText.AddLineWords(pick.Line, provider).Name;
            var answer = await RemoteFileSelectionDialog.AskAsync(
                Content.XamlRoot,
                RootGrid.ActualTheme,
                $"Choose files: {name}",
                provider,
                _ => Task.FromResult(new RemoteFilesToShow(files)),
                primaryText: "Add",
                closeText: "Skip this one");
            if (answer.Choice is { } chosen)
            {
                picks.Add((pick, AddFileChoice.From(pick.Line, chosen)));
                continue;
            }

            pick.LinkPassword = null;
            if (answer.CouldNotOpen) problems.Add($"{name}: wasn't added, because another dialog was open. Add it again.");
        }

        return picks;
    }

    /// <summary>
    /// The end of every add. A clean one says so in passing, and the new rows say the rest. Anything refused or
    /// already there is listed in full, in a message. Either way nothing is left in the status bar after it.
    /// </summary>
    private async Task ReportAddAsync(string title, int downloads, int items, string provider, IReadOnlyList<string> messages, IReadOnlyList<string> problems)
    {
        var added = RemoteText.AddedWords(downloads, items, provider);
        if (messages.Count > 0 || problems.Count > 0)
        {
            await ShowMessageAsync(title, RemoteText.AddReport(added, messages, problems));
            EndPassingStatus();
        }
        else if (added.Length > 0)
        {
            ShowPassingStatus(added);
        }
        else
        {
            EndPassingStatus();
        }
    }

    /// <summary>The package exists once its first job does, so this is the one moment <c>archive.setPassword</c> can take it.</summary>
    private async Task SetPackagePasswordAsync(AddLinksChoice choice, OrdinaryAddResult added, List<string> problems)
    {
        if (choice.ArchivePassword is not { Length: > 0 } password) return;
        var package = added.FirstJobId is { } job ? _groups.FirstOrDefault(group => group.JobIds.Contains(job)) : null;
        if (package is null)
        {
            problems.Add("The archive password wasn't saved, because these links didn't end up in one package.");
            return;
        }

        var result = await _remote.SetArchivePasswordAsync(new ArchiveSetPasswordRequest { PackageId = package.Id, Password = password });
        if (!result.Ok) problems.Add($"The archive password wasn't saved: {RemoteText.Failure(result.Error)}");
    }

}
