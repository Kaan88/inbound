using System.Collections.Specialized;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Adding through a provider: which way each input goes, and what happens after the add dialog closes.
/// <c>remote.add</c> only saves the intent, and the Host finds out whether something is cached after
/// that, so the answer and the approval it may need come in a second step that watches the jobs.
/// </summary>
public sealed partial class MainWindow
{
    private enum AddressWay
    {
        Cancelled,
        /// <summary>Today's Add URL dialog: an ordinary link, or the user's explicit choice for one nobody could classify.</summary>
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

        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return ContentDialogResult.None;
        }
    }

    private string ProviderWordForAdding()
    {
        var accounts = _remote.State.Accounts;
        var account = accounts.FirstOrDefault(RemoteRules.HasUsableKey) ?? accounts.FirstOrDefault();
        return account is null ? "the provider" : RemoteText.ProviderName(account);
    }

    /// <summary>
    /// Add URL's first step when a provider could take the address: stays open until the Host has said
    /// what it is. Something it can't classify stays here too, with a retry and an explicit way past.
    /// </summary>
    private async Task<CheckedAddress> CheckAddressAsync(ContentDialog dialog, ComboBox urlBox, StatusLine error, StackPanel panel)
    {
        var provider = ProviderWordForAdding();
        urlBox.PlaceholderText = "https://example.com/file.zip, or a magnet";
        var busy = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(busy, "Checking this address");
        var status = new StatusLine("Address status");
        var direct = new CheckBox { Content = $"Download directly, not through {provider}", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(direct, $"Download directly, not through {provider}");
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { busy, status.Text } });
        panel.Children.Add(direct);
        status.Text.MaxWidth = 440;

        using var closing = new CancellationTokenSource();
        var outcome = new CheckedAddress(AddressWay.Cancelled, string.Empty, null);
        string? checkedText = null;
        RemoteClassifyResult? checkedResult = null;
        var running = false;

        void ShowCantTell(string words)
        {
            var web = LinkExtractor.IsWebLink(urlBox.Text.Trim());
            status.Show(web ? $"{words} So it isn't treated as an ordinary link unless you say so." : words, isProblem: true);
            dialog.PrimaryButtonText = "Try again";
            direct.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        }

        direct.Click += (_, _) => dialog.PrimaryButtonText = direct.IsChecked == true ? "Next" : "Try again";

        async Task CheckAsync(string text)
        {
            running = true;
            busy.IsActive = true;
            busy.Visibility = Visibility.Visible;
            error.Show(string.Empty);
            status.Show("Checking this address…");
            direct.Visibility = Visibility.Collapsed;
            direct.IsChecked = false;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var result = await _remote.ClassifyAsync(text, closing.Token);
                checkedText = text;
                checkedResult = result;
                var name = string.IsNullOrWhiteSpace(result.ProviderName) ? provider : result.ProviderName;
                if (!result.Ok)
                {
                    ShowCantTell(RemoteText.Failure(result.Error));
                    return;
                }

                switch (result.Kind)
                {
                    case "ordinaryLink" or "providerDirectLink":
                        outcome = new CheckedAddress(AddressWay.Direct, text, result);
                        dialog.Hide();
                        break;
                    case "folderLink" or "magnet" or "torrentFile" or "hosterLink":
                        outcome = new CheckedAddress(AddressWay.Provider, text, result);
                        dialog.Hide();
                        break;
                    case "undetermined":
                        ShowCantTell(RemoteText.Failure(result.Reason, null, name));
                        break;
                    default:
                        status.Show(string.IsNullOrWhiteSpace(result.Reason) ? "This app can't use this." : UrlRedactor.HideAddresses(result.Reason), isProblem: true);
                        dialog.PrimaryButtonText = "Next";
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // The dialog closed first, and there is nothing left to show it in.
            }
            finally
            {
                running = false;
                if (!closing.IsCancellationRequested)
                {
                    busy.IsActive = false;
                    busy.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = true;
                }
            }
        }

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // Kept open: only an answer from the Host decides where this goes.
            args.Cancel = true;
            if (running) return;
            var text = urlBox.Text.Trim();
            if (direct.Visibility == Visibility.Visible && direct.IsChecked == true && text == checkedText)
            {
                outcome = new CheckedAddress(AddressWay.Direct, text, checkedResult);
                dialog.Hide();
                return;
            }

            if (!LinkExtractor.IsWebLink(text) && !LinkExtractor.IsMagnet(text) && !LinkExtractor.IsTorrentPath(text))
            {
                error.Say("Enter an http or https address, a magnet, or the full path of a .torrent file.", isProblem: true);
                return;
            }

            _ = CheckAsync(text);
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open over this window.
        }

        closing.Cancel();
        return outcome;
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
            _remote.OpenClassifier,
            link => FindExistingJob(link) is not null,
            action => DispatcherQueue.TryEnqueue(() => action())))
        {
            if (inputs is [var only] && known is not null) session.Seed(only, known);
            else if (inputs is not null) session.SetInputs(inputs);
            foreach (var (input, result) in answered ?? []) session.Seed(input, result);

            var initial = focus is AddFocus.Links or AddFocus.Magnet ? await ClipboardInputsAsync(focus) : null;
            var dialog = new AddLinksDialog(
                session,
                focus,
                _settings,
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
        IReadOnlyList<Windows.Storage.StorageFile> files;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add(".torrent");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            files = await picker.PickMultipleFilesAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Import torrent file", "The file picker couldn't open.");
            return;
        }

        if (files.Count > 0) await ShowProviderAddAsync(AddFocus.TorrentFiles, files.Select(file => file.Path).ToList());
    }

    /// <summary>
    /// A picker, then the import dialog, then what became of each entry. The file's path only ever goes to the
    /// Host: nothing here looks at it, because even asking about a file on a network path sends this Windows
    /// account's credentials there. The Host refuses those, and the dialog says so.
    /// </summary>
    private async Task ImportJdContainerAsync()
    {
        if (_settings is null) return;
        string path;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            foreach (var type in ContainerFiles.Types) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is not { } file) return;
            path = file.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Import JD Container", "The file picker couldn't open.");
            return;
        }

        // A file from a cloud or phone location has no path a program could read.
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowMessageAsync("Import JD Container", ContainerFiles.NoPathWords);
            return;
        }

        var provider = ProviderWordForAdding();
        var dialog = new ContainerImportDialog(_remote, _settings, provider, path, RootGrid, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var answer = await dialog.ShowAsync(Content.XamlRoot, RootGrid.ActualTheme);
        if (answer.CouldNotOpen)
        {
            ShowPassingStatus("Import JD Container couldn't open, because another dialog is open.");
            return;
        }

        if (answer.Stopped)
        {
            await _remote.ListAsync();
            ShowPassingStatus(ContainerFiles.StoppedWords);
        }

        if (answer.Outcomes is not { } outcomes) return;
        if (outcomes.AddedCount > 0 && answer.Folder is { } folder) RememberFolder(folder);
        await ContainerImportDialog.ShowOutcomesAsync(Content.XamlRoot, RootGrid.ActualTheme, _remote, outcomes, provider, RootGrid, LocalStatusOf);
    }

    private sealed record RemoteAdded(string Name, RemoteAddResultDto Result);

    private async Task RunAddChoiceAsync(AddLinksChoice choice)
    {
        var provider = ProviderWordForAdding();
        var problems = new List<string>();
        var messages = new List<string>();

        // Ordinary links first, the same way as always.
        var direct = choice.Picks.Where(pick => pick.Line.Route == AddRoute.Direct).Select(pick => pick.Line.Input).ToList();
        if (direct.Count > 0)
        {
            var added = await AddOrdinaryLinksAsync(direct, choice.KeepPackage, choice.Destination, choice.StartLater ? "later" : "now", choice.QueueId, choice.ConflictIndex);
            await RefreshAsync();
            messages.Add(added.Summary);
            await SetPackagePasswordAsync(choice, added, problems);
        }

        choice.ArchivePassword = null;

        var folderLinks = 0;
        foreach (var pick in choice.Picks.Where(pick => pick.Line.Route == AddRoute.FolderLink))
        {
            if (await AddFolderLinkAsync(pick.Line, choice, problems)) folderLinks++;
        }

        var sent = new List<RemoteAdded>();
        var providerPicks = choice.Picks.Where(pick => pick.Line.Route == AddRoute.Provider).ToList();
        if (providerPicks.Count > 0) StatusText.Text = $"Adding to {provider}…";
        foreach (var pick in providerPicks)
        {
            var name = RemoteText.AddLineWords(pick.Line, provider).Name;
            var result = await _remote.AddAsync(new RemoteAddRequestDto
            {
                Input = pick.Line.Input,
                Destination = choice.Destination,
                QueueId = choice.QueueId,
                StartMode = choice.StartLater ? "later" : "startNow",
                LinkPassword = pick.LinkPassword,
            });
            pick.LinkPassword = null;

            // A refusal is the end of it for this link. Nothing here tries it any other way.
            if (!result.Ok) problems.Add($"{name}: {RemoteText.Failure(result.Error, null, provider)}");
            else if (result.Job is not null) sent.Add(new RemoteAdded(name, result));
        }

        if (sent.Count > 0) await ShowAddOutcomesAsync(sent, provider);

        var addedCount = sent.Count + folderLinks;
        var addedWords = addedCount == 1 ? $"Added 1 item for {provider}." : $"Added {addedCount} items for {provider}.";
        if (addedCount > 0 && messages.Count == 0 && problems.Count == 0)
        {
            ShowPassingStatus(addedWords);
            return;
        }

        if (addedCount > 0) messages.Add(addedWords);
        messages.AddRange(problems.Take(5));
        if (messages.Count > 0) await ShowMessageAsync("Add links", string.Join(Environment.NewLine, messages));
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

    /// <summary>A folder link refers to an item already on the account, so its files can be chosen before anything is added.</summary>
    private async Task<bool> AddFolderLinkAsync(AddLine line, AddLinksChoice choice, List<string> problems)
    {
        if (line.Result is not { Provider: { } providerId, ItemKind: { } kind, ItemId: { } itemId } found) return false;
        var provider = string.IsNullOrWhiteSpace(found.ProviderName) ? ProviderWordForAdding() : found.ProviderName;
        var name = RemoteText.AddLineWords(line, provider).Name;
        var answer = await RemoteFileSelectionDialog.AskAsync(
            Content.XamlRoot,
            RootGrid.ActualTheme,
            $"Choose files: {name}",
            provider,
            async token =>
            {
                var result = await _remote.GetItemsAsync(new RemoteItemRequest { Provider = providerId, Kind = kind, ItemId = itemId }, token);
                if (!result.Ok) return new RemoteFilesToShow([], Error: RemoteText.Failure(result.Error, result.FailureKind, provider));
                if (result.Items is not [var item]) return new RemoteFilesToShow([], Error: $"{provider} has no such item on your account.");
                return new RemoteFilesToShow(item.Files ?? [], item.Stage != "ready", Title: string.IsNullOrWhiteSpace(item.Name) ? null : $"Choose files: {UrlRedactor.HideAddresses(item.Name)}");
            },
            primaryText: "Add",
            closeText: "Skip this link",
            laterText: "Add, choose later");
        if (answer.CouldNotOpen)
        {
            problems.Add($"{name}: wasn't added, because another dialog was open. Add this link again.");
            return false;
        }

        if (answer.Choice is null && !answer.Later) return false;

        var added = await _remote.AddAsync(new RemoteAddRequestDto
        {
            Provider = providerId,
            ItemKind = kind,
            ItemId = itemId,
            Destination = choice.Destination,
            QueueId = choice.QueueId,
            StartMode = choice.StartLater ? "later" : "startNow",
            Selection = answer.Later ? "ask" : answer.Choice!.All ? "all" : "chosen",
            FileIds = answer.Later || answer.Choice!.All ? null : answer.Choice.FileIds.ToList(),
        });
        if (!added.Ok) problems.Add($"{name}: {RemoteText.Failure(added.Error, null, provider)}");
        else if (!string.IsNullOrWhiteSpace(added.Note)) problems.Add($"{name}: {UrlRedactor.HideAddresses(added.Note)}");
        return added.Ok;
    }

    /// <summary>
    /// What the provider says about what was just added, live, with the approval anything uncached needs
    /// before work starts on it. Closing this loses nothing: the same jobs are on the main list.
    /// </summary>
    private async Task ShowAddOutcomesAsync(IReadOnlyList<RemoteAdded> added, string provider)
    {
        var state = _remote.State;
        var rows = added.Select(item => new OutcomeRow(item.Name, item.Result, provider, RootGrid, LocalStatusOf)).ToList();
        var explanation = new TextBlock { Text = RemoteText.ApproveExplanation(provider), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(explanation, explanation.Text);
        var approveAll = new CheckBox { Content = "Approve all", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(approveAll, "Approve all of these");
        var status = new StatusLine("Add status");
        var list = new StackPanel { Spacing = 10 };
        foreach (var row in rows) list.Children.Add(row.Root);
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 560,
            Children = { status.Text, explanation, approveAll, new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Added for {provider}",
            Content = panel,
            PrimaryButtonText = "Approve selected",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;

        void Refresh()
        {
            foreach (var row in rows) row.Update(state);
            var waiting = rows.Where(row => row.View.NeedsApproval).ToList();
            status.Show(RemoteText.AddOutcomeSummary(rows.Select(row => row.View).ToList(), provider));
            explanation.Visibility = waiting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            approveAll.Visibility = waiting.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
            approveAll.IsChecked = waiting.Count > 0 && waiting.All(row => row.Approved);
            var approving = waiting.Any(row => row.Approved);
            var withPassword = rows.Any(row => row.HasPasswordTyped);
            dialog.IsPrimaryButtonEnabled = approving || withPassword;
            // Enter takes the default button, so it only means the action while the action can run.
            dialog.DefaultButton = dialog.IsPrimaryButtonEnabled ? ContentDialogButton.Primary : ContentDialogButton.Close;
            dialog.PrimaryButtonText = withPassword ? approving ? "Approve and retry" : "Retry with this password" : "Approve selected";
        }

        NotifyCollectionChangedEventHandler onJobs = (_, _) => Refresh();
        approveAll.Click += (_, _) =>
        {
            foreach (var row in rows.Where(row => row.View.NeedsApproval)) row.Approved = approveAll.IsChecked == true;
            Refresh();
        };
        foreach (var row in rows) row.Changed += Refresh;

        var sending = false;
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // Stays open: the rows follow what happens next, and the buttons stay honest about it.
            args.Cancel = true;
            if (sending) return;
            sending = true;
            dialog.IsPrimaryButtonEnabled = false;
            _ = SendAsync();

            async Task SendAsync()
            {
                foreach (var row in rows)
                {
                    var approve = row.View.NeedsApproval && row.Approved;
                    var password = row.View.NeedsLinkPassword ? row.TakePassword() : null;
                    if (!approve && password is null) continue;
                    var result = await _remote.RetryAsync(new RemoteRetryRequest { JobId = row.JobId, ApproveUncached = approve ? true : null, LinkPassword = password });
                    password = null;
                    row.ShowProblem(result.Ok ? null : RemoteText.Failure(result.Error, null, provider));
                }

                sending = false;
                Refresh();
            }
        };

        // The local files behind an item finish without any word about the item, so its words are looked at again.
        var localWatch = DispatcherQueue.CreateTimer();
        localWatch.Interval = TimeSpan.FromSeconds(1);
        localWatch.Tick += (_, _) => Refresh();
        localWatch.Start();
        state.Jobs.CollectionChanged += onJobs;
        Refresh();
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open over this window. These are on the main list all the same.
        }
        finally
        {
            localWatch.Stop();
            state.Jobs.CollectionChanged -= onJobs;
            foreach (var row in rows) row.ClearPassword();
        }
    }

    /// <summary>One item in the second step: its words, an approval, and a password when the site turns out to want one.</summary>
    private sealed class OutcomeRow
    {
        private readonly string _name;
        private readonly string? _note;
        private readonly string _provider;
        private readonly FrameworkElement _themeRoot;
        private readonly Func<RemoteJobDto, string?> _localStatus;
        private readonly TextBlock _nameText = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _word = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _approve;
        private readonly PasswordBox _password;
        private readonly StatusLine _problem = new("Problem");
        private string? _problemWords;

        public OutcomeRow(string name, RemoteAddResultDto result, string provider, FrameworkElement themeRoot, Func<RemoteJobDto, string?> localStatus)
        {
            _localStatus = localStatus;
            _name = name;
            _note = result.Note;
            _provider = provider;
            _themeRoot = themeRoot;
            JobId = result.Job!.Id;
            _nameText.Text = name;
            _approve = new CheckBox { Content = $"Let {provider} fetch this, using your account's quota", Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_approve, $"Let {provider} fetch {name}, using your account's quota");
            _approve.Click += (_, _) => Changed?.Invoke();
            _password = new PasswordBox
            {
                Header = "The password the site set for this link. It isn't an archive password.",
                PasswordRevealMode = PasswordRevealMode.Hidden,
                Visibility = Visibility.Collapsed,
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(_password, $"Link password for {name}");
            _password.PasswordChanged += (_, _) => Changed?.Invoke();
            Root = new StackPanel { Spacing = 2, Children = { _nameText, _word, _detail, _approve, _password, _problem.Text } };
            View = RemoteText.AddOutcome(result.Job, name, _note, DateTimeOffset.Now);
        }

        public event Action? Changed;

        public Guid JobId { get; }
        public StackPanel Root { get; }
        public AddOutcomeView View { get; private set; }

        public bool Approved
        {
            get => _approve.IsChecked == true;
            set => _approve.IsChecked = value;
        }

        public bool HasPasswordTyped => View.NeedsLinkPassword && _password.Password.Length > 0;

        public string? TakePassword()
        {
            var password = _password.Password;
            _password.Password = string.Empty;
            return password.Length > 0 ? password : null;
        }

        public void ClearPassword() => _password.Password = string.Empty;

        public void ShowProblem(string? words) => _problemWords = words;

        public void Update(RemoteState state)
        {
            var job = state.Jobs.FirstOrDefault(candidate => candidate.Id == JobId);
            View = RemoteText.AddOutcome(job, _name, _note, DateTimeOffset.Now, localStatus: job is null ? null : _localStatus(job));
            _word.Text = View.Word;
            _detail.Text = View.Detail;
            _detail.Visibility = View.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detail.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot);
            if (View.IsProblem) _word.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _themeRoot);
            else _word.ClearValue(TextBlock.ForegroundProperty);
            AutomationProperties.SetName(_nameText, View.AccessibleName);
            _approve.Visibility = View.NeedsApproval ? Visibility.Visible : Visibility.Collapsed;
            if (!View.NeedsApproval) _approve.IsChecked = false;
            _password.Visibility = View.NeedsLinkPassword ? Visibility.Visible : Visibility.Collapsed;
            if (!View.NeedsLinkPassword) _password.Password = string.Empty;
            _problem.Show(_problemWords ?? string.Empty, isProblem: true);
        }
    }
}
