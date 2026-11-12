using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Options › Torrents (T1, owner-approved mockup): turn torrents on, where magnets and .torrent files go when a
/// provider account could take them too, and whether files are chosen first. The fields are part of
/// <see cref="SettingsDto"/>, so they follow the window's OK, Cancel and Apply. The handler option (step 6b) is offered
/// only for this PC's own Host, which registers it in Windows; for another server the saved value is carried through.
/// T2 adds the listening port, the UPnP/NAT-PMP choice, the port hint, and (this PC only, on Windows) the Windows Firewall
/// rule, and the seeding settings (keep seeding, stop at a ratio or after hours, the upload limit), checked by the Host's
/// rules (<see cref="TorrentSeedRules.Problem"/>) before saving.
/// </summary>
internal sealed class TorrentOptionsPage
{
    private static readonly string[] Routes = [TorrentRoutesText.Ask, TorrentRoutesText.ThisPc, TorrentRoutesText.Provider];

    private readonly CheckBox _enabled = new()
    {
        Content = "Download torrents on this PC",
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };
    private readonly RadioButtons? _route;
    private readonly CheckBox _chooseFiles = new() { Content = "Ask which files to download before starting" };
    private readonly CheckBox? _handler;
    private readonly NumberBox _port;
    private readonly CheckBox _upnp = new() { Content = "Ask my router to forward the port (UPnP and NAT-PMP)" };
    private readonly CheckBox _seed = new() { Content = "Keep seeding after a download finishes" };
    private readonly NumberBox _seedRatio;
    private readonly NumberBox _seedHours;
    private readonly NumberBox _uploadLimit;
    private double _savedRatio;
    private double _savedHours;
    private long _savedUploadLimit;
    private readonly StatusLine _portHint = new("Port status");
    private readonly StatusLine _firewallStatus = new("Windows Firewall status");
    private readonly Button? _allowFirewall;
    private readonly Button? _removeFirewall;
    private readonly RemoteClient _client;
    private readonly CancellationTokenSource _closing = new();
    private bool _busy;
    private bool _hasOwnRules;

    // T3 (only for a Host that has them): finding peers, privacy, the proxy and connection limits.
    private readonly bool _withControls;
    private readonly Func<(string Mode, string? Address)>? _proxy;
    private readonly CheckBox _dht = new() { Content = "DHT (find peers without a tracker)" };
    private readonly CheckBox _pex = new() { Content = "Peer exchange (PEX)" };
    private readonly CheckBox _lsd = new() { Content = "Local peers on this network (LSD)" };
    private readonly ComboBox _encryption = new() { Header = "Encryption", MinWidth = 220 };
    private readonly CheckBox _useProxy = new() { Content = "Use the proxy from Options › Proxy for peers and trackers" };
    private readonly StatusLine _proxyNote = new("Torrent proxy status");
    private readonly NumberBox _seedsAtOnce;
    private readonly NumberBox _perTorrent;
    private readonly NumberBox _totalConnections;

