using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>Where the address check stands on the New download screen.</summary>
public enum CheckState { Idle, Checking, Ready, Failed, Refused }

/// <summary>What to do when the file the download would save already exists.</summary>
public enum ConflictChoice { Rename, Overwrite, Fail }

/// <summary>
/// The New download screen: the address (or several, or a numbered range), what the Host says about it, and the choices that go with
/// the add: the file name, when to start, the number of connections, and under More options the referrer, a description, a checksum
/// and what to do when the file exists. Nothing here is sent until <see cref="SubmitAsync"/>.
/// </summary>
public sealed class NewDownloadModel
{
    private readonly DownloadsModel _host;
    // The address, the check and a found name change together: the check's result is applied on the thread its probe ended on,
    // while the user types on the UI thread (review of steps 3 and 6, finding 12).
    private readonly object _gate = new();
    private int _checkVersion;
    private string _address = "";
    private string _fileName = "";
    private bool _fileNameEdited;

    public NewDownloadModel(DownloadsModel host)
    {
        _host = host;
        Connections = Math.Clamp(host.Settings.DefaultConnections, 1, MaxConnections);
    }

    /// <summary>The folder chosen on a server (<see cref="FolderPicker"/>); null keeps the Host's own folder for new downloads.</summary>
    public string? Destination { get; set; }

    /// <summary>Where the download will go: the chosen folder, else the Host's folder for new downloads.</summary>
    public string SaveTo => Destination ?? _host.DefaultDownloadDirectory;

    /// <summary>The most connections one download may have, from the Host's settings (at least 1).</summary>
    public int MaxConnections => Math.Max(1, _host.Settings.MaxConnectionsPerHost);

    public int Connections { get; set; }

    public string? Referrer { get; set; }

    public string? Description { get; set; }

    public string? Sha256 { get; set; }

    public ConflictChoice IfExists { get; set; } = ConflictChoice.Rename;

    public bool StartLater { get; set; }

    public string Address
    {
        get => _address;
        set
        {
            value ??= "";
            lock (_gate)
            {
                if (_address == value) return;
                _address = value;
                _checkVersion++;
                Check = CheckState.Idle;
                CheckText = null;
                Urls = Expand(_address, out var err);
                AddressError = err;
                // A name the Host found belongs to the address it was found for; only the user's own name stays (finding 9).
                if (!_fileNameEdited) _fileName = "";
            }
        }
    }

    /// <summary>The addresses the text stands for: one, or the several a range such as <c>file[01-20].zip</c> or a list of lines expands to.</summary>
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();

    /// <summary>The text is not usable as an address (not http or https, or a range that does not parse); null when it is fine or empty.</summary>
    public string? AddressError { get; private set; }

    public CheckState Check { get; private set; }

    /// <summary>"6.1 GB" or the reason the check failed; null while idle.</summary>
    public string? CheckText { get; private set; }

    public bool IsMany => Urls.Count > 1;

    /// <summary>
    /// A name only makes sense for one file; the user's edit wins over what the Host found. Clearing the field gives the
    /// choice back to the Host's name.
    /// </summary>
    public string FileName
    {
        get { lock (_gate) return _fileName; }
        set { lock (_gate) { _fileName = value ?? ""; _fileNameEdited = _fileName.Length > 0; } }
    }

    /// <summary>The user typed the name (it is kept when the address changes); false for a name the Host found, or none.</summary>
    public bool FileNameEdited
    {
        get { lock (_gate) return _fileNameEdited; }
    }

    /// <summary>The name field is hidden for several downloads.</summary>
    public bool ShowFileName => Urls.Count == 1;

    public bool CanDownload => Urls.Count > 0 && AddressError is null && Check != CheckState.Checking && Check != CheckState.Refused
        && ChecksumError is null;

    public string? ChecksumError => string.IsNullOrWhiteSpace(Sha256) || IsSha256(Sha256.Trim()) ? null : "A SHA-256 checksum is 64 hexadecimal characters.";

    private static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    /// <summary>Expands the text: one address per line, and ranges in brackets. Only http and https; empty text gives none.</summary>
    public static IReadOnlyList<string> Expand(string? text, out string? error)
    {
        error = null;
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var line in text.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IReadOnlyList<string> one;
            try { one = BatchUrlExpander.Expand(line); }
            catch (FormatException e) { error = e.Message; return Array.Empty<string>(); }
            foreach (var u in one)
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    error = "Only http and https links can be downloaded here.";
                    return Array.Empty<string>();
                }

