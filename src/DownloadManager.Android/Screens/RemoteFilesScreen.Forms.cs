using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Inbound.App.Screens;

/// <summary>
/// The add and edit forms (x18 to x25, x31): type picker, one form per kind with the footer band, save then test (x23b),
/// certificate trust (x20c). Field values live in <see cref="_fv"/> so a re-render (busy, error) never loses what was typed.
/// </summary>
internal sealed partial class RemoteFilesScreen
{
    private int _addStep;                 // 0 = type picker, 1 = form
    private SiteDto? _editSite;           // set when editing a saved location
    private string? _savedId;             // saved by this form but not yet confirmed to connect
    private readonly Dictionary<string, string> _fv = new(StringComparer.Ordinal);
    private bool _allowInsecure, _moreOpen, _ownIdOpen;

    internal const string OwnClientIdNote =
        "Optional: your own app registration (client ID and secret) from the provider's developer console, instead of rclone's shared one, which rclone says is being retired.";

    /// <summary>The desktop's rules for an own app registration (SitesRules.SignInRequest); null when it is fine.</summary>
    internal static string? ClientIdProblem(string clientId, string secret) =>
        clientId.Length > 256 || !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? "A client ID is letters, digits, dots, dashes and underscores."
            : clientId.Length == 0 && secret.Length > 0 ? "A client secret needs its client ID." : null;

    private const string NameRequired = "Enter a location name.";
    private const string ServerRequired = "Enter a server name or address.";
    private const string PortInvalid = "Enter a port from 1 to 65535.";
    private const string UserRequired = "Enter a user name.";
    private const string S3KeysRequired = "Enter the access key ID and secret access key.";
    private const string S3EndpointRequired = "Enter the S3 endpoint for this provider.";
    private const string B2KeysRequired = "Enter the application key ID and application key.";
    private const string InvalidEndpoint = "Enter an HTTP or HTTPS address without credentials, a query, or a fragment.";
    private const string HttpConsentRequired = "HTTP is not encrypted. Tick the warning to allow it.";
    private const string FtpConsentRequired = "FTP without encryption sends the password in the open. Tick the warning to allow it.";

    private static readonly (string Kind, string Title, string Subtitle, string Group)[] KindChoices =
    [
        (SiteKindCatalog.Drive, "Google Drive", "Sign in with your Google account", "Cloud storage"),
        (SiteKindCatalog.OneDrive, "OneDrive", "Sign in with your Microsoft account", "Cloud storage"),
        (SiteKindCatalog.Dropbox, "Dropbox", "Sign in with your Dropbox account", "Cloud storage"),
        (SiteKindCatalog.Sftp, "SFTP", "SSH file transfer, with a server key", "Servers"),
        (SiteKindCatalog.Ftp, "FTP or FTPS", "Classic file servers", "Servers"),
        (SiteKindCatalog.WebDav, "WebDAV", "Nextcloud, ownCloud and web folders", "Servers"),
        (SiteKindCatalog.S3, "S3", "Amazon S3 and compatible services", "Object storage"),
        (SiteKindCatalog.B2, "Backblaze B2", "With an application key", "Object storage"),
    ];

    private static string KindTitle(string kind) => kind switch
    {
        SiteKindCatalog.Ftp => "FTP",
        SiteKindCatalog.WebDav => "WebDAV",
        SiteKindCatalog.S3 => "S3",
        SiteKindCatalog.B2 => "Backblaze B2",
        SiteKindCatalog.Sftp => "SFTP",
        SiteKindCatalog.Drive => "Google Drive",
        SiteKindCatalog.OneDrive => "OneDrive",
        SiteKindCatalog.Dropbox => "Dropbox",
        _ => kind
    };

    private string V(string key) => _fv.TryGetValue(key, out var value) ? value : "";

    private void ClearFormSecrets()
    {
        foreach (var key in new[] { "password", "secret", "ownSecret" }) _fv.Remove(key);
    }

    private void BeginAddLocation()
    {
        if (_busy || _closed) return;
        _adding = true;
        _addStep = 0;
        _editSite = null;
        _savedId = null;
        _allowInsecure = _moreOpen = _ownIdOpen = false;
        _fv.Clear();
        _error = null;
        Render();
    }

