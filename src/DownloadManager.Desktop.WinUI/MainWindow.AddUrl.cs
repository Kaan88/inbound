using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Add URL (UI.R38): one dialog. The address is at the top; once it stops changing, the dialog works out what it is by
/// itself and fills in the file's name, size, folder and category below, with Start download, Download later and Cancel
/// at the bottom. Several links, a numbered address, a torrent, or a link a provider handles go on to the dialog made
/// for them, which Continue opens.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>What closed Add URL.</summary>
    private enum AddUrlChoice
    {
        None,
        Start,
        Later,
        /// <summary>Continue: another dialog takes what was entered.</summary>
        Handoff,
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        await RefreshSettingsAsync();
        var providerAdd = await ProviderAddAppliesAsync();
        var torrentInputs = providerAdd || LocalTorrentsApply || _remote.State.TorrentsOffered;
        var provider = providerAdd ? ProviderWordForAdding() : string.Empty;

        // ---- The address ----

        var urlBox = new ComboBox
        {
            Header = "URL",
            PlaceholderText = providerAdd || LocalTorrentsApply ? "https://example.com/file.zip, or a magnet" : "https://example.com/file.zip",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // U-WL-21: an editable box draws its placeholder in the text color, so it read as a typed address.
        DimPlaceholder(urlBox, RootGrid.ActualTheme);
        foreach (var recent in _settings.RecentUrls.Take(20))
        {
            urlBox.Items.Add(recent);
        }
        // UI.R18: a very long link would make the list as wide as the screen. Cut it short; the tooltip has it all.
        urlBox.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock Text='{Binding}' MaxWidth='560' TextTrimming='CharacterEllipsis' ToolTipService.ToolTip='{Binding}' /></DataTemplate>");
        // With no recent addresses the list would open as an empty strip over the dialog.
        urlBox.DropDownOpened += (_, _) => { if (urlBox.Items.Count == 0) urlBox.IsDropDownOpen = false; };
        // Like other download managers, start from a link already on the clipboard.
        try
        {
            var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = (await clip.GetTextAsync()).Trim();
                if (text.Length < 4096 && Uri.TryCreate(text, UriKind.Absolute, out var clipUri) &&
                    (clipUri.Scheme == Uri.UriSchemeHttp || clipUri.Scheme == Uri.UriSchemeHttps ||
                     ((providerAdd || LocalTorrentsApply) && LinkExtractor.IsMagnet(text))))
                    urlBox.Text = text;
            }
        }
        catch (Exception)
        {
            // Clipboard access is best-effort.
        }

        var addressBusy = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(addressBusy, "Checking this address");
        var addressStatus = new StatusLine("Address status");
        var direct = new CheckBox { Content = $"Download directly, not through {provider}", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(direct, $"Download directly, not through {provider}");

        // ---- The download: filled in once the address is a link this dialog adds ----

        var url = string.Empty;
        RemoteClassifyResult? addressResult = null;
        string? evaluated = null;           // the box's text that what follows is about
        string? addRefusal = null;          // U-WR-2: the Host said it won't add this address for this PC (another device's rules), so Start would only fail
        var ready = false;                  // a link this dialog adds: Start and Download later go
        Func<Task>? handoff = null;         // what Continue opens
        var cantTell = false;               // nobody could say what it is: Try again, or tick the direct box
        (string Link, RemoteClassifyResult? Result) cantTellLink = (string.Empty, null);
        string? problem = null;             // why there is nothing to start, said again when Start is pressed
        var checkingAddress = false;
        var checkGeneration = 0;
        CancellationTokenSource? checkCancellation = null;
        CancellationTokenSource? waiting = null;
        AddUrlChoice? goWhenReady = null;   // Start, Enter or Download later was pressed before the Host answered
        var closed = false;
        var creatingCategory = false;
        ContentDialog? addDialog = null;
        var choice = AddUrlChoice.None;
        var noteText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var nameBox = new TextBox
        {
            Header = "File name (optional)",
            PlaceholderText = "Resolved from the URL or server",
        };
        var destinationBox = new TextBox
        {
            Text = _settings.DefaultDownloadDirectory,
        };
        var browseDestination = new Button
        {
            Content = "Browse…",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        AutomationProperties.SetName(destinationBox, "Destination folder");
        var destinationPickerError = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        browseDestination.Click += async (_, _) =>
        {
            browseDestination.IsEnabled = false;
            destinationPickerError.Text = string.Empty;
            destinationPickerError.Visibility = Visibility.Collapsed;
            try
            {
                if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this)) is { } folder)
                    destinationBox.Text = folder;
            }
            catch (Exception ex)
            {
                destinationPickerError.Text = $"Could not open the folder picker: {ex.Message}";
                destinationPickerError.Visibility = Visibility.Visible;
            }
            finally
            {
                browseDestination.IsEnabled = true;
            }
        };
        var destinationRow = new Grid { ColumnSpacing = 8 };
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browseDestination, 1);
        destinationRow.Children.Add(destinationBox);
        destinationRow.Children.Add(browseDestination);
        long? knownSize = null;
        var destinationField = FolderChooser.Attach(destinationRow, destinationBox, () => knownSize, out var refreshSpace);
        var descriptionBox = new TextBox { Header = "Description (optional)" };
        var categoryBox = new ComboBox { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryBox.Items.Add(new ComboBoxItem { Content = "Automatic (file extension)" });
        foreach (var category in FileCategories.Names.Append("other"))
            categoryBox.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(category[0]) + category[1..], Tag = category });
        foreach (var category in _settings.CustomCategories)
            categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
        categoryBox.SelectedIndex = 0;
        var changingDestination = false;
        var customDestination = false;
        void UseCategoryFolder(bool force = false)
        {
            if (customDestination && !force) return;
            var fileName = !string.IsNullOrWhiteSpace(nameBox.Text) ? nameBox.Text
                : Uri.TryCreate(url, UriKind.Absolute, out var address) ? Uri.UnescapeDataString(address.AbsolutePath)
                : string.Empty;
            var category = (categoryBox.SelectedItem as ComboBoxItem)?.Tag as string
                ?? FileCategories.For(fileName, _settings.CustomCategories);
            var folder = _settings.UseCategoryFolders ? _settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.DestinationDirectory : null;
            if (_settings.UseCategoryFolders && string.IsNullOrWhiteSpace(folder)) _settings.CategoryFolders.TryGetValue(category, out folder);
            changingDestination = true;
            destinationBox.Text = string.IsNullOrWhiteSpace(folder) ? _settings.DefaultDownloadDirectory : folder;
            changingDestination = false;
            customDestination = false;
        }
        destinationBox.TextChanged += (_, _) => { if (!changingDestination) customDestination = true; };
        nameBox.TextChanged += (_, _) => UseCategoryFolder();
        categoryBox.SelectionChanged += (_, _) => UseCategoryFolder(force: true);
        UseCategoryFolder();
        var categoryName = new TextBox { Header = "New category name" };
        var categoryExtensions = new TextBox { Header = "File extensions (optional)", PlaceholderText = "zip 7z rar" };
        var categoryError = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var createCategory = new Button { Content = "Create category" };
        createCategory.Click += async (_, _) =>
        {
            createCategory.IsEnabled = false;
            creatingCategory = true;
            UpdateButtons();
            categoryError.Visibility = Visibility.Collapsed;
            try
            {
                var current = await _connection.RefreshAsync();
                var updated = CloneSettings(current.Settings);
                var category = new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = categoryName.Text.Trim(),
                    DestinationDirectory = destinationBox.Text.Trim(),
                    Extensions = categoryExtensions.Text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList() };
                updated.CustomCategories.Add(category);
                var reply = await _connection.UpdateSettingsAsync(updated);
                var result = reply.GetPayload<SettingsResult>();
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Could not create category.");
                _settings = result.Settings;
                UpdateLimitButtons();
                RefreshCustomCategories();
                var option = new ComboBoxItem { Content = category.Name, Tag = category.Id };
                categoryBox.Items.Add(option);
                categoryBox.SelectedItem = option;
                categoryName.Text = categoryExtensions.Text = string.Empty;
            }
            catch (Exception ex)
            {
                categoryError.Text = ex.Message;
                categoryError.Visibility = Visibility.Visible;
            }
            finally
            {
                createCategory.IsEnabled = true;
                creatingCategory = false;
                UpdateButtons();
            }
        };
        var categoryPanel = new StackPanel { Spacing = 8 };
        categoryPanel.Children.Add(categoryName);
        categoryPanel.Children.Add(categoryExtensions);
        categoryPanel.Children.Add(new TextBlock { Text = "Create a saved category using the destination folder above. Matching extensions apply to future downloads; leave blank for manual selection. Creating it saves immediately, even if this download is cancelled.", TextWrapping = TextWrapping.Wrap });
        categoryPanel.Children.Add(createCategory);
        categoryPanel.Children.Add(categoryError);
        var useAuthentication = new CheckBox { Content = "Use a user name and password for this download" };
        var authenticationUser = new TextBox { Header = "User name", IsEnabled = false };
        var authenticationPassword = new PasswordBox { Header = "Password", IsEnabled = false };
        useAuthentication.Checked += (_, _) => authenticationUser.IsEnabled = authenticationPassword.IsEnabled = true;
        useAuthentication.Unchecked += (_, _) => authenticationUser.IsEnabled = authenticationPassword.IsEnabled = false;
        var authenticationPanel = new StackPanel { Spacing = 8 };
        authenticationPanel.Children.Add(useAuthentication);
        authenticationPanel.Children.Add(authenticationUser);
        authenticationPanel.Children.Add(authenticationPassword);
        authenticationPanel.Children.Add(new TextBlock { Text = $"HTTP Basic authentication. Credentials apply only to this download and are {PlatformText.ProtectedBy} when saved.", TextWrapping = TextWrapping.Wrap });
        var authenticationError = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        authenticationPanel.Children.Add(authenticationError);
        var authenticationExpander = new Expander { Header = "Authentication (optional)", Content = authenticationPanel, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Review 5:A4b: the words only; whether it starts ticked is the owner's call.
        var rememberFolder = new CheckBox { Content = "Use this folder next time too", IsChecked = true };
        categoryBox.SelectionChanged += (_, _) => rememberFolder.Content =
            (categoryBox.SelectedItem as ComboBoxItem)?.Tag is string
                ? "Use this folder for this category next time too" : "Use this folder next time too";

        var queueBox = new ComboBox { Header = "Queue" };
        foreach (var queue in _settings.Queues.OrderBy(q => q.Order))
        {
            queueBox.Items.Add(queue.Name);
        }

        var defaultQueueIndex = _settings.Queues
            .OrderBy(q => q.Order)
            .ToList()
            .FindIndex(q => q.Id == _settings.DefaultQueueId);
        queueBox.SelectedIndex = Math.Max(0, defaultQueueIndex);

        var conflictBox = new ComboBox { Header = "If the file exists" };
        conflictBox.Items.Add("Rename the new file");
        conflictBox.Items.Add("Overwrite the existing file");
        conflictBox.Items.Add("Fail the new download");
        conflictBox.SelectedIndex = 0;

        // ---- The file check ----

        var metadataText = new TextBlock { Text = AddUrlRules.EmptyInfoWords, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(metadataText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var metadataBusy = new ProgressRing { IsActive = false, Visibility = Visibility.Collapsed, Width = 18, Height = 18 };
        AutomationProperties.SetName(metadataBusy, "Checking the file");
        var checkMetadata = new Button { Content = AddRules.CheckButtonText(checkedBefore: true) };
        var metadataActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        metadataActions.Children.Add(checkMetadata);
        metadataActions.Children.Add(metadataBusy);
        var metadataGeneration = 0;
        var metadataClosed = false;
        var nameEdited = false;
        var assigningServerName = false;
        CancellationTokenSource? metadataCancellation = null;
        nameBox.TextChanged += (_, _) => { if (!assigningServerName) nameEdited = true; };

        void StopMetadata()
        {
            metadataGeneration++;
            metadataCancellation?.Cancel();
            metadataBusy.IsActive = false;
            metadataBusy.Visibility = Visibility.Collapsed;
            checkMetadata.IsEnabled = true;
        }

        // Review of 0.7.2, security-1: the site the user name and password were typed for. An address on another site
        // clears them, and the file check that runs by itself never sends them; Check again and Start do.
        string? credentialsSite = null;
        var credentialsDrops = 0;
        var dropsWhenPressed = 0;

        async Task PreviewMetadataAsync(bool automatic = false)
        {
            if (url.Length == 0) return;
            if (automatic && useAuthentication.IsChecked == true)
            {
                StopMetadata();
                metadataText.ClearValue(TextBlock.ForegroundProperty);
                metadataText.Text = AddUrlRules.CheckWithCredentialsWords;
                return;
            }
            metadataCancellation?.Cancel();
            var generation = ++metadataGeneration;
            var checking = url;
            using var cancellation = new CancellationTokenSource();
            metadataCancellation = cancellation;
            metadataBusy.IsActive = true;
            metadataBusy.Visibility = Visibility.Visible;
            checkMetadata.IsEnabled = false;
            metadataText.ClearValue(TextBlock.ForegroundProperty);
            metadataText.Text = "Checking the server… You can start without waiting.";
            try
            {
                var request = new FileMetadataRequest { Url = checking };
                if (useAuthentication.IsChecked == true)
                {
                    if (string.IsNullOrWhiteSpace(authenticationUser.Text) || authenticationUser.Text.IndexOfAny([':', '\r', '\n']) >= 0)
                        throw new ArgumentException("Enter a user name without colons or line breaks, then check again.");
                    credentialsSite ??= AddUrlRules.SiteOf(checking);
                    request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                        $"{authenticationUser.Text}:{authenticationPassword.Password}"));
                }
                var metadata = await _connection.ReadFileMetadataAsync(request, cancellation.Token);
                if (metadataClosed || generation != metadataGeneration) return;
                // The Host answered: its refusal holds only while it still gives one, so a later answer without one turns
                // Start and Download later back on.
                addRefusal = AddRules.RefusalAfter(addRefusal, answered: true, refused: !metadata.Ok && metadata.AddRefused, metadata.Error);
                UpdateButtons();
                if (addRefusal is not null)
                {
                    metadataText.Text = AddRules.RefusedText(addRefusal, _connection.Target);
                    metadataText.Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid);
                    return;
                }
                if (!metadata.Ok) throw new InvalidOperationException(metadata.Error ?? "File information is unavailable.");
                // UI.R31: the list's size format; the exact count is in the tooltip.
                var size = metadata.TotalBytes is { } bytes ? UiFormat.Bytes(bytes) : "Size unknown";
                // Review 5:A2a: the type is for those who want it, beside the exact count.
                var type = metadata.ContentType ?? "Type unknown";
                ToolTipService.SetToolTip(metadataText, metadata.TotalBytes is { } exact ? $"{exact:N0} bytes · {type}" : type);
                knownSize = metadata.TotalBytes;
                refreshSpace();
                var serverName = RemoteText.ServerNameToShow(metadata.FileName, checking, addressResult);
                metadataText.Text = AddRules.FileInfoText(size, serverName, nameEdited ? nameBox.Text : serverName);
                if (!nameEdited && serverName is not null)
                {
                    assigningServerName = true;
                    nameBox.Text = serverName;
                    assigningServerName = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (!metadataClosed && generation == metadataGeneration)
                    metadataText.Text = "File information request timed out. You can still start the download.";
            }
            catch (Exception ex)
            {
                if (!metadataClosed && generation == metadataGeneration)
                    metadataText.Text = $"File information unavailable: {ex.Message} You can still start the download.";
            }
            finally
            {
                if (ReferenceEquals(metadataCancellation, cancellation)) metadataCancellation = null;
                if (!metadataClosed && generation == metadataGeneration)
                {
                    metadataBusy.IsActive = false;
                    metadataBusy.Visibility = Visibility.Collapsed;
                    checkMetadata.IsEnabled = true;
                }
            }
        }
        void MetadataCredentialsChanged()
        {
            if (useAuthentication.IsChecked != true) credentialsSite = null;
            else if (url.Length > 0) credentialsSite = AddUrlRules.SiteOf(url);
            if (url.Length == 0) return;
            StopMetadata();
            metadataText.Text = "Credentials changed. Check again to refresh the file info.";
        }
        checkMetadata.Click += async (_, _) => await PreviewMetadataAsync();
        authenticationUser.TextChanged += (_, _) => MetadataCredentialsChanged();
        authenticationPassword.PasswordChanged += (_, _) => MetadataCredentialsChanged();
        useAuthentication.Checked += (_, _) => MetadataCredentialsChanged();
        useAuthentication.Unchecked += (_, _) => MetadataCredentialsChanged();

        // ---- The dialog ----

        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        // Why the last Start didn't add it: shown here, with everything typed kept, rather than after the dialog closed.
        var addProblem = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        AutomationProperties.SetLiveSetting(addProblem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        panel.Children.Add(addProblem);
        if (!providerAdd) AddUnreadableKeyNotice(panel);
        panel.Children.Add(urlBox);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addressBusy, addressStatus.Text } });
        addressStatus.Text.MaxWidth = 440;
        panel.Children.Add(direct);
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(noteText);
        details.Children.Add(metadataText);
        details.Children.Add(metadataActions);
        details.Children.Add(nameBox);
        details.Children.Add(categoryBox);
        details.Children.Add(new TextBlock { Text = "Destination folder" });
        details.Children.Add(destinationField);
        details.Children.Add(destinationPickerError);
        details.Children.Add(rememberFolder);
        details.Children.Add(new Expander { Header = "Create a category", Content = categoryPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        details.Children.Add(descriptionBox);
        details.Children.Add(authenticationExpander);
        var advanced = new StackPanel { Spacing = 10 };
        advanced.Children.Add(queueBox);
        advanced.Children.Add(conflictBox);
        details.Children.Add(new Expander { Header = "Queue and file conflicts", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(details);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = ServerNames.ForWindow("Add download", _connection.Target),
            Content = new ScrollViewer { Padding = InboundTheme.DialogScrollPadding, Content = panel, MaxHeight = Math.Max(240, Math.Min(560, RootGrid.ActualHeight - 200)) },
            PrimaryButtonText = AddUrlRules.StartText,
            SecondaryButtonText = AddUrlRules.LaterText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        addDialog = dialog;

        // ---- What the address is, and what the buttons do with it ----


        string CurrentText() => (FindDescendant<TextBox>(urlBox, "EditableText")?.Text ?? urlBox.Text ?? string.Empty).Trim();

        // Puts text in the box that is already worked out, so it isn't checked again; editing it checks it as usual.
        void ShowInBox(string text, string header)
        {
            evaluated = text.Trim();
            urlBox.Header = header;
            if (FindDescendant<TextBox>(urlBox, "EditableText") is { } inner) inner.Text = text;
            else urlBox.Text = text;
        }

        void UpdateButtons()
        {
            if (addDialog is not { } buttons) return;
            buttons.PrimaryButtonText = handoff is not null ? AddUrlRules.ContinueText : cantTell && direct.IsChecked != true ? "Try again" : AddUrlRules.StartText;
            // Download later has nothing to do while another dialog takes the address, so it goes rather than greys out.
            buttons.SecondaryButtonText = handoff is not null ? string.Empty : AddUrlRules.LaterText;
            var busy = creatingCategory;
            buttons.IsPrimaryButtonEnabled = !busy && (handoff is not null || (addRefusal is null && CurrentText().Length > 0));
            buttons.IsSecondaryButtonEnabled = !busy && handoff is null && ready && addRefusal is null;
        }

        void Reset()
        {
            ready = false;
            handoff = null;
            cantTell = false;
            problem = null;
            direct.Visibility = Visibility.Collapsed;
            direct.IsChecked = false;
            details.Visibility = Visibility.Visible;
        }

        void ShowNoLink(string? words)
        {
            Reset();
            problem = words;
            addressStatus.Show(words ?? string.Empty, isProblem: words is not null);
            if (url.Length > 0)
            {
                url = string.Empty;
                addressResult = null;
                StopMetadata();
            }
            metadataActions.Visibility = Visibility.Collapsed;
            noteText.Visibility = Visibility.Collapsed;
            metadataText.ClearValue(TextBlock.ForegroundProperty);
            metadataText.Text = AddUrlRules.EmptyInfoWords;
            ToolTipService.SetToolTip(metadataText, null);
            if (addRefusal is not null) addRefusal = null;
            UpdateButtons();
        }

        void ShowHandoff(string words, Func<Task> next)
        {
            ShowNoLink(null);
            handoff = next;
            details.Visibility = Visibility.Collapsed;
            addressStatus.Show(words);
            UpdateButtons();
        }

        void ShowAsDirect(string link, RemoteClassifyResult? result, bool keepCantTell = false)
        {
            var changed = link != url;
            if (!keepCantTell) Reset();
            ready = true;
            handoff = null;
            problem = null;
            if (!keepCantTell) addressStatus.Show(string.Empty);
            url = link;
            addressResult = result;
            // A direct link holds an account key, which the Host keeps out of everything shown or saved; the box shows its
            // site instead, as the old second step did, and the link itself stays here.
            if (result?.Kind == "providerDirectLink") ShowInBox(RemoteText.AddressToShow(link, result), "Site");
            noteText.Text = string.IsNullOrWhiteSpace(result?.Note) ? string.Empty : UrlRedactor.HideAddresses(result.Note);
            noteText.Visibility = noteText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            metadataActions.Visibility = Visibility.Visible;
            if (changed && useAuthentication.IsChecked == true && AddUrlRules.LeavesSite(credentialsSite, link))
            {
                // Unticking runs MetadataCredentialsChanged, which forgets the site.
                authenticationPassword.Password = string.Empty;
                useAuthentication.IsChecked = false;
                credentialsDrops++;
                addressStatus.Show(AddUrlRules.CredentialsClearedWords, isProblem: true);
            }
            if (changed)
            {
                addRefusal = null;
                knownSize = null;
                refreshSpace();
                ToolTipService.SetToolTip(metadataText, null);
                if (!nameEdited)
                {
                    assigningServerName = true;
                    nameBox.Text = string.Empty;
                    assigningServerName = false;
                }
                UseCategoryFolder();
                _ = PreviewMetadataAsync(automatic: true);
            }
            UpdateButtons();
        }

        void ShowCantTell(string words, string link, RemoteClassifyResult? result)
        {
            ShowNoLink(null);
            cantTell = true;
            cantTellLink = (link, result);
            problem = $"{words} So it isn't treated as an ordinary link unless you say so.";
            addressStatus.Show(problem, isProblem: true);
            direct.Visibility = Visibility.Visible;
            UpdateButtons();
        }

        direct.Click += (_, _) =>
        {
            if (!cantTell) return;
            if (direct.IsChecked == true) ShowAsDirect(cantTellLink.Link, cantTellLink.Result, keepCantTell: true);
            else
            {
                ready = false;
                url = string.Empty;
                StopMetadata();
                metadataActions.Visibility = Visibility.Collapsed;
                metadataText.Text = AddUrlRules.EmptyInfoWords;
                UpdateButtons();
            }
        };

        // Continue: this dialog closes to open the next one. A dialog waiting for this one to close (the tray's update
        // check) waits for that one too, or it would take its place (review of 0.7.2, ux-7).
        var handoffBegun = false;
        void ChooseHandoff()
        {
            choice = AddUrlChoice.Handoff;
            if (handoffBegun) return;
            handoffBegun = true;
            BeginDialogHandoff();
        }

        // Works out what the text is. Returns the Host's check when one is needed, which goes on after this returns.
        Task? Evaluate(string text)
        {
            waiting?.Cancel();
            checkCancellation?.Cancel();
            var generation = ++checkGeneration;
            evaluated = text;
            urlBox.Header = "URL";
            // An earlier Enter was about the text before (Go sets it again for this text).
            goWhenReady = null;
            checkingAddress = false;
            addressBusy.IsActive = false;
            addressBusy.Visibility = Visibility.Collapsed;
            var input = AddUrlRules.Read(text, providerAdd);
            switch (input.Kind)
            {
                case AddUrlKind.Empty:
                    ShowNoLink(null);
                    return null;
                case AddUrlKind.NotALink:
                    ShowNoLink(AddUrlRules.NotALinkWords(torrentInputs));
                    return null;
                case AddUrlKind.Ftp:
                    ShowNoLink(FtpAddresses.NotSupported);
                    return null;
                case AddUrlKind.SeveralLinks:
                {
                    // U-WL-12: a numbered address, or pasted text with several links, is several downloads, which Add Links lists one by one.
                    var links = input.Links;
                    ShowHandoff(AddUrlRules.HandoffWords(input, torrentDialog: false), () => ShowAddLinksAsync(links));
                    return null;
                }
                case AddUrlKind.Torrent:
                {
                    // T1: a magnet or .torrent file goes on to the torrent dialog, whose lines can still go to the provider. With
                    // torrents off there it asks whether to open Options.
                    if (!torrentInputs)
                    {
                        ShowNoLink(AddUrlRules.NoTorrentsWords);
                        return null;
                    }

                    var torrent = input.Address;
                    var torrentDialog = LocalTorrentsApply || !providerAdd;
                    ShowHandoff(AddUrlRules.HandoffWords(input, torrentDialog),
                        torrentDialog ? () => AddTorrentsAsync("mixed", [torrent]) : () => ShowProviderAddAsync(AddFocus.OneAddress, [torrent]));
                    return null;
                }
            }

            // UI.B27: an address on this PC or its local network is an ordinary download; no provider is asked about it.
            if (!providerAdd || NetworkAddresses.IsLocalWebAddress(input.Address))
            {
                ShowAsDirect(input.Address, null);
                return null;
            }

            return CheckWithHostAsync(input.Address, generation);
        }

        // With an account that could take it, only the Host's answer says where an address goes.
        async Task CheckWithHostAsync(string link, int generation)
        {
            ShowNoLink(null);
            checkingAddress = true;
            addressBusy.IsActive = true;
            addressBusy.Visibility = Visibility.Visible;
            addressStatus.Show("Checking this address…");
            UpdateButtons();
            using var cancellation = new CancellationTokenSource();
            checkCancellation = cancellation;
            RemoteClassifyResult result;
            try
            {
                // What it says goes on to the provider's add dialog, which may offer its files.
                result = await _remote.ClassifyAsync(link, cancellation.Token, withFiles: true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(checkCancellation, cancellation)) checkCancellation = null;
                if (generation == checkGeneration && !closed)
                {
                    checkingAddress = false;
                    addressBusy.IsActive = false;
                    addressBusy.Visibility = Visibility.Collapsed;
                }
            }

            if (generation != checkGeneration || closed) return;
            var name = string.IsNullOrWhiteSpace(result.ProviderName) ? provider : result.ProviderName;
            if (!result.Ok)
            {
                ShowCantTell(RemoteText.Failure(result.Error), link, result);
            }
            else
            {
                switch (result.Kind)
                {
                    case "ordinaryLink" or "providerDirectLink":
                        ShowAsDirect(link, result);
                        break;
                    case "folderLink" or "magnet" or "torrentFile" or "hosterLink":
                        ShowHandoff(AddUrlRules.ProviderWords(result.Kind, result.HosterName, name),
                            () => ShowProviderAddAsync(AddFocus.OneAddress, [link], result));
                        break;
                    case "undetermined":
                        ShowCantTell(RemoteText.Failure(result.Reason, null, name), link, result);
                        break;
                    default:
                        ShowNoLink(string.IsNullOrWhiteSpace(result.Reason) ? "This app can't use this." : UrlRedactor.HideAddresses(result.Reason));
                        break;
                }
            }

            // Enter, Start or Download later was pressed before the answer came: it goes on now if it can. Download later
            // doesn't open another dialog: when the answer is one, its line says Continue does. When the user name and
            // password were just cleared for this address, nothing goes on until the user has seen why.
            if (goWhenReady is { } wanted)
            {
                goWhenReady = null;
                if (credentialsDrops == dropsWhenPressed &&
                    (handoff is not null ? wanted != AddUrlChoice.Later : ready && addRefusal is null && AuthenticationIsValid()))
                {
                    if (handoff is not null) ChooseHandoff();
                    else choice = wanted == AddUrlChoice.Later ? AddUrlChoice.Later : AddUrlChoice.Start;
                    dialog.Hide();
                }
            }
        }

        // The address stopped changing for a moment: check it.
        async void Schedule()
        {
            if (closed) return;
            var text = CurrentText();
            UpdateButtons();
            if (text == evaluated) return;
            // What was found is about the text before, so it can't be started until this text is checked, and an Enter
            // pressed for it doesn't start this one. Nothing counts as checked now, so going back to the same text checks
            // it again rather than leaving the dropped check's 'Checking' behind.
            checkGeneration++;
            checkCancellation?.Cancel();
            evaluated = null;
            goWhenReady = null;
            checkingAddress = false;
            addressBusy.IsActive = false;
            addressBusy.Visibility = Visibility.Collapsed;
            waiting?.Cancel();
            using var delay = new CancellationTokenSource();
            waiting = delay;
            try
            {
                await Task.Delay(AddUrlRules.CheckDelay, delay.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(waiting, delay)) waiting = null;
            }

            if (closed || CurrentText() == evaluated) return;
            _ = Evaluate(CurrentText());
        }

        TextBox? watchedText = null;
        urlBox.Loaded += (_, _) =>
        {
            if (watchedText is not null || FindDescendant<TextBox>(urlBox, "EditableText") is not { } inner) return;
            watchedText = inner;
            inner.TextChanged += (_, _) => Schedule();
            // A one-line box keeps only the first line of a pasted list of links; the lines go in side by side instead,
            // so a list still goes on to Add links.
            inner.Paste += async (_, args) =>
            {
                args.Handled = true;
                string pasted;
                try
                {
                    var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (!clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
                    pasted = AddUrlRules.OneLine(await clip.GetTextAsync());
                }
                catch (Exception)
                {
                    return;
                }

                var start = Math.Min(inner.SelectionStart, inner.Text.Length);
                var length = Math.Min(inner.SelectionLength, inner.Text.Length - start);
                inner.Text = inner.Text[..start] + pasted + inner.Text[(start + length)..];
                inner.SelectionStart = start + pasted.Length;
            };
        };
        urlBox.SelectionChanged += (_, _) => Schedule();
        urlBox.RegisterPropertyChangedCallback(ComboBox.TextProperty, (_, _) => Schedule());

        bool AuthenticationIsValid()
        {
            if (useAuthentication.IsChecked == true &&
                (string.IsNullOrWhiteSpace(authenticationUser.Text) || authenticationUser.Text.IndexOfAny([':', '\r', '\n']) >= 0))
            {
                authenticationError.Text = "Enter a user name without colons or line breaks.";
                authenticationError.Visibility = Visibility.Visible;
                authenticationExpander.IsExpanded = true;
                return false;
            }

            authenticationError.Visibility = Visibility.Collapsed;
            return true;
        }

        // Start, Download later, Continue and Enter in the address box. False keeps the dialog open.
        bool Go(AddUrlChoice wanted)
        {
            var text = CurrentText();
            var drops = credentialsDrops;
            if (text != evaluated || checkingAddress)
            {
                var pending = text != evaluated ? Evaluate(text) : null;
                if (pending is not null || checkingAddress)
                {
                    // The Host is asked first; the button pressed (Start or Download later) goes on by itself once it has
                    // answered (review of 0.7.2, ux-3).
                    goWhenReady = wanted;
                    dropsWhenPressed = drops;
                    return false;
                }
            }

            // This address is on another site than the user name and password were typed for, so they were just cleared:
            // nothing is sent until the user has seen that.
            if (credentialsDrops != drops) return false;

            if (handoff is not null)
            {
                ChooseHandoff();
                return true;
            }

            if (cantTell && direct.IsChecked != true)
            {
                // Try again.
                _ = CheckWithHostAsync(cantTellLink.Link, ++checkGeneration);
                return false;
            }

            if (!ready || addRefusal is not null)
            {
                addressStatus.Say(problem ?? AddUrlRules.NotALinkWords(torrentInputs), isProblem: true);
                urlBox.Focus(FocusState.Programmatic);
                return false;
            }

            if (!AuthenticationIsValid()) return false;
            choice = wanted == AddUrlChoice.Later ? AddUrlChoice.Later : AddUrlChoice.Start;
            return true;
        }

        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Go(AddUrlChoice.Start);
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !Go(AddUrlChoice.Later);

        urlBox.PreviewKeyDown += (_, args) =>
        {
            if (urlBox.IsDropDownOpen) return;
            switch (args.Key)
            {
                // An editable ComboBox keeps Escape for itself, even with its list closed.
                case Windows.System.VirtualKey.Escape:
                    args.Handled = true;
                    choice = AddUrlChoice.None;
                    dialog.Hide();
                    break;
                // And Enter, which here starts the download once the address is ready, as the dialog's default button does.
                case Windows.System.VirtualKey.Enter:
                    args.Handled = true;
                    if (dialog.IsPrimaryButtonEnabled && Go(AddUrlChoice.Start)) dialog.Hide();
                    break;
            }
        };

        dialog.Opened += (_, _) =>
        {
            // Shown again after a failed Start: the check runs again, and its answer counts again.
            closed = false;
            metadataClosed = false;
            if (evaluated is null) _ = Evaluate(CurrentText());
            else if (ready) _ = PreviewMetadataAsync();
            UpdateButtons();
        };
        dialog.Closing += (_, _) =>
        {
            closed = true;
            metadataClosed = true;
            metadataGeneration++;
            metadataCancellation?.Cancel();
            checkGeneration++;
            checkCancellation?.Cancel();
            waiting?.Cancel();
            goWhenReady = null;
        };

        var handoffChosen = false;
        try
        {
            // U-WR-2: a Start the Host refuses shows its reason in this dialog, which opens again with all that was typed.
            // The dialog that just closed is still on its way out for a moment, or another dialog may be open on this
            // window, and DialogGuard refuses to show it until that one is gone; so it is tried again, and never dropped
            // while another dialog is the reason. Only a window that stops showing dialogs at all gives up, to the
            // status bar.
            string? lastProblem = null;
            var failedShows = 0;
            while (true)
            {
                var (shown, problemAfterStart) = await AddFromDialogAsync();
                var step = AddRules.AfterShow(shown, problemAfterStart, lastProblem, failedShows);
                if (step == ReshowStep.Stop) break;
                if (step == ReshowStep.GiveUp)
                {
                    ShowPassingStatus(lastProblem!);
                    break;
                }

                if (step == ReshowStep.TryAgain)
                {
                    if (!DialogGuard.IsOpen(dialog.XamlRoot)) failedShows++;
                    await Task.Delay(100);
                    continue;
                }

                lastProblem = problemAfterStart;
                failedShows = 0;
                addProblem.Text = problemAfterStart;
                addProblem.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            authenticationPassword.Password = string.Empty;
            if (handoffBegun && !(handoffChosen && handoff is not null)) EndDialogHandoff();
        }

        // Continue: the dialog for what was entered opens once this one has gone.
        if (handoffChosen && handoff is { } next)
        {
            try
            {
                for (var wait = 0; wait < 40 && DialogGuard.IsOpen(dialog.XamlRoot); wait++) await Task.Delay(50);
                await next();
            }
            finally
            {
                EndDialogHandoff();
            }
        }

        // Whether the dialog was shown, and the reason it wasn't added, to show in it again; null when it was added or the user stopped.
        async Task<(bool Shown, string? Problem)> AddFromDialogAsync()
        {
            choice = AddUrlChoice.None;
            var shown = await DialogGuard.ShowAsync(dialog);
            // Null: it couldn't be shown (another dialog was still open on this window).
            if (shown is null) return (false, null);
            switch (choice)
            {
                case AddUrlChoice.None:
                    return (true, null);
                case AddUrlChoice.Handoff:
                    handoffChosen = true;
                    return (true, null);
            }

            var existing = FindExistingJob(url);
            if (existing is not null)
            {
                var duplicateDialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme,
                    Title = "Download already exists",
                    Content = new TextBlock
                    {
                        Text = $"{existing.FileName} ({existing.StateText}) is already in the list for this URL.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Download again",
                    SecondaryButtonText = "Select existing",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                // The add dialog is still on its way out for a moment.
                for (var wait = 0; wait < 40 && DialogGuard.IsOpen(dialog.XamlRoot); wait++) await Task.Delay(50);
                var duplicateResult = await DialogGuard.ShowAsync(duplicateDialog);
                // The question couldn't be shown: what was typed comes back, with why, rather than being dropped.
                if (duplicateResult is null)
                    return (true, $"{existing.FileName} is already in the list for this URL. Choose Start again to decide whether to download it again.");
                if (duplicateResult == ContentDialogResult.Secondary)
                {
                    JobsList.SelectedItem = existing;
                    return (true, null);
                }

                if (duplicateResult != ContentDialogResult.Primary)
                {
                    return (true, null);
                }
            }

            var request = new AddJobRequest
            {
                Url = url,
                FileName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
                CategoryId = (categoryBox.SelectedItem as ComboBoxItem)?.Tag as string,
                DestinationDirectory = string.IsNullOrWhiteSpace(destinationBox.Text) ? null : destinationBox.Text.Trim(),
                StartMode = choice == AddUrlChoice.Later ? "later" : "now",
                Description = string.IsNullOrWhiteSpace(descriptionBox.Text) ? null : descriptionBox.Text.Trim(),
                RememberDestination = rememberFolder.IsChecked == true,
                ConflictPolicy = conflictBox.SelectedIndex switch
                {
                    1 => "overwrite",
                    2 => "fail",
                    _ => "rename",
                },
                Source = "api",
                QueueId = _settings.Queues.OrderBy(q => q.Order)
                    .ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId,
            };

            try
            {
                if (useAuthentication.IsChecked == true)
                {
                    credentialsSite ??= AddUrlRules.SiteOf(url);
                    request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{authenticationUser.Text}:{authenticationPassword.Password}"));
                }
                var reply = await _connection.AddAsync(request);
                var add = reply.GetPayload<AddJobResponse>();
                if (!add.Accepted) return (true, AddRules.NotAddedText(add.Error, _connection.Target));
                await RefreshAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (true, AddRules.NotAddedText(ex.Message, _connection.Target));
            }
        }
    }
}
