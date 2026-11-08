using System.Text.Json;
using DownloadManager.Containers;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>
/// The <c>remote.*</c> messages, from request to reply. This is the one place provider state turns
/// into what a client sees, so it is also where everything is scrubbed once more: words that came
/// from a provider or an input pass through <see cref="Safe"/> on their way out, whatever earlier
/// steps did. The key only ever goes in, and no reply has a field that could hold a link. Holds no
/// gate: each call either reads what is in memory or waits on the network with nothing locked.
/// </summary>
public sealed class RemoteCommands : IDisposable
{
    private const int DefaultPageSize = 50;
    private const int LargestPageSize = 250;

    private static readonly RemoteCapabilities[] AllCapabilities =
        Enum.GetValues<RemoteCapabilities>().Where(capability => capability != RemoteCapabilities.None).ToArray();

    private readonly RemoteProviderRegistry _providers;
    private readonly RemoteJobCoordinator _jobs;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, LastAnswer> _answers = new(StringComparer.Ordinal);
    private readonly List<(RemoteAccountStore Store, EventHandler Handler)> _accountHandlers = [];
    private readonly ContainerImportService _containers;
    private readonly PathRoots? _jobRoots;

    private sealed record LastAnswer(RemoteAccountInfo Info, DateTimeOffset At);

    /// <param name="roots">On a server, the folders whose files a client may have it read (container files). Null on the desktop.</param>
    /// <param name="jobRoots">
    /// Where these callers may have the coordinator read a .torrent file and save downloads, passed with each call
    /// because the coordinator is shared: a browser on another device (D8). Null leaves it to the coordinator's own.
    /// </param>
    public RemoteCommands(RemoteProviderRegistry providers, RemoteJobCoordinator jobs, TimeProvider? timeProvider = null, Action<string>? log = null,
        ContainerImportService? containers = null, PathRoots? roots = null, PathRoots? jobRoots = null)
    {
        _providers = providers;
        _jobs = jobs;
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _jobRoots = jobRoots;
        _containers = containers ?? new ContainerImportService(providers, jobs, timeProvider: _time, log: log, roots: roots, jobRoots: jobRoots);

        _jobs.Changed += OnJobChanged;
        _jobs.Removed += OnJobRemoved;
        foreach (var entry in _providers.All)
        {
            if (entry.Account is { } store)
            {
                EventHandler handler = (_, _) => OnAccountChanged(entry);
                store.Changed += handler;
                _accountHandlers.Add((store, handler));
            }
        }
    }

    /// <summary>Something a client should hear about. Raised outside any gate, after the change is saved.</summary>
    public event EventHandler<RemoteChangedDto>? Changed;

    public void Dispose()
    {
        _jobs.Changed -= OnJobChanged;
        _jobs.Removed -= OnJobRemoved;
        foreach (var (store, handler) in _accountHandlers)
        {
            store.Changed -= handler;
        }

        _accountHandlers.Clear();
        _containers.Dispose();
    }

    private void OnJobChanged(object? sender, RemoteJobView view)
        => Changed?.Invoke(this, new RemoteChangedDto { Change = "job", Job = ToDto(view) });

    private void OnJobRemoved(object? sender, Guid id)
        => Changed?.Invoke(this, new RemoteChangedDto { Change = "removed", JobId = id });

