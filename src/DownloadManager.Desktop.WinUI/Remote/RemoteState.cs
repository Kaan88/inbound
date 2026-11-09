using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// What the Host has said about remote work and archive sets. <see cref="RemoteClient"/> changes it
/// only through the function it was given for reaching the UI thread, so everything here is safe to
/// bind to. Remote jobs live here and not in the download list: they aren't local transfers.
/// </summary>
internal sealed class RemoteState : INotifyPropertyChanged
{
    // A list is read on the Host before its reply is sent, so an event can arrive with newer news
    // than the list that follows it. These say what happened since the list was asked for.
    private readonly HashSet<Guid> _jobsTouched = [];
    private readonly HashSet<Guid> _jobsGone = [];
    private readonly HashSet<Guid> _setsTouched = [];
    private readonly HashSet<Guid> _setsGone = [];

    private bool _isConnected;
    private bool _remoteSupported;
    private bool _archivesSupported;
    private bool _uploadsOffered;
    private bool _isReady;
    private bool _accountsKnown;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The account with this provider id changed, by a reply or from the Host.</summary>
    public event Action<RemoteAccountDto>? AccountChanged;

    /// <summary>A key was saved through this client, maybe another account's. Null when no provider was named.</summary>
    public event Action<string?>? KeySaved;

    internal void RaiseKeySaved(string? provider) => KeySaved?.Invoke(provider);

    public ObservableCollection<RemoteAccountDto> Accounts { get; } = [];
    public ObservableCollection<RemoteJobDto> Jobs { get; } = [];
    public ObservableCollection<ArchiveSetDto> ArchiveSets { get; } = [];

    public bool IsConnected { get => _isConnected; internal set => Set(ref _isConnected, value); }

    /// <summary>The Host offers <c>remote.*</c> and has a provider. Every remote entry point hides when this is false.</summary>
    public bool RemoteSupported { get => _remoteSupported; internal set => Set(ref _remoteSupported, value); }

    /// <summary>The Host answers <c>archive.*</c>. It doesn't list this in its hello, so it is found out by asking.</summary>
    public bool ArchivesSupported { get => _archivesSupported; internal set => Set(ref _archivesSupported, value); }

    /// <summary>
    /// Another server's hello lists <c>uploads</c>: it reads the <c>.torrent</c> and container files this window sends it
    /// (<c>POST /api/upload/...</c>). False for this PC's Host, which reads this PC's files by their path.
    /// </summary>
    public bool UploadsOffered { get => _uploadsOffered; internal set => Set(ref _uploadsOffered, value); }

    /// <summary>The first connection has been read, so the two above can be trusted.</summary>
    public bool IsReady { get => _isReady; internal set => Set(ref _isReady, value); }

    /// <summary>The last whole read of the accounts worked, or the Host has no remote support at all. Until then, an empty list says nothing about keys.</summary>
    public bool AccountsKnown { get => _accountsKnown; internal set => Set(ref _accountsKnown, value); }

    public RemoteAccountDto? FindAccount(string? provider)
        => Accounts.FirstOrDefault(account => string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether any provider can do this: account, catalog, torrents, webDownloads, usenet, cacheCheck, hosterList.</summary>
    public bool HasCapability(string capability)
        => Accounts.Any(account => account.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase));

    internal void ApplyAccounts(IEnumerable<RemoteAccountDto> accounts, bool wholeList = false)
    {
        var received = accounts.ToList();
        if (wholeList)
        {
            for (var i = Accounts.Count - 1; i >= 0; i--)
            {
                if (!received.Any(account => SameProvider(account, Accounts[i]))) Accounts.RemoveAt(i);
            }
        }

        foreach (var account in received)
        {
            var index = IndexOf(Accounts, existing => SameProvider(existing, account));
            if (index < 0) Accounts.Add(account);
            else Accounts[index] = account;
            AccountChanged?.Invoke(account);
        }
    }

    internal void ClearRemote()
    {
        Accounts.Clear();
        Jobs.Clear();
        _jobsTouched.Clear();
        _jobsGone.Clear();
    }

