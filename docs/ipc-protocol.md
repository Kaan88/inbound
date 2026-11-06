# IPC protocol

How clients talk to the background Host: the desktop UI, the CLI and the browser's native host. This
page gives the framing rules, the remote provider messages and the archive messages. The full list of message names is
`MessageTypes` in `src/DownloadManager.Contracts/Protocol.cs`, and the payloads are the classes in
`Dtos.cs` and `RemoteDtos.cs` next to it.

## Connection and framing

- A per-user named pipe, `\\.\pipe\DownloadManager-<hash>`. Only the current user and SYSTEM can open it.
- Each frame is a 4-byte little-endian length, then that many bytes of UTF-8 JSON. The limit is 8 MB per frame.
- Each message has `v` (protocol version), `type`, `id`, and on replies `replyTo` with the id of the request, plus `payload`.
- JSON uses camelCase names and leaves out null values. Unknown fields are ignored.
- On connect the Host sends `server.hello` and then `jobs.snapshot`. After that a client gets replies to its own requests and events meant for everyone.

The protocol version is **1**. It changes only for a change that existing clients can't ignore. New
abilities arrive as new message types and new optional fields, and clients ignore message types they
don't know. A request with a different version gets `server.incompatible` and the connection closes.

`server.hello` lists capabilities. `remote` means this Host has the provider messages below.

## One command at a time per connection

The Host handles a connection's commands strictly in order, and each waits for the one before it.
Events keep flowing, but the next command doesn't start until the current one has been answered.

**So a call that waits on the network holds up everything else sent on that connection.** A frontend
should send these on a separate connection of their own and keep its main one for quick commands:

