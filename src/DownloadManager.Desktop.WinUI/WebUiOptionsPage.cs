using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Web UI page of Options: lets a browser on another device control this PC's downloads, through the
/// web bridge the Host starts. The box, port and names follow the window's OK and Apply. The password, the
/// browser link and the firewall step take effect straight away, whichever way the window is closed, and the
/// password is write-only: it goes out in one message and is gone from the window. What is shown follows
/// <c>webui.changed</c>, so a change made by the Host (a failed start, a rule found later) shows without a refresh.
/// </summary>
internal sealed class WebUiOptionsPage
{
    private readonly RemoteClient _client;
    private readonly CancellationTokenSource _closing = new();
    private readonly CheckBox _enable = new() { Content = "Let other devices on my network control this PC's downloads" };
    private readonly StatusLine _state = new("Web UI state");
    private readonly NumberBox _port = SettingsWindow.Number("Port", WebUiForm.DefaultPort, WebUiForm.MinPort, WebUiForm.MaxPort);
    // U-WR-15: fixed widths; sized to their headers they were narrower than what is typed in them.
    private readonly TextBox _names = new() { Header = "Also reachable as", PlaceholderText = "nas, nas.example.com", Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _addresses = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
    private readonly TextBlock _addressesHint = SettingsWindow.Hint("Type one of these in the browser on the other device.");
    private readonly TextBlock _localOnlyHint = SettingsWindow.Hint("Other devices can't reach the web UI until Windows Firewall allows it (see Windows Firewall below). Open in browser works on this PC.");
    private readonly Button _openInBrowser = new() { Content = "Open in browser", MinWidth = 92, IsEnabled = false };
    private readonly StatusLine _openResult = new("Open in browser result");
    private readonly StatusLine _passwordState = new("Web UI password status");
    private readonly PasswordBox _newPassword = new() { Header = "New password", PasswordRevealMode = PasswordRevealMode.Hidden, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PasswordBox _repeatPassword = new() { Header = "Type it again", PasswordRevealMode = PasswordRevealMode.Hidden, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _setPassword = new() { Content = "Set password", MinWidth = 92, IsEnabled = false };
    private readonly StatusLine _passwordResult = new("Web UI password result");
    private readonly StatusLine _firewallState = new("Windows Firewall status");
    private readonly StatusLine _profile = new("Network type");
    private readonly StatusLine _publicNote = new("Public network note");
    private readonly CheckBox _allowPublic = new() { Content = "Also allow on Public networks (every Public network, including ones you join later)" };
    private readonly Button _allow = new() { Content = "Allow through Windows Firewall", IsEnabled = false };
    private readonly Button _recheck = new() { Content = "Check again", MinWidth = 92, IsEnabled = false };
    private readonly StatusLine _firewallResult = new("Windows Firewall result");
    private readonly StatusLine _loadStatus = new("Web UI settings");
    private readonly Button _tryAgain = new() { Content = "Try again", Visibility = Visibility.Collapsed };
    private readonly List<Control> _fields;

    private const string NewPortNote = "Select Apply to use the new port, then allow it through Windows Firewall.";

    private WebUiStatusDto? _status;
    private IReadOnlyList<NetworkEntry>? _profiles;
    private bool _profilesKnown;
    private bool _profilesRequested;
    private bool _applying;
    private bool _busy;

    public WebUiOptionsPage(RemoteClient client)
    {
        _client = client;

        AutomationProperties.SetName(_enable, "Let other devices on my network control this PC's downloads");
        AutomationProperties.SetName(_port, "Web UI port");
        AutomationProperties.SetName(_names, "Also reachable as");
        AutomationProperties.SetName(_addresses, "Addresses to type on the other device");
        AutomationProperties.SetName(_openInBrowser, "Open the web UI in your browser");
        AutomationProperties.SetName(_newPassword, "New web UI password");
        AutomationProperties.SetName(_repeatPassword, "New web UI password again");
        AutomationProperties.SetName(_setPassword, "Set the web UI password");
        AutomationProperties.SetName(_allowPublic, "Also allow on Public networks");
        AutomationProperties.SetName(_allow, "Allow the web UI through Windows Firewall");
        AutomationProperties.SetName(_recheck, "Check Windows Firewall again");
        AutomationProperties.SetName(_tryAgain, "Try reading the web UI settings again");

        _fields = [_enable, _port, _names, _newPassword, _repeatPassword, _allowPublic];
        SetFieldsEnabled(false);

        _enable.Checked += (_, _) => OnBoxChanged();
        _enable.Unchecked += (_, _) => OnBoxChanged();
        _port.ValueChanged += (_, _) => RenderButtons();
        _newPassword.PasswordChanged += (_, _) => PasswordEdited();
        _repeatPassword.PasswordChanged += (_, _) => PasswordEdited();
        foreach (var box in new[] { _newPassword, _repeatPassword })
        {
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter && _setPassword.IsEnabled)
                {
                    args.Handled = true;
                    _ = SetPasswordAsync();
                }
            };
        }

        _setPassword.Click += (_, _) => _ = SetPasswordAsync();
        _openInBrowser.Click += (_, _) => _ = OpenInBrowserAsync();
        _allow.Click += (_, _) => _ = AllowAsync();
        _recheck.Click += (_, _) => _ = RecheckAsync();
        _tryAgain.Click += (_, _) => _ = LoadAsync();

        var passwordButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        passwordButtons.Children.Add(_setPassword);
        var firewallButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        firewallButtons.Children.Add(_allow);
        firewallButtons.Children.Add(_recheck);

        Controls =
        [
            _loadStatus.Text,
            _tryAgain,
            // U-WL-10: in the order they are done. The box needs a password, so the password comes first.
            SettingsWindow.Hint("A browser on another device opens an address below and signs in with the password. From there it can add, stop and delete downloads and change speed limits and queues, within the folders Inbound already uses. It can't run programs on this PC. Nothing runs while the box is off."),
            SettingsWindow.Section("1. Password"),
            _passwordState.Text,
            _newPassword,
            _repeatPassword,
            passwordButtons,
            _passwordResult.Text,
            SettingsWindow.Hint($"At least {WebUiForm.MinPasswordLength} characters. It is set only here."),
            SettingsWindow.Section("2. Turn it on"),
            _enable,
            _state.Text,
            _port,
            SettingsWindow.Section("Addresses"),
            _names,
            SettingsWindow.Hint("Names that other devices use for this PC, such as nas or nas.local. Separate them with commas."),
            _addresses,
            _addressesHint,
            _localOnlyHint,
            _openInBrowser,
            _openResult.Text,
            SettingsWindow.Hint("Open in browser signs in for you, with a link that works once for a minute, so the password isn't typed into a page on this PC."),
            .. WindowsOnly(
                SettingsWindow.Section("Windows Firewall"),
                _firewallState.Text,
                _profile.Text,
                _publicNote.Text,
                _allowPublic,
                firewallButtons,
                _firewallResult.Text,
                SettingsWindow.Hint("Until Windows Firewall allows it, the web UI answers on this PC only. Allowing it asks Windows for permission once, in a Windows PowerShell prompt, and adds a rule for the web UI program, this port, and devices on your own network.")),
        ];

        _client.WebUiChanged += OnChanged;
        _ = LoadAsync();
    }

    public UIElement[] Controls { get; }

    /// <summary>Where the page sits in the Options window, for the message that jumps to it.</summary>
    public int PageIndex { get; set; }

    /// <summary>A password was typed and not set. OK shouldn't silently lose it.</summary>
    public bool HasUnsavedPassword => !string.IsNullOrEmpty(_newPassword.Password) || !string.IsNullOrEmpty(_repeatPassword.Password);

    /// <summary>What is wrong with the page before anything is saved, or null.</summary>
    public string? Validate()
    {
        if (HasUnsavedPassword) return "The web UI password you typed isn't set. Select Set password, or empty the boxes.";
        if (_status is null) return null;
        WebUiForm.Changes(_status, _enable.IsChecked == true, _port.Value, _names.Text, out var problem);
        return problem;
    }

    /// <summary>Sends what changed. Returns what the user should see, or null when all is well.</summary>
    public async Task<string?> SaveAsync()
    {
        if (_status is null) return null;
        var request = WebUiForm.Changes(_status, _enable.IsChecked == true, _port.Value, _names.Text, out var problem);
        if (problem is not null) return problem;
        if (request is null) return null;

        WebUiResult result;
        try
        {
            result = await _client.SetWebUiAsync(request, _closing.Token);
            if (result.Ok && result.Status is null) result = await _client.GetWebUiAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        // A refusal (nothing changed) comes back as words, and the window stays open on this page.
        if (!result.Ok || result.Status is null) return result.Error ?? RemoteText.Failure(null);
        var status = result.Status;
        if (request.Enabled == true && status.State == WebUiStates.Starting)
        {
            // The port-in-use or forbidden-port failure arrives a moment after the start, so wait for it before
            // the window closes and the person believes the web UI is on.
            try
            {
                status = await WaitForStartAsync(status, _closing.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        ApplyStatus(status, replaceInputs: true);
        return request.Enabled == true && status.State == WebUiStates.Failed
            ? WebUiForm.StateText(status).Text
            : null;
    }

    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StartPoll = TimeSpan.FromMilliseconds(400);

    /// <summary>Reads the state again until it is no longer starting, for a few seconds at most.</summary>
    private async Task<WebUiStatusDto> WaitForStartAsync(WebUiStatusDto status, CancellationToken token)
    {
        var until = DateTime.UtcNow + StartPatience;
        while (status.State == WebUiStates.Starting && DateTime.UtcNow < until)
        {
            await Task.Delay(StartPoll, token);
            var again = await _client.GetWebUiAsync(token);
            if (again.Ok && again.Status is not null) status = again.Status;
        }

        return status;
    }

    public void Close()
    {
        _client.WebUiChanged -= OnChanged;
        _closing.Cancel();
        _newPassword.Password = string.Empty;
        _repeatPassword.Password = string.Empty;
    }

    private async Task LoadAsync()
    {
        _tryAgain.Visibility = Visibility.Collapsed;
        _loadStatus.Show("Reading the web UI settings…");
        WebUiResult result;
        try
        {
            result = await _client.GetWebUiAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!result.Ok || result.Status is null)
        {
            // Nothing on this page can be trusted, so nothing on it can be changed or saved.
            _loadStatus.Show($"Couldn't read the web UI settings. {result.Error ?? RemoteText.HostUnreadable}", isProblem: true);
            _tryAgain.Visibility = Visibility.Visible;
            return;
        }

        _loadStatus.Show(string.Empty);
        ApplyStatus(result.Status, replaceInputs: true);
        // The network type is read once the Host has a web UI to show, never while Options only opens.
        if (!_profilesRequested) await LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        _profilesRequested = true;
        List<NetworkEntry>? profiles;
        try
        {
            profiles = await WebUiFirewall.ReadNetworkProfilesAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _profiles = profiles;
        _profilesKnown = true;
        RenderFirewall();
    }

    private void OnChanged(WebUiStatusDto status)
    {
        if (_status is null) return;
        ApplyStatus(status, replaceInputs: false);
    }

    /// <param name="replaceInputs">Put the saved values in the box, port and names. Left alone for a change from the Host while the person is editing.</param>
    private void ApplyStatus(WebUiStatusDto status, bool replaceInputs)
    {
        var previous = _status;
        _status = status;
        _applying = true;
        try
        {
            if (replaceInputs || previous is null || !InputsEdited(previous))
            {
                _enable.IsChecked = status.Enabled;
                _port.Value = status.Port;
                _names.Text = string.Join(", ", status.AlsoReachableAs);
            }
        }
        finally
        {
            _applying = false;
        }

        SetFieldsEnabled(true);
        Render();
    }

    private bool InputsEdited(WebUiStatusDto saved)
        => (_enable.IsChecked == true) != saved.Enabled ||
           // An emptied port box counts as being edited, so a change from the Host doesn't refill it.
           double.IsNaN(_port.Value) || (int)_port.Value != saved.Port ||
           WebUiForm.ParseNames(_names.Text, out _) is not { } names ||
           !names.SequenceEqual(saved.AlsoReachableAs, StringComparer.OrdinalIgnoreCase);

    private void Render()
    {
        if (_status is not { } status) return;
        var (stateText, stateProblem) = WebUiForm.StateText(status);
        _state.Show(stateText, stateProblem);
        _passwordState.Show(WebUiForm.PasswordText(status));
        _setPassword.Content = status.HasPassword ? "Change password" : "Set password";
        // The box is ticked only when there is a password, but one that is on can always be switched off.
        _enable.IsEnabled = status.HasPassword || _enable.IsChecked == true;

        var addresses = WebUiForm.AddressesToType(status);
        // U-WL-10: before the firewall step, the addresses that will work once it is done.
        var later = addresses.Count == 0 ? WebUiForm.AddressesOnceAllowed(status, Environment.MachineName) : [];
        var shown = addresses.Count > 0 ? addresses : later;
        _addresses.Text = string.Join(Environment.NewLine, shown);
        _addresses.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _addressesHint.Text = later.Count > 0
            ? "Once Windows Firewall allows it, type one of these in the browser on the other device."
            : "Type one of these in the browser on the other device.";
        _addressesHint.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _localOnlyHint.Visibility = OperatingSystem.IsWindows() && status is { State: WebUiStates.Running, LocalOnly: true } ? Visibility.Visible : Visibility.Collapsed;
        RenderFirewall();
        RenderButtons();
    }

    private void RenderFirewall()
    {
        if (_status is not { } status) return;
        _firewallState.Show(WebUiForm.FirewallText(status));
        _profile.Show(_profilesKnown ? WebUiForm.ProfileText(_profiles) : string.Empty);
        _publicNote.Show(WebUiForm.ShowPublicNote(_profiles) ? WebUiForm.PublicNetworkNote : string.Empty, isProblem: false);
        RenderButtons();
    }

    private void RenderButtons()
    {
        var status = _status;
        _setPassword.IsEnabled = !_busy && status is not null && !string.IsNullOrEmpty(_newPassword.Password);
        _openInBrowser.IsEnabled = !_busy && status is { State: WebUiStates.Running };
        _recheck.IsEnabled = !_busy && status is not null;
        var portMoved = status is not null && !double.IsNaN(_port.Value) && (int)_port.Value != status.Port;
        _allow.IsEnabled = !_busy && status is not null && !string.IsNullOrEmpty(status.BridgePath) && !portMoved;
        if (portMoved && !_busy) _firewallResult.Show(NewPortNote);
        else if (_firewallResult.Text.Text == NewPortNote) _firewallResult.Show(string.Empty);
    }

    private void SetFieldsEnabled(bool enabled)
    {
        foreach (var field in _fields) field.IsEnabled = enabled;
    }

    private void OnBoxChanged()
    {
        if (_applying || _status is null) return;
        // Ticking without a password is refused here as well as on OK, so the person hears it at once.
        if (_enable.IsChecked == true && !_status.HasPassword)
        {
            _applying = true;
            _enable.IsChecked = false;
            _applying = false;
            _state.Say("Set a password first. This can't be turned on without one.", isProblem: true);
            return;
        }

        Render();
    }

    private void PasswordEdited()
    {
        RenderButtons();
        if (!HasUnsavedPassword) _passwordResult.Show(string.Empty);
    }

    // ---- Password -----------------------------------------------------------------------------

    private async Task SetPasswordAsync()
    {
        var password = _newPassword.Password;
        var repeated = _repeatPassword.Password;
        if (WebUiForm.PasswordProblem(password, repeated) is { } problem)
        {
            _passwordResult.Say(problem, isProblem: true);
            return;
        }

        if (_busy) return;
        // Out of the boxes before anything is sent, whatever the answer turns out to be.
        _newPassword.Password = string.Empty;
        _repeatPassword.Password = string.Empty;
        await RunAsync(async token =>
        {
            _passwordResult.Say("Setting the password…");
            var result = await _client.SetWebUiPasswordAsync(password, token);
            password = repeated = string.Empty;
            if (!result.Ok)
            {
                _passwordResult.Say(result.Error ?? RemoteText.Failure(null), isProblem: true);
                return;
            }

            await ShowReplyAsync(result, token);
            _passwordResult.Say("Password set.");
        });
    }

    // ---- Browser -------------------------------------------------------------------------------

    private async Task OpenInBrowserAsync()
    {
        if (_status is not { } status) return;
        await RunAsync(async token =>
        {
            _openResult.Say("Opening your browser…");
            var ticket = await _client.OpenWebUiTicketAsync(token);
            if (!ticket.Ok || !WebUiForm.IsLocalSignInUrl(ticket.Url, status.Port))
            {
                _openResult.Say(ticket.Error ?? "Inbound sent a link this window won't open.", isProblem: true);
                return;
            }

            try
            {
                var opened = await Windows.System.Launcher.LaunchUriAsync(new Uri(ticket.Url!));
                _openResult.Say(opened ? "Opened in your browser." : "Your browser couldn't be opened.", isProblem: !opened);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or System.Runtime.InteropServices.COMException)
            {
                _openResult.Say("Your browser couldn't be opened.", isProblem: true);
            }
        });
    }

    // ---- Windows Firewall ------------------------------------------------------------------------

    private async Task AllowAsync()
    {
        if (_status is not { } status) return;
        FirewallCommand command;
        try
        {
            command = WebUiFirewall.BuildAllowCommand(status.BridgePath, status.Port, status.RuleName, _allowPublic.IsChecked == true);
        }
        catch (ArgumentException ex)
        {
            _firewallResult.Say(ex.Message, isProblem: true);
            return;
        }

        await RunAsync(async token =>
        {
            _firewallResult.Say("Waiting for Windows to ask for permission…");
            var outcome = await WebUiFirewall.RunElevatedAsync(command, token);
            switch (outcome)
            {
                case FirewallRunOutcome.Declined:
                    _firewallResult.Say("Windows Firewall wasn't changed, because permission wasn't given.", isProblem: true);
                    return;
                case FirewallRunOutcome.TimedOut:
                    _firewallResult.Say("Windows hasn't answered yet, and the change may still go through. Select Check again before trying once more.", isProblem: true);
                    return;
                case FirewallRunOutcome.Failed:
                    _firewallResult.Say("Windows Firewall couldn't be changed. Try again, or add the rule in Windows Security.", isProblem: true);
                    return;
            }

            await RecheckCoreAsync(token, afterAdding: true);
        });
    }

    private Task RecheckAsync() => RunAsync(async token =>
    {
        await RecheckCoreAsync(token, afterAdding: false);
        // The network type can change while the window is open, so it is read again here.
        await LoadProfilesAsync();
    });

    private async Task RecheckCoreAsync(CancellationToken token, bool afterAdding)
    {
        _firewallResult.Say("Checking Windows Firewall…");
        var result = await _client.RecheckWebUiAsync(token);
        if (!result.Ok)
        {
            _firewallResult.Say(result.Error ?? RemoteText.Failure(null), isProblem: true);
            return;
        }

        await ShowReplyAsync(result, token);
        var ready = _status?.Firewall == WebUiFirewallStates.Ready;
        _firewallResult.Say(ready
            ? (afterAdding ? "Done. Windows Firewall now allows other devices to reach the web UI." : "Windows Firewall allows other devices to reach the web UI.")
            : (afterAdding ? "The rule was added, but the web UI doesn't see it yet. Select Check again in a moment." : "Windows Firewall doesn't allow this yet."),
            isProblem: !ready);
    }

    // ---- Shared ------------------------------------------------------------------------------

    /// <summary>Shows the status a reply carried, or reads it again when the reply only said it worked.</summary>
    private async Task ShowReplyAsync(WebUiResult result, CancellationToken token)
    {
        var status = result.Status;
        if (status is null)
        {
            var again = await _client.GetWebUiAsync(token);
            status = again.Status;
        }

        if (status is not null) ApplyStatus(status, replaceInputs: false);
    }

    /// <summary>One action at a time; the buttons rest meanwhile. A closed window ends the wait quietly.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        RenderButtons();
        try
        {
            await work(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            _busy = false;
            if (!_closing.IsCancellationRequested) RenderButtons();
        }
    }

    /// <summary>The Windows Firewall step exists only on Windows; elsewhere the system's own firewall is the user's.</summary>
    private static UIElement[] WindowsOnly(params UIElement[] elements) => OperatingSystem.IsWindows() ? elements : [];
}