    internal void Apply(RemoteChangedDto change)
    {
        switch (change.Change)
        {
            case "job" when change.Job is { } job:
                ApplyJobEvent(job);
                break;
            case "removed" when change.JobId is { } id:
                RemoveJob(id);
                break;
            case "account" when change.Account is { } account:
                ApplyAccounts([account]);
                break;
        }
    }

    internal void BeginJobList()
    {
        _jobsTouched.Clear();
        _jobsGone.Clear();
    }

    internal void ApplyJobList(IEnumerable<RemoteJobDto> jobs)
    {
        var listed = jobs.Where(job => !_jobsGone.Contains(job.Id) && !IsRemoved(job)).ToList();
        for (var i = Jobs.Count - 1; i >= 0; i--)
        {
            var id = Jobs[i].Id;
            if (!_jobsTouched.Contains(id) && !listed.Any(job => job.Id == id)) Jobs.RemoveAt(i);
        }

        foreach (var job in listed) ApplyJobReply(job);
    }

    /// <summary>The newest word on a job. Events reach this connection in order, so one always counts.</summary>
    private void ApplyJobEvent(RemoteJobDto job)
    {
        if (IsRemoved(job))
        {
            RemoveJob(job.Id);
            return;
        }

        _jobsTouched.Add(job.Id);
        _jobsGone.Remove(job.Id);
        var index = IndexOf(Jobs, existing => existing.Id == job.Id);
        if (index < 0) Jobs.Add(job);
        else Jobs[index] = job;
    }

    /// <summary>A reply can come from another connection, or was read before an event already here.</summary>
    internal void ApplyJobReply(RemoteJobDto job)
    {
        if (_jobsGone.Contains(job.Id)) return;
        if (IsRemoved(job))
        {
            RemoveJob(job.Id);
            return;
        }

        var index = IndexOf(Jobs, existing => existing.Id == job.Id);
        if (index < 0) Jobs.Add(job);
        else if (Jobs[index].UpdatedAt <= job.UpdatedAt) Jobs[index] = job;
    }

    internal void RemoveJob(Guid id)
    {
        _jobsGone.Add(id);
        _jobsTouched.Remove(id);
        var index = IndexOf(Jobs, existing => existing.Id == id);
        if (index >= 0) Jobs.RemoveAt(index);
    }

    internal void Apply(ArchiveChangedDto change)
    {
        if (change.Set is { } set)
        {
            _setsTouched.Add(set.Id);
            _setsGone.Remove(set.Id);
            var index = IndexOf(ArchiveSets, existing => existing.Id == set.Id);
            if (index < 0) ArchiveSets.Add(set);
            else ArchiveSets[index] = set;
        }
        else if (change.RemovedId is { } id)
        {
            RemoveArchiveSet(id);
        }
    }

    internal void BeginArchiveList()
    {
        _setsTouched.Clear();
        _setsGone.Clear();
    }

    internal void ApplyArchiveList(IEnumerable<ArchiveSetDto> sets)
    {
        var listed = sets.Where(set => !_setsGone.Contains(set.Id)).ToList();
        for (var i = ArchiveSets.Count - 1; i >= 0; i--)
        {
            var id = ArchiveSets[i].Id;
            if (!_setsTouched.Contains(id) && !listed.Any(set => set.Id == id)) ArchiveSets.RemoveAt(i);
        }

        foreach (var set in listed) ApplyArchiveReply(set);
    }

    internal void ApplyArchiveReply(ArchiveSetDto set)
    {
        if (_setsGone.Contains(set.Id)) return;
        var index = IndexOf(ArchiveSets, existing => existing.Id == set.Id);
        if (index < 0) ArchiveSets.Add(set);
        else if (ArchiveSets[index].UpdatedAt <= set.UpdatedAt) ArchiveSets[index] = set;
    }

    internal void RemoveArchiveSet(Guid id)
    {
        _setsGone.Add(id);
        _setsTouched.Remove(id);
        var index = IndexOf(ArchiveSets, existing => existing.Id == id);
        if (index >= 0) ArchiveSets.RemoveAt(index);
    }

    private static bool IsRemoved(RemoteJobDto job) => string.Equals(job.State, "removed", StringComparison.OrdinalIgnoreCase);

    private static bool SameProvider(RemoteAccountDto left, RemoteAccountDto right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf<T>(IList<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (match(items[i])) return i;
        }

        return -1;
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