- `remote.account.get` with `refresh`, and `remote.account.validate`
- `remote.classify` and `remote.add` (the provider's hoster list may have to be fetched)
- `remote.item.get`
- `remote.files`
- `remote.deleteRemote`
- `remote.container.preview` and `remote.container.add` (the hoster list again)
- `remote.container.sendToJd`, which waits for a person to answer another program's question, for up to five minutes

If a client closes its connection during one of these, the call is cancelled. The rest of the
`remote.*` messages only touch local state, and answer without waiting on the network.

## Remote provider messages

A remote provider is an account service that fetches content on the user's behalf and offers it for
download. The Host owns the account key and all provider state.

Three rules hold for every reply and event below:

- **No secrets go out.** No field holds an API key, a direct link, a magnet, a hoster link or a link
  password. Clients get names, a display host, stages, the provider's own state word, progress and
  failures. Text that came from a provider or an input is scrubbed of addresses and keys first.
- **The key is write-only.** `hasKey` and `keyUnreadable` are all a client can ever learn about it.
- **Remote stages aren't local progress.** `progress` on a remote job is the provider's own. Bytes
  reaching this computer are ordinary jobs, with `remote://` references in `job.event` and
  `jobs.snapshot`. Pausing or removing those never touches the provider.

Every reply has `ok` and, when it is false, `error` in words for the user. `failureKind` where present is
one of `authentication`, `accountLimit`, `accountBusy`, `linkPasswordRequired`, `rateLimited`,
`notFound`, `rejected`, `serviceError`, `network`, `badResponse`, `notCached`, `alreadyExists`. A
request whose reply would pass the frame limit gets `ok: false` instead, and the connection stays up.

`provider` is a provider id such as `torbox`. It may be left out while only one provider is
registered. Kinds are `torrents`, `webdl` and `usenet`.

### Requests

| Type | Payload | Reply |
|---|---|---|
| `remote.account.get` | `provider?`, `refresh?` | `remote.account.result` |
| `remote.account.setKey` | `provider?`, `key` | `remote.account.result` |
| `remote.account.clearKey` | `provider?` | `remote.account.result` |
| `remote.account.validate` | `provider?` | `remote.account.result` |
| `remote.classify` | `text` | `remote.classifyResult` |
| `remote.add` | see below | `remote.addResult` |
| `remote.list` | none | `remote.listResult` |
| `remote.item.get` | `provider?`, `kind`, `itemId?`, `offset?`, `limit?`, `includeFiles?`, `fresh?` | `remote.item.result` |
| `remote.files` | `jobId` | `remote.filesResult` |
| `remote.select` | `jobId`, `all?`, `fileIds?` | `remote.actionResult` |
| `remote.retry` | `jobId`, `linkPassword?`, `approveUncached?` | `remote.actionResult` |
| `remote.checkNow` | `jobId?`, `provider?` | `remote.actionResult` |
| `remote.remove` | `jobId` | `remote.actionResult` |
| `remote.deleteRemote` | `jobId`, `confirmed` | `remote.actionResult` |
| `remote.archivePassword` | `jobId`, `password` | `remote.actionResult` |
| `remote.container.preview` | `path` | `remote.container.previewResult` |
| `remote.container.add` | `previewId`, `entryIds`, `destination?`, `queueId?`, `startMode?`, `approveUncached?` | `remote.container.addResult` |
| `remote.container.sendToJd` | `path` | `remote.container.sendResult` |

**`remote.account.*`** replies carry `accounts`: `provider`, `providerName`, `capabilities`, `itemKinds`,
`hasKey`, `keyUnreadable`, `plan`, `premium`, `premiumUntil`, `cooldownUntil` and `checkedAt`. Without
`refresh`, `get` gives the last answer known since the key changed, with no network call. A key
the provider refuses stays saved and `validate` reports it. `keyUnreadable` means a key is saved and
can't be decrypted on this Windows account, so it has to be entered again.

**`remote.classify`** says what a pasted string or a local `.torrent` path is: `kind` is `folderLink`,
`magnet`, `torrentFile`, `hosterLink`, `providerDirectLink`, `ordinaryLink`, `undetermined` or
`unsupported`, with a `reason` for the last two. `undetermined` means an account could take the link
and there is no list of supported sites to tell whether it does, so it must not be treated as
ordinary: offer a retry, or an explicit choice to request it directly. Show `note` when there is one:
it can say a stale list couldn't be refreshed, or that a site couldn't be checked. A `hosterLink` also
carries what the provider last said about that site, each null when it sets no limit:
`hosterDailyLinksLeft`, `hosterDailyBytesLeft` and `hosterLargestFile`. Clients that don't know a
value of `kind` should treat it as they would `unsupported`.

`job.add` never classifies. It stays an explicit direct request, because a browser handoff is made of
those. A frontend with a hoster link has to route it through `remote.add`; `dm add` does so by itself
and refuses a hoster link.

**`remote.add`** takes either `input` (a folder link, magnet, hoster link or a full path to a local
`.torrent` file) or `provider`, `itemKind` and `itemId` (an item from `remote.item.get`). Other fields:
`destination` (full path), `queueId`, `startMode` (`startNow`, `queue`, `later`), `selection` (`ask`,
`all`, `chosen`), `fileIds`, `approveUncached`, `linkPassword`, `archivePassword`. The input is stored encrypted and is
never returned. With no `selection`, a hoster link takes `all` and starts by itself when ready, and
anything else asks. The reply has `job`, `inputKind`, `note` when options given were not used, and
`existing` when an active job already covers
this input. An ordinary link gets `ok: false`: those go through `job.add`. Adding only saves the
intent, and the Host does the work afterwards, so watch `remote.changed`.

**Jobs** (`remote.addResult`, `remote.listResult`, `remote.actionResult`, `remote.changed`) have `id`,
`provider`, `providerName`, `kind`, `state`, `status` (words to show), `stage`, `rawState`, `progress`,
`speedBytesPerSecond`, `etaSeconds`, `itemId`, `name`, `displayHost`, `cached`, `approvalNeeded`,
`approvedUncached`, `hasLinkPassword`, `hasArchivePassword`, `failureKind`, `failureMessage`, `nextCheckAt`, `destination`,
`queueId`, `startMode`, `selectionMode`, `selectedFileIds`, `packageId`, `notes`, `createdAt` and `updatedAt`.

| `state` | Meaning |
|---|---|
| `intended` | Saved, nothing sent. `approvalNeeded` means it waits for the user's go-ahead |
| `submitting` | Being sent |
| `submissionUnknown` | The answer was lost. The provider's list decides, and nothing is sent twice |
| `waiting` | The provider is working on it |
| `awaitingSelection` | Ready. `remote.files` lists the files, `remote.select` chooses |
| `localStarted` | Local downloads exist |
| `blocked` | The account is busy for now. Retries by itself |
| `failed` | Waits for the user: `remote.retry` |

`stage` is `unknown`, `queued`, `metadataPending`, `processing`, `stalled`, `ready` or `failed`. An
unknown provider state stays visible in `rawState` and never counts as ready.

**`remote.item.get`** reads the account's own list for a browser: a page by `kind`, `offset` and `limit`
(1 to 250, 50 otherwise), or one item with its files by `itemId`. It changes nothing. `hasMore` means
the page was full.

**`remote.files`** gives each file's `fileId`, `relativePath`, `size`, `infected`, `selectable` and
`note`. `pending` means the provider doesn't know the files yet, and none are made up. A path that
isn't safe has a null `relativePath` and can't be selected.

**`remote.retry`** also approves a job waiting with `approvalNeeded` when `approveUncached` is true.
`linkPassword` is the source site's password for the link, which is not an archive password. Retrying
a known item looks at it again. Only an item confirmed gone from the account is ever submitted again.

**`remote.checkNow`** looks at a job, or at everything, without waiting for its timer. A rate-limit wait
the provider asked for still holds.

**`remote.remove`** takes the job off this app's list and leaves the provider's item and any local
downloads as they are. **`remote.deleteRemote`** deletes the item from the provider for good, and is
refused unless `confirmed` is true.

**`remote.archivePassword`** sets the password for a job's archives, before or after its downloads exist.
It is not `linkPassword`: that is the source site's password for a link and goes to the provider, while this
goes to the archive side and never leaves the computer. It is kept encrypted on the job until there is a
package, and then handed to `archive.*`'s record for that package. A password can't hold a line break. An
empty one clears it on both sides. Only `hasArchivePassword` ever comes back, and it stays true after the
hand-over, because the password is still there. If a run has already started without it, the set runs again
by itself.

### Containers

A container is a local `.rsdf`, `.ccf` or `.dlc` file. Reading one takes two steps, because a decrypted link
is a secret and a client never gets to see it.

**`remote.container.preview`** reads the file by its full path. A path has to start with a drive. Network and
device paths are refused before anything looks at them, because even asking whether such a file exists
sends this Windows account's credentials to the host it names. Files are limited to 16 MB and 5000 links. The
reply has:

- `ok`, `error`. A damaged file gets `ok: false` and a reason in words.
- `format` (`rsdf`, `ccf`, `dlc` or `unknown`), `formatVersion`.
- `notSupported` with `canSendToJd`, see below.
- `previewId` and `expiresAt`. The Host holds the links in memory only, against this id, for 15 minutes, with at
  most 8 previews at a time. Both are absent when nothing in the file can be added.
- `accountProblem`, such as no API key being set. Each entry also says so in its `reason`.
- `packages`: `index`, `name`, `comment`, `hasArchivePassword` and `entryCount`.
- `entries`: `entryId`, `packageIndex`, `displayHost`, `displayName`, `size`, `kind`, `reason` and `canAdd`.
  No entry holds its link, and names and comments have addresses, keys and the package's passwords taken out.
- `problems`: what was wrong with parts of the file, or is worth saying about it as a whole.

`kind` is `folderLink`, `magnet`, `hosterLink`, `undetermined` or `unsupported`, and a client should treat a
value it doesn't know as `unsupported`. Each `unsupported` entry has a `reason`. An ordinary link is
`unsupported` here, with a reason that says the provider won't take it and that it can be added as a normal
download; nothing from a container is ever requested directly. A link naming a file on this computer is
refused, and so is a direct link holding an account key.

**`remote.container.add`** adds the chosen entries one at a time, each through the same path as `remote.add`.
So the cache check, the approval of uncached items, the spacing between creations and the dedupe all apply,
and adding only saves the intent. `outcomes` has one line per entry: `entryId`, `ok`, `error`, `note`,
`existing`, `inputKind`, `jobId` and `job`. The reply's `ok` means at least one was added. An expired or unknown
`previewId` gets `ok: false` and no outcomes. When so many entries were added that their full jobs wouldn't fit
one frame, the reply has `jobsLeftOut: true` and every `job` is null. `ok`, each outcome and `jobId` still hold,
the entries are saved, and `remote.list` has the jobs.

The links of one container package share a local package, in a folder named after it under the destination.
Links in no package share one too, and go in the destination itself. An item holding one file puts it straight
in that folder, so the parts of a split archive that came through separate links meet in one place and are
claimed as one set. An item with several files keeps its own folders. `status` on these jobs describes the
whole local package.

**Archive passwords from a container.** A CCF can state a password for a package's archives, and that value goes
to the archive side for that package as an offer: it never replaces one the package already has, so what the
user has said stands. A package can name several, in which case the first is used and `problems` says so. It is
never sent to the provider, and it is never used as a link password.

**DLC.** A DLC can't be opened here, because unlocking one depends on an online service this app has no
permission to use. `remote.container.preview` gives `ok: false` with `notSupported` and the reason in `error`.

**`remote.container.sendToJd`** is the separate, optional route for a `.dlc` or `.ccf`. It only happens when a
client asks. It hands the file to a JDownloader running on this computer, on `127.0.0.1:9666`. JDownloader asks
its user to confirm, and the links stay in JDownloader: they do not come back. The reply has `ok`, `status`
(`sent`, `notRunning`, `rejected`, `timedOut`, `invalidInput` or `failed`), `jdAnswered` and `message`.
`jdAnswered` is true only when JDownloader itself said yes or no. Nothing else in this app depends on JDownloader.
This route has only been tried against a stand-in so far, not a real JDownloader.

### Event

`remote.changed` goes to every connected client after the change is saved. Its `change` says which:

- `job`, with `job` holding the whole job
- `removed`, with `jobId`
- `account`, with `account`

A client that connects later reads `remote.list` and `remote.account.get`. These aren't part of
`jobs.snapshot`.

## Archive extraction: `archive.*`

The browser bridge does not carry any of these, in either direction (`NativeBridgePolicy`). A reply has
`replyTo` set to the request's `id`. No reply or event carries a password; `hasPassword` is the only
thing a client can learn about one. None of these requests waits for an extraction, so they are safe on
a client's main connection.

| Request | Payload | Reply |
|---|---|---|
| `archive.settings.get` | none | `archive.settings.result` |
| `archive.settings.set` | `ArchiveSettingsUpdate`: only what is set changes | `archive.settings.result` |
| `archive.list` | none | `archive.listResult` |
| `archive.extract` | `ArchiveExtractRequest` | `archive.extractResult` |
| `archive.setPassword` | `ArchiveSetPasswordRequest` | `archive.actionResult` |
| `archive.retry` | `{ setId }` | `archive.actionResult` |
| `archive.cancel` | `{ setId }` | `archive.actionResult` |
| `archive.remove` | `{ setId }`: the record only, never a file | `archive.actionResult` |

**`archive.settings.result`**: `{ ok, error?, settings, tool }`.

- `settings`: `autoExtract`, `unrarPath`, `destination` (`nextToArchive`, `subfolder` or `folder`),
  `folder`, `preserveFolders`, `conflictPolicy` (`rename`, `overwrite` or `skip`), `deleteArchives`.
- `tool`: `found`, `path` (what an extraction would run now), `configuredPath`, `configuredPathFound`,
  `message`.
- `archive.settings.set` also takes `clearUnrarPath` to go back to looking for UnRAR on the machine.

**`archive.extract`** takes `filePath` (absolute, a volume of the set), `jobIds`, or `packageId`, and an
optional `password`. With `packageId` the password is kept for the package, otherwise for each set. Its
result is `{ ok, error?, sets, ignored }`, where `ignored` lists what was asked for and left alone, each
with a `reason`, such as a file that is not a RAR archive or a set that has already been extracted.

**`archive.setPassword`** takes one of `setId`, `packageId` or `filePath`, plus `password`. An empty
password clears what is on record. `filePath` need not exist yet, which is how a set gets a password
before it downloads. A set that was stopped waiting for a password runs again as soon as it gets one.

**`archive.retry`** runs a stopped or finished set again. The conflict policy decides what a second run
does to the output of the first.

### Set state

`ArchiveSetDto` carries a set's whole state each time. `state` is one of:

| State | Meaning |
|---|---|
| `downloading` | A volume is still coming, and nobody has asked for extraction |
| `downloaded` | The files are here, and nobody has asked for extraction |
| `waiting` | Asked for; see `downloadingVolumes`, `failedVolumes` and `missingVolumes` for what it waits for |
| `extracting` | Running. `phase` is `probing`, `extracting`, `verifying` or `finalizing`; `percent` comes from the tool |
| `completed` | Every check passed and the files are in `destination`. `volumes` is what the tool really read; `note` names any file with a matching name that it didn't, which was neither extracted nor deleted |
| `failed` | `failureKind` says why, and the archives are kept |

Deleting archives is opt-in, and only what the tool read can go, only once the output is flushed to the disk. Once
a download's file has gone this way its job stays `completed`, with `fileRemovedAfterExtraction: true` and a
`statusNote` that says so in words. There is no file to open for such a job.

`failureKind` is one of `passwordRequired`, `wrongPassword`, `missingVolume`, `corrupted`,
`unsupportedVersion`, `toolUnavailable`, `diskSpace`, `permission`, `unsafePath`, `cancelled` or
`unknown`. `needsPassword` is set for the first two. `stateText` says all of this in plain words, ready
to show.

### Event

**`archive.changed`**: `{ set }` with the set's whole state, or `{ removedId }` when it left the list. Sent
to every connected client, and throttled during a run.

## The browser extension

The native host passes only `handoff.request`, `host.ping`, `host.openUi`, `host.pickFolder` and
`jobs.query` from the extension, and refuses anything else with `host.actionResult`. Nothing named
`remote.*` or `archive.*` crosses in either direction, and neither does `groups.changed`. A snapshot
reaches the extension without its settings. See `NativeBridgePolicy` in Contracts.
