# IPC protocol: additions

The frame format, version check and connection rules are in `architecture.md`. This file records
message families added on top of protocol version 1. They only add message types, so version 1 stays:
a client that does not know them ignores them.

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
| `completed` | Every check passed and the files are in `destination` |
| `failed` | `failureKind` says why, and the archives are kept |

`failureKind` is one of `passwordRequired`, `wrongPassword`, `missingVolume`, `corrupted`,
`unsupportedVersion`, `toolUnavailable`, `diskSpace`, `permission`, `unsafePath`, `cancelled` or
`unknown`. `needsPassword` is set for the first two. `stateText` says all of this in plain words, ready
to show.

### Event

**`archive.changed`**: `{ set }` with the set's whole state, or `{ removedId }` when it left the list. Sent
to every connected client, and throttled during a run.