    private void BeginEditLocation(SiteDto site)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        _adding = true;
        _addStep = 1;
        _editSite = site;
        _savedId = null;
        _addKind = site.Kind;
        _allowInsecure = site.Kind == SiteKindCatalog.WebDav && site.Url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
            || site.Kind == SiteKindCatalog.Ftp && site.Tls == SiteTlsModes.None;
        _moreOpen = !string.IsNullOrWhiteSpace(site.RemoteFolder);
        _ownIdOpen = !string.IsNullOrWhiteSpace(site.ClientId);
        _fv.Clear();
        _fv["name"] = site.Name;
        _fv["host"] = site.Host;
        _fv["port"] = site.Port > 0 ? site.Port.ToString() : "";
        _fv["user"] = site.User;
        _fv["folder"] = site.RemoteFolder;
        _fv["url"] = site.Url ?? "";
        _fv["endpoint"] = site.Endpoint ?? "";
        _fv["region"] = site.Region ?? "";
        _fv["provider"] = site.Provider ?? SiteKindCatalog.AwsProvider;
        _fv["tls"] = site.Tls ?? SiteTlsModes.Explicit;
        _fv["local"] = site.LocalFolder ?? "";
        _fv["ownId"] = site.ClientId ?? "";
        _error = null;
        Render();
    }

    /// <summary>Back, X or Cancel: a type form returns to the picker; anything else leaves the form.</summary>
    private void CloseForm()
    {
        if (_busy && _editSite is null)
        {
            // A save or test is running: drop it, so it cannot carry on after the form is gone.
            CancelCurrent();
            ClearPassword();
            _busy = false;
            _adding = false; _addStep = 0; _editSite = null; _savedId = null;
            _fv.Clear();
            _ = LoadLocationsAsync();
            return;
        }
        if (_editSite is null && _addStep == 1 && _savedId is null)
        {
            ClearFormSecrets();
            _addStep = 0;
            _error = null;
            Render();
            return;
        }
        bool saved = _savedId is not null;
        ClearPassword();
        _adding = false; _addStep = 0; _editSite = null; _savedId = null;
        _fv.Clear();
        CancelCurrent();
        if (saved) _ = LoadLocationsAsync(); else Render();
    }

    /// <summary>Footer Cancel (a5): leaves the add flow entirely, also from a type form.</summary>
    private void LeaveForm()
    {
        if (_editSite is null && _addStep == 1 && _savedId is null && !_busy) _addStep = 2;
        CloseForm();
    }

    private Task FinishFormLoad()
    {
        ClearPassword();
        _adding = false; _addStep = 0; _editSite = null; _savedId = null;
        _fv.Clear();
        _busy = false;
        return LoadLocationsAsync();
    }

    private void FinishForm()
    {
        ClearPassword();
        _adding = false; _addStep = 0; _editSite = null; _savedId = null;
        _fv.Clear();
        _busy = false;
        _ = LoadLocationsAsync();
    }

    private void RenderAddLocation()
    {
        _content.SetPadding(Dp(16), Dp(8), Dp(16), Dp(124));
        if (_editSite is null && _addStep == 0) RenderTypePicker(); else RenderForm();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // x18

    private void RenderTypePicker()
    {
        _content.AddView(FormBar("Add location", true, CloseForm));
        foreach (var group in KindChoices.GroupBy(choice => choice.Group))
        {
            var heading = FolderText(group.Key, 15, Resource.Attribute.colorPrimary);
            heading.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            heading.SetPadding(Dp(8), Dp(16), 0, Dp(8));
            _content.AddView(heading);
            var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            card.Background = Rounded(Attr(Resource.Attribute.colorSurfaceContainer), Dp(24));
            bool first = true;
            foreach (var (kind, title, subtitle, _) in group)
            {
                if (!first)
                {
                    var line = new View(_activity);
                    line.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorOutlineVariant)));
                    card.AddView(line, new LinearLayout.LayoutParams(-1, Dp(1)));
                }
                first = false;
                var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
                row.Background = new TypedValueHolder(_activity).SelectableBackground();
                row.AddView(LocationTile(LocationGlyph(kind)), new LinearLayout.LayoutParams(Dp(40), Dp(40)));
                var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
                texts.SetPadding(Dp(14), 0, Dp(4), 0);
                texts.AddView(FolderText(title, 17, Resource.Attribute.colorOnSurface));
                texts.AddView(FolderText(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
                row.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
                row.AddView(SheetIcon(Resource.Drawable.ic_chevron_right, Resource.Attribute.colorOnSurfaceVariant), new LinearLayout.LayoutParams(Dp(22), Dp(22)));
                row.ContentDescription = title + ". " + subtitle;
                var chosen = kind;
                row.Click += (_, _) =>
                {
                    if (!SameHost() || _busy) return;
                    _addKind = chosen;
                    _addStep = 1;
                    _fv.Clear();
                    _allowInsecure = _moreOpen = _ownIdOpen = false;
                    if (SiteKindCatalog.SignsIn(chosen)) _fv["name"] = KindTitle(chosen);
                    _fv["port"] = chosen == SiteKindCatalog.Ftp ? "21" : "22";
                    _fv["tls"] = SiteTlsModes.Explicit;
                    _fv["provider"] = SiteKindCatalog.AwsProvider;
                    _error = null;
                    Render();
                };
                card.AddView(row);
            }
            _content.AddView(card);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // x19 to x25, x31

    private View ExpanderHeader(string title, string subtitle, bool open, Action<bool> toggled, out LinearLayout body)
    {
        var holder = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(4), Dp(8), Dp(4), Dp(8));
        var texts = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        texts.AddView(FolderText(title, 18, Resource.Attribute.colorOnSurface));
        texts.AddView(FolderText(subtitle, 14, Resource.Attribute.colorOnSurfaceVariant));
        header.AddView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        var caret = SheetIcon(Resource.Drawable.ic_expand_more, Resource.Attribute.colorOnSurfaceVariant);
        caret.Rotation = open ? 180 : 0;
        header.AddView(caret, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        header.ContentDescription = title + ", " + subtitle;
        holder.AddView(header);
        body = new LinearLayout(_activity) { Orientation = Orientation.Vertical, Visibility = open ? ViewStates.Visible : ViewStates.Gone };
        holder.AddView(body);
        var bodyRef = body;
        header.Click += (_, _) =>
        {
            bool now = bodyRef.Visibility != ViewStates.Visible;
            bodyRef.Visibility = now ? ViewStates.Visible : ViewStates.Gone;
            caret.Rotation = now ? 180 : 0;
            toggled(now);
        };
        holder.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8), BottomMargin = Dp(4) };
        return holder;
    }

    private void RenderForm()
    {
        string kind = _addKind;
        bool edit = _editSite is not null || _savedId is not null;
        bool cloud = SiteKindCatalog.SignsIn(kind);
        bool secure = _secureHostConnection;
        var site = _editSite;
        string title = _editSite is not null ? "Edit " + _editSite.Name : KindTitle(kind) + " location";
        _content.AddView(FormBar(title, _editSite is not null, CloseForm));

        if (!secure)
            _content.AddView(Banner(BannerKind.Warn, "Connect to " + _hostName + " with HTTPS first",
                "Locations can't be added over an unencrypted or unverified Host connection, because the password would be sent with it.",
                Resource.Drawable.ic_remote_unlock, "Host connection", () =>
                    _activity.FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_settings));
        if (_error is not null) _content.AddView(Banner(BannerKind.Fail, _error, null, Resource.Drawable.ic_remote_alert));

        var layouts = new Dictionary<string, TextInputLayout>(StringComparer.Ordinal);
        var edits = new Dictionary<string, TextInputEditText>(StringComparer.Ordinal);
        TextInputLayout Add(LinearLayout parent, string key, string label, InputTypes type = InputTypes.ClassText, bool password = false,
            string? placeholder = null, Action<string>? changed = null, bool wrapBottom = true)
        {
            var (layout, edit1) = OutlinedField(label, V(key), type, password, value =>
            {
                _fv[key] = value;
                if (layout0(key) is { } l && l.Error is not null) SetFieldError(l, null);
                changed?.Invoke(value);
            }, placeholder);
            layout.Enabled = secure;
            layouts[key] = layout; edits[key] = edit1;
            parent.AddView(layout);
            return layout;
        }
        TextInputLayout? layout0(string key) => layouts.GetValueOrDefault(key);

        var name = Add(_content, "name", "Location name");

        MaterialButton? primary = null;
        // --- kind specific ---------------------------------------------------------------------------------------
        if (cloud)
        {
            string who = kind switch { SiteKindCatalog.Drive => "Google", SiteKindCatalog.OneDrive => "Microsoft", _ => "Dropbox" };
            _content.AddView(_phoneHost
                ? Banner(BannerKind.Info, "Sign in with your browser",
                    $"Add and sign in opens {who}'s sign-in page. This phone stores the authorization; your password stays with {who}.", Resource.Drawable.ic_remote_lock)
                : Banner(BannerKind.Info, "Sign in with a command",
                    $"{_hostName} stores the authorization. Add and sign in shows a command to run on a computer with a browser; then you paste its answer here.",
                    Resource.Drawable.ic_remote_lock));
            _content.AddView(ExpanderHeader("Use your own app registration", "Optional", _ownIdOpen, open => _ownIdOpen = open, out var own));
            var note = FolderText(OwnClientIdNote.Replace("Optional: your own app registration (client ID and secret)", "Your own client ID and secret"), 14,
                Resource.Attribute.colorOnSurfaceVariant);
            note.SetPadding(Dp(4), 0, Dp(4), Dp(10));
            own.AddView(note);
            Add(own, "ownId", "Your own client ID (optional)");
            Add(own, "ownSecret", "Its client secret (optional)", password: true,
                placeholder: site?.HasClientSecret == true ? "Saved; type to replace" : null);
        }
        else if (kind is SiteKindCatalog.Sftp or SiteKindCatalog.Ftp)
        {
            View? newAddressBanner = null;
            var hostRow = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            var hostHolder = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            var portHolder = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            Add(hostHolder, "host", "Server name or address", InputTypes.ClassText | InputTypes.TextVariationUri,
                changed: _ => UpdateKeyNote());
            Add(portHolder, "port", "Port", InputTypes.ClassNumber, changed: _ => UpdateKeyNote());
            hostRow.AddView(hostHolder, new LinearLayout.LayoutParams(0, -2, 2.3f) { RightMargin = Dp(12) });
            hostRow.AddView(portHolder, new LinearLayout.LayoutParams(0, -2, 1));
            _content.AddView(hostRow);
            void UpdateKeyNote()
            {
                if (newAddressBanner is null || site is null) return;
                bool changedAddress = !string.Equals(V("host").Trim(), site.Host, StringComparison.OrdinalIgnoreCase)
                    || V("port").Trim() != (site.Port > 0 ? site.Port.ToString() : "");
                newAddressBanner.Visibility = changedAddress ? ViewStates.Visible : ViewStates.Gone;
            }

            View? noneBanner = null;
            if (kind == SiteKindCatalog.Ftp)
            {
                var label = FolderText("Encryption", 14, Resource.Attribute.colorOnSurfaceVariant);
                label.SetPadding(Dp(4), Dp(2), 0, Dp(6));
                _content.AddView(label);
                var group = new MaterialButtonToggleGroup(_activity) { SingleSelection = true, SelectionRequired = true };
                var modes = new[] { (SiteTlsModes.Explicit, "FTPS"), (SiteTlsModes.Implicit, "Implicit FTPS"), (SiteTlsModes.None, "None") };
                int checkedId = 0;
                var ids = new Dictionary<int, string>();
                foreach (var (mode, text) in modes)
                {
                    var button = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle)
                    { Text = text, Id = View.GenerateViewId(), Enabled = secure };
                    button.SetAllCaps(false);
                    group.AddView(button, new LinearLayout.LayoutParams(0, Dp(56), 1));
                    ids[button.Id] = mode;
                    if (V("tls") == mode || V("tls").Length == 0 && mode == SiteTlsModes.Explicit) checkedId = button.Id;
                }
                if (checkedId != 0) group.Check(checkedId);
                group.ContentDescription = "Encryption";
                _content.AddView(group, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(12) });

                var tick = new CheckBox(_activity) { SaveEnabled = false, Checked = _allowInsecure, Enabled = secure, ContentDescription = "Allow FTP without encryption" };
                noneBanner = Banner(BannerKind.Warn, "Allow FTP without encryption", "Not encrypted. Credentials and files can be read or changed in transit.",
                    0, leading: tick);
                tick.CheckedChange += (_, e) => _allowInsecure = e.IsChecked;
                noneBanner.Visibility = V("tls") == SiteTlsModes.None ? ViewStates.Visible : ViewStates.Gone;
                _content.AddView(noneBanner);
                group.AddOnButtonCheckedListener(new ToggleListener((id, isChecked) =>
                {
                    if (!isChecked || !ids.TryGetValue(id, out var mode)) return;
                    string previous = V("tls");
                    _fv["tls"] = mode;
                    noneBanner.Visibility = mode == SiteTlsModes.None ? ViewStates.Visible : ViewStates.Gone;
                    if (mode != SiteTlsModes.None) { _allowInsecure = false; tick.Checked = false; }
                    // Implicit FTPS uses 990: change the port unless the user typed their own.
                    string port = V("port").Trim();
                    string? target = mode == SiteTlsModes.Implicit && port is "21" or "" ? "990"
                        : previous == SiteTlsModes.Implicit && mode != SiteTlsModes.Implicit && port == "990" ? "21" : null;
                    if (target is not null && edits.TryGetValue("port", out var portEdit)) portEdit.Text = target;
                }));
            }
            bool keyLogin = site?.AuthMethod == "key";
            bool sftpEdit = site is not null && kind == SiteKindCatalog.Sftp;
            Add(_content, "user", kind == SiteKindCatalog.Sftp ? "User name" : "User name (optional)");
            if (keyLogin)
            {
                _content.AddView(Hint("This location signs in with a private key, which is managed on the desktop. Its address can't be changed here."));
                edits["host"].Enabled = false;
                edits["port"].Enabled = false;
            }
            else
            {
                Add(_content, "password", sftpEdit ? "Password" : "Password (optional)", password: true,
                    placeholder: site?.HasPassword == true ? "Saved; type to replace" : null);
                if (!sftpEdit)
                    _content.AddView(Hint(_phoneHost ? "Credentials are stored by this phone's Host." : "Credentials are sent to " + _hostName + " and stored there."));
            }
            if (kind == SiteKindCatalog.Ftp)
            {
                Add(_content, "folder", "Starting folder (optional)", placeholder: "/");
                _content.AddView(Hint("Implicit FTPS uses port 990. If the server's certificate isn't from a known authority, you are asked to trust it after Add location."));
            }
            else
            {
                if (sftpEdit)
                {
                    Add(_content, "folder", "Starting folder (optional)", placeholder: "/");
                    newAddressBanner = Banner(BannerKind.Warn, "New address or port", "You are asked to trust the server key again when you save.",
                        Resource.Drawable.ic_settings_site_key);
                    newAddressBanner.Visibility = ViewStates.Gone;
                    _content.AddView(newAddressBanner);
                }
                else
                {
                    _content.AddView(ExpanderHeader("More options", "Starting folder", _moreOpen, open => _moreOpen = open, out var more));
                    Add(more, "folder", "Starting folder (optional)", placeholder: "/");
                }
            }
        }
        else if (kind == SiteKindCatalog.WebDav)
        {
            View? httpBanner = null;
            CheckBox? httpTick = null;
            var address = Add(_content, "url", "Address", InputTypes.ClassText | InputTypes.TextVariationUri, placeholder: "https://files.example.com/dav",
                changed: _ => { _allowInsecure = false; if (httpTick is not null) httpTick.Checked = false; UpdateHttpBanner(); });
            address.EndIconMode = TextInputLayout.EndIconCustom;
            address.SetEndIconDrawable(Resource.Drawable.ic_paste);
            address.EndIconContentDescription = "Paste address";
            address.SetEndIconOnClickListener(new ClickAction(() =>
            {
                try
                {
                    if (_activity.GetSystemService(Android.Content.Context.ClipboardService) is Android.Content.ClipboardManager { PrimaryClip: { ItemCount: > 0 } clip }
                        && clip.GetItemAt(0)?.Text is { } pasted)
                        edits["url"].Text = pasted;
                }
                catch (Exception) { /* Nothing to paste. */ }
            }));
            var tick = new CheckBox(_activity) { SaveEnabled = false, Checked = _allowInsecure, Enabled = secure, ContentDescription = "Allow HTTP" };
            httpBanner = Banner(BannerKind.Warn, "Allow HTTP", "Not encrypted. Credentials and files can be read or changed in transit.", 0, leading: tick);
            httpTick = tick;
            tick.CheckedChange += (_, e) =>
            {
                _allowInsecure = e.IsChecked;
                if (e.IsChecked && layouts["url"].Error == HttpConsentRequired) SetFieldError(layouts["url"], null);
            };
            void UpdateHttpBanner() { if (httpBanner is not null) httpBanner.Visibility = V("url").Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? ViewStates.Visible : ViewStates.Gone; }
            UpdateHttpBanner();
            tick.Checked = _allowInsecure;
            _content.AddView(httpBanner);
            Add(_content, "user", "User name (optional)");
            Add(_content, "password", "Password (optional)", password: true, placeholder: site?.HasPassword == true ? "Saved; type to replace" : null);
            _content.AddView(Hint(_phoneHost ? "Credentials are stored by this phone's Host." : "Credentials are sent to " + _hostName + " and stored there."));
        }
        else if (kind == SiteKindCatalog.S3)
        {
            var providers = SiteKindCatalog.Find(SiteKindCatalog.S3)!.Choices[SiteFields.Provider];
            var providerField = OutlinedField("Provider", providers.FirstOrDefault(p => p.Value == V("provider"))?.Title ?? providers[0].Title, InputTypes.Null);
            providerField.Edit.Focusable = false;
            providerField.Edit.Clickable = true;
            providerField.Edit.Enabled = secure;
            providerField.Layout.EndIconMode = TextInputLayout.EndIconCustom;
            providerField.Layout.SetEndIconDrawable(Resource.Drawable.ic_remote_caret);
            providerField.Layout.EndIconContentDescription = "Choose provider";
            void ChooseProvider()
            {
                if (_confirm is not null) return;
                _confirm = new MaterialAlertDialogBuilder(_activity).SetTitle("S3 provider")!
                    .SetItems(providers.Select(p => p.Title).ToArray(), (_, e) =>
                    {
                        _fv["provider"] = providers[e.Which].Value;
                        providerField.Edit.Text = providers[e.Which].Title;
                        if (layouts.TryGetValue("endpoint", out var ep) && ep.Error == S3EndpointRequired && providers[e.Which].Value == SiteKindCatalog.AwsProvider) SetFieldError(ep, null);
                    })!.Create();
                _confirm.DismissEvent += (_, _) => _confirm = null;
                _confirm.Show();
            }
            providerField.Edit.Click += (_, _) => ChooseProvider();
            providerField.Layout.SetEndIconOnClickListener(new ClickAction(ChooseProvider));
            _content.AddView(providerField.Layout);
            Add(_content, "endpoint", "Endpoint", InputTypes.ClassText | InputTypes.TextVariationUri, placeholder: "Optional for Amazon S3");
            Add(_content, "region", "Region (optional)");
            Add(_content, "user", "Access key ID");
            Add(_content, "password", "Secret access key", password: true, placeholder: site?.HasPassword == true ? "Saved; type to replace" : null);
            Add(_content, "folder", "Starting folder (optional)", placeholder: "bucket/folder");
        }
        else if (kind == SiteKindCatalog.B2)
        {
            Add(_content, "user", "Application key ID");
            Add(_content, "password", "Application key", password: true, placeholder: site?.HasPassword == true ? "Saved; type to replace" : null);
            Add(_content, "folder", "Starting folder (optional)", placeholder: "bucket/folder");
            _content.AddView(Hint("Create the key in your Backblaze account under Application Keys. A key limited to one bucket works."));
        }

        if (!_phoneHost)
        {
            Add(_content, "local", "Download folder on " + _hostName + " (optional)", placeholder: "D:\\Downloads");
            if (kind is SiteKindCatalog.S3 or SiteKindCatalog.B2)
                _content.AddView(Hint("Keys are sent to " + _hostName + " and stored there."));
        }

        if (site is not null)
        {
            var remove = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            remove.SetGravity(GravityFlags.CenterVertical);
            remove.SetPadding(Dp(8), 0, Dp(8), 0);
            remove.Background = new TypedValueHolder(_activity).SelectableBackground();
            remove.AddView(SheetIcon(Resource.Drawable.ic_remote_trash, Resource.Attribute.colorError), new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(16) });
            remove.AddView(FolderText("Remove location", 18, Resource.Attribute.colorError));
            remove.ContentDescription = "Remove location";
            remove.Click += (_, _) => ConfirmRemove(site, fromForm: true);
            _content.AddView(remove, new LinearLayout.LayoutParams(-1, Dp(56)) { TopMargin = Dp(16) });
        }

        string primaryText = _busy ? _savedId is not null ? "Checking…" : "Saving…" : edit && _editSite is not null ? "Save" : cloud ? "Add and sign in" : "Add location";
        int primaryIcon = cloud && _editSite is null ? Resource.Drawable.ic_open : Resource.Drawable.ic_remote_check;
        primary = FormFooter(primaryText, primaryIcon, LeaveForm, () => SubmitForm(layouts), secure && !_busy);
        primary.ContentDescription = primaryText;
        if (_editSite is null && _savedId is null) name.RequestFocus();
    }

    private TextView Hint(string text)
    {
        var view = FolderText(text, 13, Resource.Attribute.colorOnSurfaceVariant);
        view.SetPadding(Dp(4), Dp(2), Dp(4), Dp(10));
        return view;
    }

    private sealed class ClickAction(Action action) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => action();
    }

    private sealed class ToggleListener(Action<int, bool> action) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked) => action(checkedId, isChecked);
    }

    private void SubmitForm(Dictionary<string, TextInputLayout> layouts)
    {
        if (!SameHost() || _busy) return;
        if (!_secureHostConnection) { _error = "Adding a location requires a secure Host connection."; Render(); return; }
        string kind = _addKind;
        bool cloud = SiteKindCatalog.SignsIn(kind);
        bool failed = false;
        void Fail(string key, string message)
        {
            if (layouts.TryGetValue(key, out var layout)) { SetFieldError(layout, message); if (!failed) layout.RequestFocus(); }
            failed = true;
        }
        string name = V("name").Trim();
        if (name.Length == 0) Fail("name", NameRequired);
        int port = kind == SiteKindCatalog.Ftp ? 21 : 22;
        string host = V("host").Trim();
        if (kind is SiteKindCatalog.Sftp or SiteKindCatalog.Ftp)
        {
            if (host.Length == 0) Fail("host", ServerRequired);
            if (!int.TryParse(V("port").Trim(), out port) || port is < 1 or > 65535) Fail("port", PortInvalid);
            if (kind == SiteKindCatalog.Sftp && V("user").Trim().Length == 0) Fail("user", UserRequired);
            if (kind == SiteKindCatalog.Ftp && V("tls") == SiteTlsModes.None && !_allowInsecure)
            {
                failed = true;
                _activity.ShowMessage(FtpConsentRequired);
            }
        }
        string url = V("url").Trim();
        if (kind == SiteKindCatalog.WebDav)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                Fail("url", InvalidEndpoint);
            else if (uri.Scheme == Uri.UriSchemeHttp && !_allowInsecure) Fail("url", HttpConsentRequired);
        }
        string provider = V("provider").Length == 0 ? SiteKindCatalog.AwsProvider : V("provider");
        string endpoint = V("endpoint").Trim();
        bool hasPassword = V("password").Length > 0 || _editSite?.HasPassword == true;
        if (kind == SiteKindCatalog.S3)
        {
            if (V("user").Trim().Length == 0 || !hasPassword) Fail("user", S3KeysRequired);
            if (provider != SiteKindCatalog.AwsProvider && endpoint.Length == 0) Fail("endpoint", S3EndpointRequired);
        }
        if (kind == SiteKindCatalog.B2 && (V("user").Trim().Length == 0 || !hasPassword)) Fail("user", B2KeysRequired);
        string ownId = V("ownId").Trim(), ownSecret = V("ownSecret");
        if (cloud && ClientIdProblem(ownId, ownSecret) is { } clientProblem) Fail("ownId", clientProblem);
        if (_editSite is { HasPassword: true } stored && stored.AuthMethod != "key" && V("password").Length == 0 && !SiteKindCatalog.SignsIn(kind))
        {
            bool moved = kind switch
            {
                SiteKindCatalog.Sftp or SiteKindCatalog.Ftp => !string.Equals(stored.Host, host, StringComparison.OrdinalIgnoreCase) || stored.Port != port,
                SiteKindCatalog.WebDav => !string.Equals(stored.Url ?? "", url, StringComparison.OrdinalIgnoreCase),
                SiteKindCatalog.S3 => !string.Equals(stored.Endpoint ?? "", endpoint, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (moved) Fail("password", "Enter the password again: it isn't kept for a new address.");
        }
        if (failed) return;

        var edit = _editSite;
        var request = new SiteSaveRequest
        {
            Id = edit?.Id ?? _savedId, Name = name, Kind = kind,
            AuthMethod = edit?.AuthMethod ?? "password",
            Host = host, Port = port, RemoteFolder = V("folder").Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(V("local")) ? null : V("local").Trim(),
            User = V("user").Trim(),
            Password = string.IsNullOrEmpty(V("password")) ? null : V("password"),
            Tls = kind == SiteKindCatalog.Ftp ? V("tls").Length == 0 ? SiteTlsModes.Explicit : V("tls") : null,
            Url = kind == SiteKindCatalog.WebDav ? url : null,
            Vendor = kind == SiteKindCatalog.WebDav ? edit?.Vendor ?? "other" : null,
            Provider = kind == SiteKindCatalog.S3 ? provider : null,
            Endpoint = kind == SiteKindCatalog.S3 && endpoint.Length > 0 ? endpoint : null,
            Region = kind == SiteKindCatalog.S3 && V("region").Trim().Length > 0 ? V("region").Trim() : null,
            ClientId = cloud && ownId.Length > 0 ? ownId : null,
            ClientSecret = !cloud ? null : ownSecret.Length > 0 ? ownSecret : ownId.Length == 0 && (edit?.HasClientSecret == true) ? "" : null,
        };
        if (edit is not null && cloud) request.AuthMethod = "signIn";
        bool addressChanged = edit is not null && kind == SiteKindCatalog.Sftp
            && (!string.Equals(edit.Host, host, StringComparison.OrdinalIgnoreCase) || edit.Port != port);
        ClearFormSecrets();
        _ = SaveFormAsync(request, addressChanged);
    }

    private async Task SaveFormAsync(SiteSaveRequest request, bool addressChanged)
    {
        if (!SameHost() || _busy || !_secureHostConnection) { request.Password = null; request.ClientSecret = null; return; }
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        SiteDto? saved = null;
        try
        {
            var result = await _client.SaveAsync(request, _life.Token);
            if (_closed || generation != _generation) return;
            if (!result.Ok) _error = result.Message ?? "The Host could not save this location. Check the details and try again.";
            else saved = result.Site;
            if (result.Ok && saved is null)
            {
                var list = await _client.ListAsync(_life.Token);
                saved = list.Sites.FirstOrDefault(s => s.Id == request.Id) ?? list.Sites.FirstOrDefault(s => s.Name == request.Name && s.Kind == request.Kind);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (!_closed && generation == _generation) _error = "Could not confirm the save. Refresh the location list before trying again.";
        }
        finally
        {
            request.Password = null;
            request.ClientSecret = null;
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
        if (saved is null || !SameHost() || generation != _generation) return;
        _savedId = saved.Id;
        string kind = saved.Kind;
        bool editing = _editSite is not null;
        if (SiteKindCatalog.SignsIn(kind))
        {
            await FinishFormLoad();
            if (!editing && SameHost())
            {
                if (FreshSite(saved.Id) is { } fresh) _activity.RunOnUiThread(() => { if (SameHost()) BeginCloudSignIn(fresh); });
                else _activity.ShowMessage("Saved. Open the location to sign in.");
            }
            return;
        }
        if (kind == SiteKindCatalog.Sftp)
        {
            if (editing && !addressChanged && saved.HostKeys.Count > 0) { FinishForm(); return; }
            await ReviewSftpKeyAsync(saved, FinishForm);
            return;
        }
        await TestFormLocationAsync(saved, false);
    }

    private async Task TestFormLocationAsync(SiteDto saved, bool afterTrust)
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = true; _error = null;
        Render();
        SiteResultDto? result = null;
        string? failure = null;
        try { result = await _client.TestAsync(saved.Id, _life.Token); }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { failure = "Could not reach the Host to test the connection."; }
        finally { if (!_closed && generation == _generation) { _busy = false; Render(); } }
        if (!SameHost() || generation != _generation) return;
        if (result is { Ok: true }) { FinishForm(); return; }
        if (result?.Problem == SiteProblems.CertificateNotTrusted && !afterTrust) { await ShowCertificateTrustAsync(saved); return; }
        ShowTestFailed(saved, failure ?? result?.Message ?? "It did not answer.");
    }

    /// <summary>x23b Edit: the form stays open as an edit of the location the Host already saved.</summary>
    private void EditSavedAfterFailure(SiteDto saved)
    {
        if (!SameHost()) return;
        _editSite = saved;
        _savedId = saved.Id;
        _error = null;
        Render();
    }

    private void ShowTestFailed(SiteDto saved, string message)
    {
        if (!SameHost() || _confirm is not null) return;
        string detail = message.Length > 0 ? char.ToLowerInvariant(message[0]) + message[1..] : message;
        detail = detail.TrimEnd('.');
        var dialog = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Saved, but it did not connect")!
            .SetMessage($"{saved.Name} is saved. The Host said: {detail}.\n\nEdit the location to fix it, or keep it and try later.")!
            .SetPositiveButton("Edit", (_, _) => EditSavedAfterFailure(saved))!
            .SetNegativeButton("Keep anyway", (_, _) => FinishForm())!
            .Create();
        _confirm = dialog;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
        dialog.CancelEvent += (_, _) => EditSavedAfterFailure(saved);
        dialog.Show();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // x20c

    private async Task ShowCertificateTrustAsync(SiteDto saved)
    {
        if (!SameHost() || _busy || _confirm is not null) return;
        int generation = ++_generation;
        _busy = true;
        Render();
        SiteCertificateResultDto? read = null;
        try { read = await _client.ReadCertificateAsync(saved.Id, _life.Token); }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { /* Shown as a failure below. */ }
        finally { if (!_closed && generation == _generation) { _busy = false; Render(); } }
        if (!SameHost() || generation != _generation) return;
        if (read is not { Ok: true, Certificate: { } cert })
        {
            ShowTestFailed(saved, read?.Message ?? "Could not read the server's certificate.");
            return;
        }
        if (read.TrustedBySite || cert.TrustedBySystem) { await TestFormLocationAsync(saved, true); return; }
        if (cert.CannotTrust is { } cannot) { ShowTestFailed(saved, cannot); return; }

        var dialog = NewSheet(out var column);
        SheetTitle(column, "Trust this server certificate?",
            $"{saved.Name} · {(string.IsNullOrEmpty(saved.Host) ? HostOfUrl(saved.Url) : saved.Host + (saved.Port > 0 ? ":" + saved.Port : ""))}",
            _phoneHost ? "On this phone" : "On " + _hostName);
        var warning = cert.SelfSigned
            ? Banner(BannerKind.Warn, "Self-signed certificate", "No known authority vouches for it. Compare the fingerprint with the one from the server's administrator.", Resource.Drawable.ic_remote_alert)
            : Banner(BannerKind.Warn, "Not from a known authority", "Compare the fingerprint with the one from the server's administrator before you trust it.", Resource.Drawable.ic_remote_alert);
        AddInset(column, warning);
        var details = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        details.Background = Rounded(Attr(Resource.Attribute.colorSurfaceContainerHighest), Dp(20));
        details.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        void Row(string label, string value)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.AddView(FolderText(label, 16, Resource.Attribute.colorOnSurfaceVariant), new LinearLayout.LayoutParams(Dp(104), -2));
            row.AddView(FolderText(value, 16, Resource.Attribute.colorOnSurface), new LinearLayout.LayoutParams(0, -2, 1));
            row.SetPadding(0, Dp(5), 0, Dp(5));
            details.AddView(row);
        }
        Row("Issued to", cert.Subject);
        Row("Issued by", cert.Issuer);
        Row("Valid until", cert.NotAfter.ToLocalTime().ToString("d MMM yyyy"));
        AddInset(column, details);
        AddInset(column, CopyBox(cert.Fingerprint, "Copy fingerprint", "SHA-256"));
        SheetButtons(column, "Trust certificate", Resource.Drawable.ic_remote_lock, () => _ = TrustCertificateAsync(saved, cert.Fingerprint));
        ShowSheet(dialog);
    }

    private static string HostOfUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Authority : "";

    private async Task TrustCertificateAsync(SiteDto saved, string fingerprint)
    {
        if (!SameHost() || _busy) return;
        int generation = ++_generation;
        _busy = true;
        Render();
        SiteResultDto? result = null;
        string? failure = null;
        try { result = await _client.TrustCertificateAsync(saved.Id, fingerprint, _life.Token); }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { failure = "Could not confirm whether the Host trusted the certificate."; }
        finally { if (!_closed && generation == _generation) { _busy = false; Render(); } }
        if (!SameHost() || generation != _generation) return;
        if (result is not { Ok: true }) { ShowTestFailed(saved, failure ?? result?.Message ?? "The Host did not accept the certificate."); return; }
        await TestFormLocationAsync(saved, true);
    }
}