    /// <param name="withControls">T3: the Host has the peer, privacy, proxy and connection settings.</param>
    /// <param name="proxy">T3: Options › Proxy as the window has it now (mode and address), for the proxy note.</param>
    public TorrentOptionsPage(SettingsDto settings, string? providerName, bool offerHandler, RemoteClient client,
        bool withControls = false, Func<(string Mode, string? Address)>? proxy = null)
    {
        _client = client;
        _withControls = withControls;
        _proxy = proxy;
        _seedsAtOnce = SettingsWindow.Number("Torrents seeding at once", settings.TorrentSeedsAtOnce > 0 ? settings.TorrentSeedsAtOnce : null, 0, 1000, "All of them");
        _perTorrent = SettingsWindow.Number("Connections per torrent", settings.TorrentConnectionsPerTorrent > 0 ? settings.TorrentConnectionsPerTorrent : null,
            0, 2000, "No limit of its own");
        _totalConnections = SettingsWindow.Number("Torrent connections in all", settings.TorrentMaxConnections > 0 ? settings.TorrentMaxConnections : null,
            0, 20000, "The torrent program's default");
        _enabled.IsChecked = settings.TorrentsEnabled;
        _chooseFiles.IsChecked = settings.TorrentChooseFiles;
        AutomationProperties.SetName(_enabled, "Download torrents on this PC");
        AutomationProperties.SetName(_chooseFiles, "Ask which files to download before starting");
        var dependent = new List<FrameworkElement> { _chooseFiles };
        if (providerName is not null)
        {
            _route = new RadioButtons
            {
                Header = "Magnets and .torrent files go to",
                ItemsSource = new[] { "Ask each time", "This PC", providerName },
                SelectedIndex = Math.Max(0, Array.IndexOf(Routes, settings.TorrentRoute)),
                Margin = new Thickness(28, 0, 0, 0),
            };
            AutomationProperties.SetName(_route, "Magnets and .torrent files go to");
            dependent.Insert(0, _route);
        }

        _chooseFiles.Margin = new Thickness(28, 0, 0, 0);
        _port = SettingsWindow.Number("Listening port", settings.TorrentListenPort is > 0 ? settings.TorrentListenPort : null,
            TorrentListenPorts.Min, TorrentListenPorts.Max, "Chosen when torrents are turned on");
        // Left as typed, so Validate can refuse a port out of range with a message instead of the box quietly changing it.
        _port.ValidationMode = NumberBoxValidationMode.Disabled;
        _upnp.IsChecked = settings.TorrentUpnp;
        AutomationProperties.SetName(_port, "Listening port");
        AutomationProperties.SetName(_upnp, "Ask my router to forward the port (UPnP and NAT-PMP)");
        var portHelp = SettingsWindow.Hint("Other peers connect to this port while a torrent runs. Nothing listens while no torrent runs.");
        dependent.Add(_port);
        dependent.Add(_upnp);
        dependent.Add(portHelp);

        // T2 seeding: the Host's defaults are ratio 1.0 or 48 hours, whichever comes first.
        _seed.IsChecked = settings.TorrentSeedAfterDownload;
        _savedRatio = settings.TorrentSeedRatio;
        _savedHours = settings.TorrentSeedHours;
        _savedUploadLimit = settings.TorrentUploadLimit;
        AutomationProperties.SetName(_seed, "Keep seeding after a download finishes");
        _seedRatio = SettingsWindow.Number("Stop seeding at ratio", settings.TorrentSeedRatio > 0 ? settings.TorrentSeedRatio : null,
            0, TorrentSeedRules.MostRatio, "No ratio limit");
        _seedHours = SettingsWindow.Number("Stop seeding after (hours)", settings.TorrentSeedHours > 0 ? settings.TorrentSeedHours : null,
            0, TorrentSeedRules.MostHours, "No time limit");
        _uploadLimit = SettingsWindow.Number("Torrent upload limit (KB/s)", null, 0, TorrentSeedRules.MostUploadKilobytes, "Total speed limit");
        _uploadLimit.Value = TorrentSeedRules.UploadLimitShown(settings.TorrentUploadLimit);
        _uploadLimit.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        _seedRatio.SmallChange = 0.1;
        _seedRatio.LargeChange = 1;
        _seedHours.SmallChange = 1;
        _seedHours.LargeChange = 24;
        foreach (var box in new[] { _seedRatio, _seedHours, _uploadLimit })
        {
            // Left as typed, so Validate refuses a value out of range with the Host's words.
            box.ValidationMode = NumberBoxValidationMode.Disabled;
            AutomationProperties.SetName(box, (string)box.Header);
        }

        _seedRatio.Margin = _seedHours.Margin = new Thickness(28, 0, 0, 0);
        var seedHelp = SettingsWindow.Hint("A finished torrent goes on sharing its files with other peers until it reaches the ratio or the time, whichever comes first. A queue counts a torrent as finished when its download finishes.");
        var uploadHelp = SettingsWindow.Hint("Empty: torrent uploads are capped by the total speed limit in Options › Connection, if one is set.");
        dependent.Add(_seed);
        dependent.Add(seedHelp);
        dependent.Add(_uploadLimit);
        dependent.Add(uploadHelp);

        // The firewall rule is this PC's, like the handler, and Windows' own: Linux has none to set from here.
        var offerFirewall = offerHandler && OperatingSystem.IsWindows();
        var firewallHelp = offerFirewall
            ? SettingsWindow.Hint("Lets peers on the internet and your network connect in. Windows asks for permission.")
            : null;
        if (offerFirewall)
        {
            const string allowText = "Allow incoming connections (Windows Firewall)";
            const string removeText = "Remove the firewall rule";
            _allowFirewall = new Button { Content = allowText };
            _removeFirewall = new Button { Content = removeText, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_allowFirewall, allowText);
            AutomationProperties.SetName(_removeFirewall, removeText);
            _allowFirewall.Click += async (_, _) => await ChangeFirewallAsync(allow: true);
            _removeFirewall.Click += async (_, _) => await ChangeFirewallAsync(allow: false);
        }
        var help = SettingsWindow.Hint("Adds Add Magnet and Import Torrent File, and a Torrents view. The torrent engine runs only while a torrent is downloading; with nothing to do it is closed and nothing listens on the network.");
        var multiFile = SettingsWindow.Hint("For a torrent with several files. A magnet asks once its file list arrives.");
        multiFile.Margin = new Thickness(28, 0, 0, 0);
        var proxyHint = SettingsWindow.Hint("Torrents connect directly: the proxy in Options › Proxy is not used for them.");
        dependent.Add(multiFile);
        var peerControls = new List<UIElement>();
        if (withControls)
        {
            _dht.IsChecked = settings.TorrentDht;
            _pex.IsChecked = settings.TorrentPex;
            _lsd.IsChecked = settings.TorrentLsd;
            foreach (var (_, text) in TorrentControlRules.Encryption) _encryption.Items.Add(text);
            _encryption.SelectedIndex = TorrentControlRules.EncryptionIndex(settings.TorrentEncryption);
            _useProxy.IsChecked = settings.TorrentUseProxy;
            foreach (var box in new[] { _dht, _pex, _lsd, _useProxy }) AutomationProperties.SetName(box, (string)box.Content);
            AutomationProperties.SetName(_encryption, "Encryption");
            foreach (var box in new[] { _seedsAtOnce, _perTorrent, _totalConnections })
            {
                // Left as typed, so Validate refuses a value out of range with the Host's words.
                box.ValidationMode = NumberBoxValidationMode.Disabled;
                AutomationProperties.SetName(box, (string)box.Header);
            }

            var privateHint = SettingsWindow.Hint("All three are always off for private torrents.");
            var encryptionHint = SettingsWindow.Hint("Requiring encryption leaves out peers that can't encrypt.");
            var limitsHint = SettingsWindow.Hint("Empty: no limit of their own. The total speed limit in Options › Connection covers torrents too.");
            _useProxy.Checked += (_, _) => ShowProxyNote();
            _useProxy.Unchecked += (_, _) => ShowProxyNote();
            // The Proxy page may have changed while this one was hidden.
            _useProxy.Loaded += (_, _) => ShowProxyNote();
            ShowProxyNote();
            dependent.AddRange([_dht, _pex, _lsd, privateHint, _encryption, encryptionHint, _useProxy, _seedsAtOnce, _perTorrent, _totalConnections, limitsHint]);
            peerControls.AddRange([SettingsWindow.Section("Finding peers and privacy"), _dht, _pex, _lsd, privateHint, _encryption, encryptionHint,
                _useProxy, _proxyNote.Text, SettingsWindow.Section("Limits"), _seedsAtOnce, _perTorrent, _totalConnections, limitsHint]);
        }
        TextBlock? handlerHelp = null;
        if (offerHandler)
        {
            const string handlerText = "Open magnet links and .torrent files with Inbound";
            _handler = new CheckBox { Content = handlerText, IsChecked = settings.TorrentHandler, Margin = new Thickness(28, 0, 0, 0) };
            AutomationProperties.SetName(_handler, handlerText);
            handlerHelp = SettingsWindow.Hint(OperatingSystem.IsWindows()
                ? "If another app still opens them, choose Inbound in Windows Settings › Apps › Default apps."
                : "If another app still opens them, choose Inbound in your desktop's default applications settings.");
            handlerHelp.Margin = new Thickness(28, 0, 0, 0);
            dependent.Add(_handler);
            dependent.Add(handlerHelp);
        }

        void Follow()
        {
            foreach (var control in dependent)
            {
                if (control is Control input) input.IsEnabled = _enabled.IsChecked == true;
                else control.Opacity = _enabled.IsChecked == true ? 1 : 0.5;
            }

            // The limits follow the seeding box too.
            _seedRatio.IsEnabled = _seedHours.IsEnabled = _enabled.IsChecked == true && _seed.IsChecked == true;
        }

        _enabled.Checked += (_, _) => { Follow(); RenderFirewallButtons(); };
        _enabled.Unchecked += (_, _) => { Follow(); RenderFirewallButtons(); };
        _seed.Checked += (_, _) => Follow();
        _seed.Unchecked += (_, _) => Follow();
        Follow();

        var controls = new List<UIElement> { _enabled, help };
        if (_route is not null) controls.Add(_route);
        controls.Add(_chooseFiles);
        controls.Add(multiFile);
        if (_handler is not null && handlerHelp is not null)
        {
            controls.Add(_handler);
            controls.Add(handlerHelp);
        }

        controls.Add(SettingsWindow.Section("Network"));
        controls.Add(_port);
        controls.Add(portHelp);
        controls.Add(_portHint.Text);
        controls.Add(_upnp);
        if (_allowFirewall is not null && _removeFirewall is not null && firewallHelp is not null)
        {
            controls.Add(_firewallStatus.Text);
            controls.Add(firewallHelp);
            controls.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _allowFirewall, _removeFirewall } });
        }

        controls.Add(SettingsWindow.Section("Seeding"));
        controls.Add(_seed);
        controls.Add(seedHelp);
        controls.Add(_seedRatio);
        controls.Add(_seedHours);
        controls.Add(_uploadLimit);
        controls.Add(uploadHelp);
        if (withControls) controls.AddRange(peerControls);
        else controls.Add(proxyHint);
        Controls = controls.ToArray();
        RenderFirewallButtons();
        _ = LoadAsync();
    }

    public UIElement[] Controls { get; }

    /// <summary>Why the page can't be saved, or null.</summary>
    public string? Validate()
    {
        var (ratio, hours) = SeedLimits();
        return TorrentPortInput.Problem(_port.Value)
            ?? TorrentSeedRules.Problem(ratio, hours, _uploadLimit.Value)
            ?? (_withControls ? ControlsProblem() : null);
    }

    /// <summary>T3: the Host's rules for the limits, and a proxy torrents can't use while "Use the proxy" is on.</summary>
    private string? ControlsProblem()
    {
        if (TorrentControlRules.LimitsProblem(TorrentControlRules.WholeOrZero(_seedsAtOnce.Value), TorrentControlRules.WholeOrZero(_perTorrent.Value),
                TorrentControlRules.WholeOrZero(_totalConnections.Value)) is { } limits) return limits;
        if (_useProxy.IsChecked != true || _proxy is null) return null;
        var (mode, address) = _proxy();
        return TorrentControlRules.ProxyProblem(mode, address);
    }

    private void ShowProxyNote()
    {
        if (!_withControls) return;
        var (mode, address) = _proxy?.Invoke() ?? ("none", null);
        var (words, problem) = TorrentControlRules.ProxyNote(_useProxy.IsChecked == true, mode, address);
        _proxyNote.Show(words, problem);
    }

    /// <summary>The ratio and hours to save. While seeding is off their boxes rest, and the saved ones are kept.</summary>
    private (double Ratio, double Hours) SeedLimits()
        => _seed.IsChecked == true
            ? (TorrentSeedRules.OrZero(_seedRatio.Value), TorrentSeedRules.OrZero(_seedHours.Value))
            : (_savedRatio, _savedHours);

    /// <summary>
    /// Audit L4: what the Host saved on Apply is the saved value from then on. Kept from when Options opened, a ratio
    /// changed and applied went back to the old one on the next OK with seeding turned off.
    /// </summary>
    public void Saved(SettingsDto saved)
    {
        _savedRatio = saved.TorrentSeedRatio;
        _savedHours = saved.TorrentSeedHours;
        _savedUploadLimit = saved.TorrentUploadLimit;
    }

    public void Close() => _closing.Cancel();

    /// <summary>The port hint and, on this PC, the firewall rule's state. Read once when the page opens.</summary>
    private async Task LoadAsync()
    {
        try
        {
            var network = await _client.GetTorrentNetworkAsync(_closing.Token);
            if (_closing.IsCancellationRequested) return;
            if (TorrentPortHint.Describe(network) is { } hint) _portHint.Show(hint, isProblem: (network.ListenError is not null && network.Port <= 0) || network.UnusablePort is > 0);
            if (_allowFirewall is not null) await ReadFirewallAsync(said: null);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }

    private async Task ReadFirewallAsync(string? said)
    {
        var worker = TorrentFirewall.WorkerPath();
        var problem = TorrentFirewall.WorkerPathProblem(worker);
        if (problem is not null || !File.Exists(worker))
        {
            _firewallStatus.Show(problem ?? "The torrent engine isn't in this app's folder, so the firewall can't be set up from here.", isProblem: true);
            if (_allowFirewall is not null) _allowFirewall.Visibility = Visibility.Collapsed;
            return;
        }

        var status = await TorrentFirewall.ReadAsync(worker);
        if (_closing.IsCancellationRequested) return;
        _hasOwnRules = status.HasOwnRules;
        var text = TorrentFirewall.Describe(status.State);
        var problemState = status.State is TorrentFirewallState.Blocked or TorrentFirewallState.Unknown;
        if (said is null) _firewallStatus.Show(text, problemState);
        else _firewallStatus.Say(said + " " + text, problemState);
        RenderFirewallButtons();
    }

    /// <summary>The firewall buttons rest while torrents are off on this page and while a change runs.</summary>
    private void RenderFirewallButtons()
    {
        var on = _enabled.IsChecked == true && !_busy;
        if (_allowFirewall is not null) _allowFirewall.IsEnabled = on;
        if (_removeFirewall is not null)
        {
            _removeFirewall.Visibility = _hasOwnRules ? Visibility.Visible : Visibility.Collapsed;
            // Removing stays possible with torrents off: the rule is the user's to take away at any time.
            _removeFirewall.IsEnabled = !_busy;
        }
    }

    /// <summary>Adds or removes the rule after one Windows permission prompt, then reads the rules again.</summary>
    private async Task ChangeFirewallAsync(bool allow)
    {
        if (_busy) return;
        FirewallCommand command;
        try
        {
            var worker = TorrentFirewall.WorkerPath();
            command = allow ? TorrentFirewall.BuildAllowCommand(worker) : TorrentFirewall.BuildRemoveCommand(worker);
        }
        catch (ArgumentException ex)
        {
            _firewallStatus.Say(ex.Message, isProblem: true);
            return;
        }

        _busy = true;
        RenderFirewallButtons();
        try
        {
            _firewallStatus.Say("Waiting for Windows to ask for permission…");
            var outcome = await WebUiFirewall.RunElevatedAsync(command, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            var said = outcome switch
            {
                FirewallRunOutcome.Declined => "Windows Firewall wasn't changed, because permission wasn't given.",
                FirewallRunOutcome.TimedOut => "Windows hasn't answered yet, and the change may still go through.",
                FirewallRunOutcome.Failed => allow ? "Windows Firewall couldn't be changed." : "The rule couldn't be removed.",
                _ => allow ? "Done." : "Removed.",
            };
            await ReadFirewallAsync(said);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        finally
        {
            _busy = false;
            if (!_closing.IsCancellationRequested) RenderFirewallButtons();
        }
    }

    /// <summary>Writes the page into the settings to save. Without an account the route stays as it was saved.</summary>
    public void ApplyTo(SettingsDto settings)
    {
        // Empty keeps the saved port (or lets the Host choose one when torrents are turned on).
        settings.TorrentListenPort = double.IsNaN(_port.Value) ? 0 : (int)Math.Round(_port.Value);
        settings.TorrentUpnp = _upnp.IsChecked == true;
        settings.TorrentSeedAfterDownload = _seed.IsChecked == true;
        // Empty is 0: no ratio limit, no time limit, or no upload limit of its own.
        (settings.TorrentSeedRatio, settings.TorrentSeedHours) = SeedLimits();
        settings.TorrentUploadLimit = TorrentSeedRules.UploadLimitToSave(_uploadLimit.Value, _savedUploadLimit);
        settings.TorrentsEnabled = _enabled.IsChecked == true;
        settings.TorrentChooseFiles = _chooseFiles.IsChecked == true;
        if (_handler is not null) settings.TorrentHandler = _handler.IsChecked == true;
        if (_route is { SelectedIndex: >= 0 and < 3 } route) settings.TorrentRoute = Routes[route.SelectedIndex];
        if (!_withControls) return;
        // T3. A Host without them keeps what is saved, which the window's copy carries through.
        settings.TorrentDht = _dht.IsChecked == true;
        settings.TorrentPex = _pex.IsChecked == true;
        settings.TorrentLsd = _lsd.IsChecked == true;
        settings.TorrentEncryption = TorrentControlRules.Encryption[Math.Clamp(_encryption.SelectedIndex, 0, TorrentControlRules.Encryption.Count - 1)].Value;
        settings.TorrentUseProxy = _useProxy.IsChecked == true;
        settings.TorrentSeedsAtOnce = TorrentControlRules.WholeOrZero(_seedsAtOnce.Value);
        settings.TorrentConnectionsPerTorrent = TorrentControlRules.WholeOrZero(_perTorrent.Value);
        settings.TorrentMaxConnections = TorrentControlRules.WholeOrZero(_totalConnections.Value);
    }

    private static class TorrentRoutesText
    {
        public const string Ask = "ask";
        public const string ThisPc = "thisPc";
        public const string Provider = "provider";
    }
}