                result.Add(u);
            }

            if (result.Count > BatchUrlExpander.DefaultLimit) { error = "That is more than " + BatchUrlExpander.DefaultLimit + " links."; return Array.Empty<string>(); }
        }

        return result;
    }

    /// <summary>
    /// Asks the Host about the single address (name and size). Only the latest call counts: a result for an address that was changed
    /// meanwhile is dropped. Several addresses are not checked one by one.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        int version;
        string url;
        lock (_gate)
        {
            if (Urls.Count != 1 || AddressError is not null) return;
            version = ++_checkVersion;
            url = Urls[0];
            Check = CheckState.Checking;
            CheckText = null;
        }

        FileMetadataResult result;
        try { result = await _host.ProbeAsync(url, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { result = new FileMetadataResult { Ok = false, Error = e.Message }; }
        lock (_gate)
        {
            if (version != _checkVersion) return;
            if (result.AddRefused) { Check = CheckState.Refused; CheckText = result.Error ?? "This address can't be added."; return; }
            if (!result.Ok) { Check = CheckState.Failed; CheckText = Readable(result.Error); return; }
            Check = CheckState.Ready;
            CheckText = result.TotalBytes is > 0 ? Fmt.Bytes(result.TotalBytes.Value) : "Size not given by the server";
            if (!_fileNameEdited) _fileName = string.IsNullOrWhiteSpace(result.FileName) ? "" : result.FileName;
        }
    }

    /// <summary>
    /// A trimmed build can report a .NET resource key ("net_http_client_execution_error") instead of the sentence; that is no text for a person.
    /// </summary>
    public static string Readable(string? error) =>
        string.IsNullOrWhiteSpace(error) || System.Text.RegularExpressions.Regex.IsMatch(error.Trim(), "^[a-z0-9_]+$")
            ? "The address could not be reached."
            : error;

    /// <summary>How many were added, how many were already there, and the first thing that went wrong, if anything.</summary>
    public sealed record SubmitResult(int Added, int Duplicates, string? Error);

    public IReadOnlyList<AddJobRequest> BuildRequests()
    {
        var list = new List<AddJobRequest>();
        string fileName;
        lock (_gate) fileName = _fileName;
        foreach (var u in Urls)
            list.Add(new AddJobRequest
            {
                Url = u,
                DestinationDirectory = string.IsNullOrWhiteSpace(Destination) ? null : Destination,
                FileName = Urls.Count == 1 && !string.IsNullOrWhiteSpace(fileName) ? fileName.Trim() : null,
                StartMode = StartLater ? StartModes.Later : StartModes.StartNow,
                Connections = Connections,
                Referrer = string.IsNullOrWhiteSpace(Referrer) ? null : Referrer.Trim(),
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                ExpectedSha256 = Urls.Count == 1 && !string.IsNullOrWhiteSpace(Sha256) ? Sha256.Trim() : null,
                ConflictPolicy = IfExists switch { ConflictChoice.Overwrite => "overwrite", ConflictChoice.Fail => "fail", _ => "rename" },
            });
        return list;
    }

    /// <summary>Adds every address, in order; stops at the first one the Host refuses (the rest are not sent) and says why.</summary>
    public async Task<SubmitResult> SubmitAsync(CancellationToken cancellationToken = default)
    {
        int added = 0, duplicates = 0;
        foreach (var request in BuildRequests())
        {
            AddJobResponse r;
            try { r = await _host.AddAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new SubmitResult(added, duplicates, "The download was not added: " + e.Message); }
            if (!r.Accepted) return new SubmitResult(added, duplicates, r.Error ?? "The download was not added.");
            if (r.Duplicate) duplicates++; else added++;
        }

        return new SubmitResult(added, duplicates, null);
    }

    /// <summary>"Download added" / "Added 20 downloads" / "Already in your downloads" for a snackbar.</summary>
    public static string Describe(SubmitResult r) =>
        r.Added == 0 && r.Duplicates > 0 ? (r.Duplicates == 1 ? "Already in your downloads" : $"{r.Duplicates} were already in your downloads")
        : r.Added == 1 && r.Duplicates == 0 ? "Download added"
        : $"Added {r.Added} downloads" + (r.Duplicates > 0 ? $" ({r.Duplicates} already there)" : "");
}