    private void OnAccountChanged(RemoteProviderEntry entry)
    {
        // What the provider said about the old key says nothing about this one.
        lock (_sync) _answers.Remove(entry.Id);
        try
        {
            Changed?.Invoke(this, new RemoteChangedDto { Change = "account", Account = ToDto(entry) });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"A listener failed after the {entry.Id} account changed: {ex.GetType().Name}.");
        }
    }

    // ---- Account ------------------------------------------------------------------------------

    public async Task<RemoteAccountResult> GetAccountAsync(RemoteAccountRequest request, CancellationToken cancellationToken = default)
    {
        var entries = new List<RemoteProviderEntry>();
        if (request.Provider is { } id)
        {
            if (_providers.Find(id) is not { } found)
            {
                return new RemoteAccountResult { Error = NoSuchProvider(id) };
            }

            entries.Add(found);
        }
        else
        {
            entries.AddRange(_providers.All);
        }

        string? error = null;
        string? failureKind = null;
        if (request.Refresh)
        {
            foreach (var entry in entries.Where(entry => entry.AccountProblem() is null))
            {
                var (_, problem, kind) = await AskAccountAsync(entry, cancellationToken).ConfigureAwait(false);
                error ??= problem;
                failureKind ??= kind;
            }
        }

        return new RemoteAccountResult
        {
            Ok = error is null,
            Error = error,
            FailureKind = failureKind,
            Accounts = entries.Select(ToDto).ToList(),
        };
    }

    public async Task<RemoteAccountResult> SetKeyAsync(RemoteSetKeyRequest request, CancellationToken cancellationToken = default)
    {
        if (OneAccount(request.Provider, out var entry) is { } refusal)
        {
            return new RemoteAccountResult { Error = refusal };
        }

        try
        {
            await entry!.Account!.SetKeyAsync(request.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Its own words, which never quote the value.
            return new RemoteAccountResult { Error = ex.Message, Accounts = [ToDto(entry!)] };
        }

        return new RemoteAccountResult { Ok = true, Accounts = [ToDto(entry!)] };
    }

    public async Task<RemoteAccountResult> ClearKeyAsync(RemoteAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (OneAccount(request.Provider, out var entry) is { } refusal)
        {
            return new RemoteAccountResult { Error = refusal };
        }

        await entry!.Account!.ClearAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteAccountResult { Ok = true, Accounts = [ToDto(entry)] };
    }

    /// <summary>Asks the provider whether the key works. A key it refuses stays saved: the user may be offline, or fixing the account.</summary>
    public async Task<RemoteAccountResult> ValidateAsync(RemoteAccountRequest request, CancellationToken cancellationToken = default)
    {
        if (OneAccount(request.Provider, out var entry) is { } refusal)
        {
            return new RemoteAccountResult { Error = refusal };
        }

        if (entry!.AccountProblem() is { } problem)
        {
            return new RemoteAccountResult
            {
                Error = problem,
                FailureKind = Word(RemoteFailureKind.Authentication),
                Accounts = [ToDto(entry)],
            };
        }

        var (_, error, kind) = await AskAccountAsync(entry, cancellationToken).ConfigureAwait(false);
        return new RemoteAccountResult { Ok = error is null, Error = error, FailureKind = kind, Accounts = [ToDto(entry)] };
    }

    private async Task<(RemoteAccountInfo? Info, string? Error, string? Kind)> AskAccountAsync(RemoteProviderEntry entry, CancellationToken cancellationToken)
    {
        var (info, error, kind) = await CallAsync(entry, entry.Provider.GetAccountAsync, cancellationToken).ConfigureAwait(false);
        if (info is not null)
        {
            lock (_sync) _answers[entry.Id] = new LastAnswer(info, _time.GetUtcNow());
        }

        return (info, error, kind);
    }

    private string? OneAccount(string? providerId, out RemoteProviderEntry? entry)
    {
        var refusal = OneProvider(providerId, out entry);
        if (refusal is null && entry!.Account is null)
        {
            return $"{entry.DisplayName} has no API key to manage.";
        }

        return refusal;
    }

    private string? OneProvider(string? providerId, out RemoteProviderEntry? entry)
    {
        if (providerId is not null)
        {
            entry = _providers.Find(providerId);
            return entry is null ? NoSuchProvider(providerId) : null;
        }

        var all = _providers.All;
        entry = all.Count == 1 ? all.First() : null;
        return entry is null ? "Say which provider this is for." : null;
    }

    private string NoSuchProvider(string id) => $"This app has no provider called {Safe(id)}.";

    // ---- Classifying and adding ---------------------------------------------------------------

    public async Task<RemoteHostersResult> GetHostersAsync(CancellationToken cancellationToken = default)
    {
        if (await _jobs.GetHosterListAsync(cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return new RemoteHostersResult { Error = "No provider here has a list of sites." };
        }

        return new RemoteHostersResult
        {
            Ok = true,
            Provider = found.Entry.Id,
            ProviderName = found.Entry.DisplayName,
            Problem = Safe(found.Problem),
            FetchedAt = found.FetchedAt,
            Hosters = found.Hosters.OrderBy(hoster => hoster.Name, StringComparer.OrdinalIgnoreCase).Select(hoster => new RemoteHosterDto
            {
                Name = Safe(hoster.Name) ?? string.Empty,
                Domains = hoster.Domains.ToList(),
                Available = hoster.Available,
                Type = Safe(hoster.Type),
                IconUrl = hoster.IconUrl,
                Note = Safe(hoster.Note),
                DailyLinkLimit = hoster.DailyLinkLimit,
                DailyLinksUsed = hoster.DailyLinksUsed,
                DailyBandwidthLimit = hoster.DailyBandwidthLimit,
                DailyBandwidthUsed = hoster.DailyBandwidthUsed,
                PerLinkSizeLimit = hoster.PerLinkSizeLimit,
            }).ToList(),
        };
    }

    public async Task<RemoteClassifyResult> ClassifyAsync(RemoteClassifyRequest request, CancellationToken cancellationToken = default)
    {
        var classified = request.WithFiles
            ? await _jobs.ClassifyWithFilesAsync(request.Text, cancellationToken, _jobRoots).ConfigureAwait(false)
            : await _jobs.ClassifyAsync(request.Text, cancellationToken).ConfigureAwait(false);
        return new RemoteClassifyResult
        {
            Ok = true,
            Kind = Word(classified.Kind),
            Provider = classified.Provider,
            ProviderName = classified.ProviderName,
            ItemKind = classified.ItemKind is { } kind ? RemoteKinds.ToWord(kind) : null,
            ItemId = classified.ItemId,
            Name = Safe(classified.Name),
            DisplayHost = Safe(classified.DisplayHost),
            HosterName = Safe(classified.HosterName),
            HosterAvailable = classified.HosterAvailable,
            HosterNote = Safe(classified.HosterNote),
            HosterDailyLinksLeft = classified.HosterDailyLinksLeft,
            HosterDailyBytesLeft = classified.HosterDailyBytesLeft,
            HosterLargestFile = classified.HosterLargestFile,
            Reason = Safe(classified.Reason),
            Note = Safe(classified.Note),
            CheckIncomplete = classified.CheckIncomplete,
            Cached = classified.Cached,
            Files = classified.Files?.Select(ToDto).ToList(),
        };
    }

    public async Task<RemoteAddResultDto> AddAsync(RemoteAddRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!TryReadSelection(request, out var selection))
        {
            return new RemoteAddResultDto { Error = "The selection has to be ask, all or chosen." };
        }

        RemoteItemRef? existing = null;
        if (request.ItemId is not null)
        {
            if (!string.IsNullOrEmpty(request.Input))
            {
                return new RemoteAddResultDto { Error = "Send a link or an item, not both." };
            }

            if (OneProvider(request.Provider, out var entry) is { } refusal)
            {
                return new RemoteAddResultDto { Error = refusal };
            }

            if (!RemoteKinds.TryParse(request.ItemKind, out var kind) || string.IsNullOrWhiteSpace(request.ItemId))
            {
                return new RemoteAddResultDto { Error = "An item needs its kind (torrents, webdl or usenet) and its id." };
            }

            existing = new RemoteItemRef(entry!.Id, kind, request.ItemId.Trim());
        }

        var result = await _jobs.AddAsync(new Remote.RemoteAddRequest
        {
            Input = request.Input,
            ExistingItem = existing,
            Destination = request.Destination,
            QueueId = request.QueueId,
            StartMode = request.StartMode,
            Selection = selection,
            FileIds = request.FileIds,
            // A missing entry or path is refused below as a path that can't be used.
            FilePaths = request.FilePaths?.Select(file => new RemoteChosenPath(file?.Path ?? string.Empty, file?.Size ?? 0)).ToList(),
            ApprovedUncached = request.ApproveUncached,
            LinkPassword = request.LinkPassword,
            ArchivePassword = OfferedLinkPassword(request) ?? request.ArchivePassword,
            // An offer only: a password the package gets from the user meanwhile stays.
            ArchivePasswordOffered = OfferedLinkPassword(request) is not null,
            SourceRoots = _jobRoots,
            DestinationRoots = _jobRoots,
        }, cancellationToken).ConfigureAwait(false);

        return new RemoteAddResultDto
        {
            Ok = result.Job is not null,
            Error = Safe(result.Error),
            Note = Safe(result.Note),
            Existing = result.Existing,
            InputKind = result.Kind is { } inputKind ? Word(inputKind) : null,
            Job = result.Job is { } job ? ToDto(job) : null,
        };
    }

    /// <param name="selection">Null when nothing was asked for, which leaves it to the input.</param>
    private static bool TryReadSelection(RemoteAddRequestDto request, out RemoteSelectionMode? selection)
    {
        switch (request.Selection?.ToLowerInvariant())
        {
            case null or "":
                selection = request.FileIds is { Count: > 0 } || request.FilePaths is { Count: > 0 } ? RemoteSelectionMode.Chosen : null;
                return true;
            case "ask" or "askwhenready":
                selection = RemoteSelectionMode.AskWhenReady;
                return true;
            case "all":
                selection = RemoteSelectionMode.All;
                return true;
            case "chosen":
                selection = RemoteSelectionMode.Chosen;
                return true;
            default:
                selection = default;
                return false;
        }
    }

    // ---- Reading ------------------------------------------------------------------------------

    public RemoteListResult List() => new() { Ok = true, Jobs = _jobs.List().Select(ToDto).ToList() };

    /// <summary>The provider's own list, or one item of it, for browsing. Reads only, and changes nothing here.</summary>
    public async Task<RemoteItemResult> GetItemsAsync(RemoteItemRequest request, CancellationToken cancellationToken = default)
    {
        if (OneProvider(request.Provider, out var entry) is { } refusal)
        {
            return new RemoteItemResult { Error = refusal };
        }

        var provider = entry!;
        if (!RemoteKinds.TryParse(request.Kind, out var kind))
        {
            return new RemoteItemResult { Error = "The kind has to be torrents, webdl or usenet." };
        }

        if (!ItemKinds(provider).Contains(request.Kind) || provider.Catalog is not { } catalog)
        {
            return new RemoteItemResult { Error = $"{provider.DisplayName} has no {request.Kind} to list." };
        }

        if (provider.AccountProblem() is { } problem)
        {
            return new RemoteItemResult { Error = problem, FailureKind = Word(RemoteFailureKind.Authentication) };
        }

        var offset = Math.Max(0, request.Offset);
        var limit = request.Limit is >= 1 and <= LargestPageSize ? request.Limit : DefaultPageSize;
        if (request.ItemId is { } itemId)
        {
            var (found, error, failure) = await CallAsync(
                provider,
                token => catalog.GetAsync(new RemoteItemRef(provider.Id, kind, itemId), request.Fresh, token),
                cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return new RemoteItemResult { Error = error, FailureKind = failure };
            }

            return found is null
                ? new RemoteItemResult
                {
                    Error = RemoteJobCoordinator.NotOnAccount(provider),
                    FailureKind = Word(RemoteFailureKind.NotFound),
                }
                : new RemoteItemResult { Ok = true, Items = [ToDto(provider, found, withFiles: true)], Limit = 1 };
        }

        var (items, listError, listFailure) = await CallAsync(
            provider,
            token => catalog.ListAsync(kind, offset, limit, request.Fresh, token),
            cancellationToken).ConfigureAwait(false);
        return items is null
            ? new RemoteItemResult { Error = listError, FailureKind = listFailure, Offset = offset, Limit = limit }
            : new RemoteItemResult
            {
                Ok = true,
                Items = items.Select(item => ToDto(provider, item, request.IncludeFiles)).ToList(),
                Offset = offset,
                Limit = limit,
                HasMore = items.Count >= limit,
            };
    }

    public async Task<RemoteFilesResultDto> GetFilesAsync(RemoteJobIdRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _jobs.GetFilesAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return new RemoteFilesResultDto
        {
            Ok = result.Error is null,
            Error = Safe(result.Error),
            Pending = result.Pending,
            Files = result.Files.Select(ToDto).ToList(),
        };
    }

    // ---- Acting -------------------------------------------------------------------------------

    public async Task<RemoteActionResultDto> SelectAsync(RemoteSelectRequest request, CancellationToken cancellationToken = default)
        => RefuseOutside(request.JobId) ?? ToDto(await _jobs.ChooseFilesAsync(request.JobId, request.All, request.FileIds, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// With <see cref="_jobRoots"/>: a job saved outside them (one the user added on this PC) is left alone by what would
    /// add or start its downloads, because they would be saved there. Null when it may go ahead, or there is no such job.
    /// </summary>
    private RemoteActionResultDto? RefuseOutside(Guid jobId)
        => _jobRoots is not null && _jobs.Find(jobId) is { Destination: { } destination } && !string.IsNullOrWhiteSpace(destination) &&
           _jobRoots.Allowed(destination) is null
            ? new RemoteActionResultDto { Error = CallerScope.OutsideJobRefused }
            : null;

    public async Task<RemoteActionResultDto> RetryAsync(RemoteRetryRequest request, CancellationToken cancellationToken = default)
    {
        if (RefuseOutside(request.JobId) is { } outside) return outside;
        // Agreeing to a job that is waiting for exactly that isn't a retry of anything.
        if (request.ApproveUncached == true && _jobs.Find(request.JobId) is { ApprovalNeeded: true, State: RemoteJobState.Intended })
        {
            return ToDto(await _jobs.ApproveUncachedAsync(request.JobId, cancellationToken).ConfigureAwait(false));
        }

        var retried = await _jobs.RetryAsync(request.JobId, request.LinkPassword, request.ApproveUncached, cancellationToken).ConfigureAwait(false);
        if (retried.Ok && request.LinkPasswordForArchives && UsableForArchives(request.LinkPassword))
        {
            var offered = await _jobs.OfferArchivePasswordAsync(request.JobId, request.LinkPassword!, cancellationToken).ConfigureAwait(false);
            if (!offered.Ok) return ToDto(offered);
        }

        return ToDto(retried);
    }

    public async Task<RemoteActionResultDto> CheckNowAsync(RemoteCheckNowRequest request, CancellationToken cancellationToken = default)
    {
        if (request.JobId is { } id)
        {
            return ToDto(await _jobs.CheckNowAsync(id, cancellationToken).ConfigureAwait(false));
        }

        if (request.Provider is { } provider && _providers.Find(provider) is null)
        {
            return new RemoteActionResultDto { Error = NoSuchProvider(provider) };
        }

        await _jobs.CheckNowAsync(request.Provider, cancellationToken).ConfigureAwait(false);
        return new RemoteActionResultDto { Ok = true };
    }

    public async Task<RemoteActionResultDto> RemoveAsync(RemoteJobIdRequest request, CancellationToken cancellationToken = default)
        => ToDto(await _jobs.RemoveAsync(request.JobId, cancellationToken).ConfigureAwait(false));

    /// <summary>TB.S1: starts the job's entry in the provider's queue. Removing it is <see cref="DeleteRemoteAsync"/>, confirmed.</summary>
    public async Task<RemoteActionResultDto> StartQueuedAsync(RemoteJobIdRequest request, CancellationToken cancellationToken = default)
        => RefuseOutside(request.JobId) ?? ToDto(await _jobs.ControlQueuedAsync(request.JobId, RemoteQueueAction.Start, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteActionResultDto> DeleteRemoteAsync(RemoteDeleteRequest request, CancellationToken cancellationToken = default)
        => ToDto(await _jobs.RemoveRemoteItemAsync(request.JobId, request.Confirmed, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// TB.S2: the link's password, when the user agreed to have it tried on this job's own archives and gave no archive
    /// password of their own. Null otherwise, and for one the archive side couldn't take anyway.
    /// </summary>
    private static string? OfferedLinkPassword(RemoteAddRequestDto request)
        => request.LinkPasswordForArchives && string.IsNullOrEmpty(request.ArchivePassword) && UsableForArchives(request.LinkPassword)
            ? request.LinkPassword
            : null;

    private static bool UsableForArchives(string? password)
        => !string.IsNullOrEmpty(password) && RemoteJobRules.DescribeBadArchivePassword(password) is null;

    public async Task<RemoteActionResultDto> SetArchivePasswordAsync(RemoteArchivePasswordRequest request, CancellationToken cancellationToken = default)
        => RefuseOutside(request.JobId) ?? ToDto(await _jobs.SetArchivePasswordAsync(request.JobId, request.Password, cancellationToken).ConfigureAwait(false));

    // ---- Containers ---------------------------------------------------------------------------

    public async Task<RemoteContainerPreviewResult> ContainerPreviewAsync(RemoteContainerPreviewRequest request, CancellationToken cancellationToken = default)
    {
        var preview = await _containers.PreviewAsync(request.Path, cancellationToken).ConfigureAwait(false);
        // A browser on another device can't hand it on (HostCommandDispatcher refuses remote.container.sendToJd there).
        if (_jobRoots is not null) preview.CanSendToJd = false;
        preview.Error = Safe(preview.Error);
        preview.AccountProblem = Safe(preview.AccountProblem);
        preview.Problems = preview.Problems.Select(problem => Safe(problem) ?? string.Empty).ToList();
        foreach (var package in preview.Packages)
        {
            package.Name = Safe(package.Name);
            package.Comment = Safe(package.Comment);
        }

        foreach (var entry in preview.Entries)
        {
            entry.DisplayHost = Safe(entry.DisplayHost);
            entry.DisplayName = Safe(entry.DisplayName);
            entry.Reason = Safe(entry.Reason);
        }

        return preview;
    }

    public async Task<RemoteContainerAddResult> ContainerAddAsync(RemoteContainerAddRequest request, CancellationToken cancellationToken = default)
    {
        var added = await _containers.AddAsync(request, cancellationToken).ConfigureAwait(false);
        var outcomes = added.Outcomes.Select(outcome => new RemoteContainerAddOutcomeDto
        {
            EntryId = outcome.EntryId,
            Ok = outcome.Result.Job is not null,
            Error = Safe(outcome.Result.Error),
            Note = Safe(outcome.Result.Note),
            Existing = outcome.Result.Existing,
            InputKind = outcome.Result.Kind is { } kind ? Word(kind) : null,
            JobId = outcome.Result.Job?.Id,
            Job = outcome.Result.Job is { } job ? ToDto(job) : null,
        }).ToList();
        return FitInOneAnswer(new RemoteContainerAddResult { Ok = outcomes.Any(outcome => outcome.Ok), Error = Safe(added.Error), Outcomes = outcomes });
    }

    /// <summary>
    /// By now the entries are saved, and may already be sending links to the provider. A container can hold enough that the
    /// whole list of jobs won't fit one answer, and the worst thing to tell the client then is that adding failed. So the
    /// full jobs are what goes: the client still learns how each entry went, and can read the jobs through remote.list.
    /// </summary>
    public static RemoteContainerAddResult FitInOneAnswer(RemoteContainerAddResult result, int mostChars = Protocol.MaxFrameBytes - (64 * 1024))
    {
        if (IpcMessage.Create(MessageTypes.RemoteContainerAddResult, result).Payload?.GetRawText().Length < mostChars)
        {
            return result;
        }

        foreach (var outcome in result.Outcomes)
        {
            outcome.Job = null;
        }

        result.JobsLeftOut = true;
        return result;
    }

    public async Task<RemoteContainerSendResult> ContainerSendToJdAsync(RemoteContainerSendRequest request, CancellationToken cancellationToken = default)
    {
        var sent = await _containers.SendToJDownloaderAsync(request.Path, cancellationToken).ConfigureAwait(false);
        return new RemoteContainerSendResult
        {
            Ok = sent.Status == JDownloaderBridgeStatus.Sent,
            Status = Word(sent.Status),
            JdAnswered = sent.Status is JDownloaderBridgeStatus.Sent or JDownloaderBridgeStatus.Rejected,
            Message = Safe(sent.Message) ?? string.Empty,
        };
    }

    // ---- Calling a provider -------------------------------------------------------------------

    /// <summary>A provider's failure as the words and kind a client gets. Only the caller going away is left to throw.</summary>
    private async Task<(T? Value, string? Error, string? Kind)> CallAsync<T>(RemoteProviderEntry entry, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        where T : class?
    {
        try
        {
            return (await call(cancellationToken).ConfigureAwait(false), null, null);
        }
        catch (RemoteProviderException ex)
        {
            return (null, Safe(RemoteJobCoordinator.Describe(entry, ex)), Word(ex.Kind));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // Such as the client's timeout, which surfaces as a cancellation nobody asked for.
            _log?.Invoke($"A call to {entry.Id} failed: {ex.GetType().Name}.");
            return (null, $"{entry.DisplayName} could not be reached.", Word(RemoteFailureKind.Network));
        }
    }

    // ---- To what clients see ------------------------------------------------------------------

    /// <summary>
    /// Every word on its way out: nothing that looks like an address, never the key in any spelling,
    /// and nothing a terminal or a log view would act on rather than show.
    /// </summary>
    private string? Safe(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var bounded = text.Length <= UrlRedactor.LongestText ? text : text[..UrlRedactor.LongestText];
        var hidden = UrlRedactor.HideAddresses(_providers.Mask(bounded));
        return hidden.Any(FileNameResolver.IsDeceptiveOrControl)
            ? new string(hidden.Select(c => FileNameResolver.IsDeceptiveOrControl(c) ? ' ' : c).ToArray())
            : hidden;
    }

    private static string Word<T>(T value) where T : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static List<string> ItemKinds(RemoteProviderEntry entry)
    {
        var kinds = new List<string>();
        if (entry.Capabilities.HasFlag(RemoteCapabilities.Torrents)) kinds.Add(RemoteKinds.ToWord(RemoteItemKind.Torrent));
        if (entry.Capabilities.HasFlag(RemoteCapabilities.WebDownloads)) kinds.Add(RemoteKinds.ToWord(RemoteItemKind.WebDownload));
        if (entry.Capabilities.HasFlag(RemoteCapabilities.Usenet)) kinds.Add(RemoteKinds.ToWord(RemoteItemKind.Usenet));
        return kinds;
    }

    private RemoteAccountDto ToDto(RemoteProviderEntry entry)
    {
        LastAnswer? answer;
        lock (_sync) _answers.TryGetValue(entry.Id, out answer);
        return new RemoteAccountDto
        {
            Provider = entry.Id,
            ProviderName = entry.DisplayName,
            Capabilities = AllCapabilities.Where(capability => entry.Capabilities.HasFlag(capability)).Select(Word).ToList(),
            ItemKinds = ItemKinds(entry),
            HasKey = entry.Account?.HasKey == true,
            KeyUnreadable = entry.Account?.State == RemoteKeyState.Unreadable,
            Plan = Safe(answer?.Info.DisplayPlan),
            Premium = answer?.Info.Premium == true,
            PremiumUntil = answer?.Info.PremiumUntil,
            CooldownUntil = answer?.Info.CooldownUntil,
            CheckedAt = answer?.At,
        };
    }

    private RemoteJobDto ToDto(RemoteJobView view) => new()
    {
        Id = view.Id,
        Provider = view.Provider,
        ProviderName = view.ProviderName,
        Kind = RemoteKinds.ToWord(view.Kind),
        State = Word(view.State),
        Status = Safe(view.Status) ?? string.Empty,
        Stage = view.Stage is { } stage ? Word(stage) : null,
        RawState = Safe(view.RawState),
        Progress = view.Progress,
        SpeedBytesPerSecond = view.SpeedBytesPerSecond,
        EtaSeconds = view.EtaSeconds,
        ItemId = Safe(view.ItemId),
        Name = Safe(view.Name),
        DisplayHost = Safe(view.DisplayHost),
        Cached = view.Cached,
        ApprovalNeeded = view.ApprovalNeeded,
        HeldForApproval = view.HeldForApproval,
        ApprovedUncached = view.ApprovedUncached,
        HasLinkPassword = view.HasLinkPassword,
        HasArchivePassword = view.HasArchivePassword,
        FailureKind = view.FailureKind is { } failure ? Word(failure) : null,
        FailureMessage = Safe(view.FailureMessage),
        NextCheckAt = view.NextCheckAt,
        Destination = view.Destination,
        QueueId = view.QueueId,
        StartMode = view.StartMode,
        SelectionMode = Word(view.SelectionMode),
        SelectedFileIds = view.SelectedFileIds.ToList(),
        PackageId = view.PackageId,
        Notes = view.Notes.Select(note => Safe(note) ?? string.Empty).ToList(),
        CreatedAt = view.CreatedAt,
        UpdatedAt = view.UpdatedAt,
    };

    private RemoteActionResultDto ToDto(Remote.RemoteActionResult result) => new()
    {
        Ok = result.Ok,
        Error = Safe(result.Error),
        Job = result.Job is { } job ? ToDto(job) : null,
    };

    private RemoteFileDto ToDto(RemoteFileView file) => new()
    {
        FileId = Safe(file.FileId) ?? string.Empty,
        RelativePath = Safe(file.RelativePath),
        Size = file.Size,
        Infected = file.Infected,
        Selectable = file.Selectable,
        Note = Safe(file.Note),
    };

    private RemoteItemDto ToDto(RemoteProviderEntry entry, RemoteItem item, bool withFiles) => new()
    {
        Provider = entry.Id,
        Kind = RemoteKinds.ToWord(item.Ref.Kind),
        ItemId = Safe(item.Ref.ItemId) ?? string.Empty,
        Name = Safe(item.Name),
        Size = item.Size,
        Stage = Word(item.Stage),
        RawState = Safe(item.RawState),
        Progress = item.Progress,
        SpeedBytesPerSecond = item.SpeedBytesPerSecond,
        EtaSeconds = item.EtaSeconds,
        Cached = item.Cached,
        Error = Safe(item.Error),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        ExpiresAt = item.ExpiresAt,
        FileCount = item.Files.Count,
        Files = withFiles ? item.Files.Select(file => ToDto(RemoteJobCoordinator.ToFileView(entry, file))).ToList() : null,
    };
}
