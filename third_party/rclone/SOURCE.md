# rclone, for Sites

Sites (`docs/plans/remote-sites.md`) runs rclone as a child process. rclone is free software by Nick Craig-Wood and
contributors, under the MIT licence; `COPYING` here is its own licence text, from
`https://raw.githubusercontent.com/rclone/rclone/v1.76.0/COPYING`
(SHA-256 `8cd2e9e750b90a04b7d82dbbca3930c696ae0309d7c10464f90a44f45754cd04`). Source: https://github.com/rclone/rclone.

rclone is **not** shipped in Setup or the update zip (owner, 2026-11-15). The desktop apps download it from Options ›
Sites, and the Docker image includes it (`Dockerfile`, stage `rclone`). Either way the official release zip is used,
and only its `rclone` binary is kept.

## The tested version

rclone 1.76.0 is the version this Inbound was tested with: the first download, and the floor an update never goes
below (`RclonePinned` in `src/DownloadManager.Sites/RcloneUpdater.cs`). Its hashes are lines of rclone's
`https://downloads.rclone.org/v1.76.0/SHA256SUMS`, which was identical on GitHub's release and carried a good signature
from rclone's release key when checked with gpg on 2026-11-15:

| File | SHA-256 |
| --- | --- |
| rclone-v1.76.0-windows-amd64.zip | 8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc |
| rclone-v1.76.0-windows-arm64.zip | c406c8a69a5a408e50624aa990fbb0a6266b3a4e83782a94de3cb96f50f86f10 |
| rclone-v1.76.0-linux-amd64.zip | b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7 |
| rclone-v1.76.0-linux-arm64.zip | ee169016675e23a69d77240565133ff7dbc9b33d16fa8f7053d4af39f1eaeb78 |

The rest (386, osx) are in `RclonePinned`. The linux-amd64 zip was also downloaded and its size (32,194,302 bytes) and
hash checked by hand.

## Newer versions

A newer rclone is installed only if its `SHA256SUMS` comes identical from `downloads.rclone.org` and from the GitHub
release, its signature is good with rclone's release key, and the zip matches its line
(`RcloneReleaseSignature`, `RcloneDownloads`). The key is built in:

- Fingerprint `FBF737ECE9F8AB18604BD2AC93935E02FF3B54FA`, Nick Craig-Wood, as listed on https://rclone.org/release_signing/
  and served by keys.openpgp.org.
- It is DSA-1024 and rclone signs the list with a SHA-1 digest (seen for 1.76.0). That is weak today, which is why
  the signature is one of three checks, not the only one.

If rclone ever changes its key, `RcloneReleaseSignature.KeyFingerprint` and `KeyPacket` change with an Inbound
release, after the new key is checked against rclone's own page.

## To change the tested version

1. Take the new `SHA256SUMS`, check its signature (`gpg --verify`) and that GitHub's copy is identical.
2. Update `RclonePinned` (version and hashes), the `Dockerfile` arguments, this file and `COPYING` if it changed.
3. Run the Sites tests with `INBOUND_RCLONE` (and `INBOUND_RCLONE_ZIP`) pointing at the new version.
