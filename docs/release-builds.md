# Public builds repository

Source, installers and update-feed assets live in the public `Kaan88/inbound`
repository; releases are GitHub release assets there. Preparing assets
is a local, reviewable step; `scripts/prepare-release.ps1` does not build the
application, create a GitHub release, or upload files.

First have the Firefox add-on signed (EXT.S4). This writes the signed XPI and
`extensions/firefox-updates.json`, which points Firefox at this release:

```powershell
.\scripts\sign-extension-firefox.ps1 -ReleaseTag vX.Y.Z -ApiKeysPath <AMO API key file>
```

The key file holds `{ "issuer": "...", "secret": "..." }` from addons.mozilla.org,
Developer Hub, Manage API Keys, and stays outside the repository. AMO signs a
version number only once: if signing stops after the upload, run the same command
again (it resumes from `artifacts\firefox-addon\.sign-<version>`), or download the
signed file from the Developer Hub and pass `-SignedXpi <file>`. For an app release
that keeps the same extension version, re-point the update file at the new release
with the XPI already signed:
`-ReleaseTag vX.Y.Z -SignedXpi artifacts\firefox-addon\inbound-bridge-<version>-firefox.xpi`.
Then build Setup (`scripts/build-installer.ps1`, which bundles that XPI).

For version `X.Y.Z`, prepare from the already-built artifacts:

```powershell
.\scripts\prepare-release.ps1 `
  -Version X.Y.Z `
  -PrivateKeyPath "$env:USERPROFILE\.keys\download-manager-update.pem"
```

Replace `X.Y.Z` with the release version and use the existing private key path.

The script expects `artifacts/installer/DownloadManagerSetup-X.Y.Z.exe`,
`artifacts/package/download-manager-X.Y.Z.zip`, and the already-built
`tools/UpdateSigner/bin/Release/net10.0/UpdateSigner.exe`. It validates package
contents/version, signs the update ZIP with the existing private key, and writes
six reviewable files under `artifacts/release/vX.Y.Z/`:

- `DownloadManagerSetup-X.Y.Z.exe`
- `download-manager-X.Y.Z.zip`
- `manifest.json`
- `public.pem`
- `inbound-bridge-<extension version>-firefox.xpi`, after checking that
  `extensions/firefox-updates.json` names it at this release with its hash
- `SHA256SUMS`

Review the manifest URL, version, archive contents, public key, and hashes. Then
create a release tagged `vX.Y.Z` in the builds repository and upload those six
files explicitly. Firefox follows `extensions/firefox-updates.json` on `main` as soon
as it is there, so push in this order: the tag alone (`git push origin vX.Y.Z`, on the
release squash commit), then create the release on that tag and upload the six
files, then push `main`. Pushed the other way, Firefox only meets a short window of
failed update checks (the hash rules out a wrong file) and tries again later. The manifest points at the versioned ZIP release asset. Do not
upload the private signing key or any staging directory. Keep using the same
private key for all releases because installed clients trust its public key.

The output folder must not already exist. Failed preparation leaves its uniquely
named `.vX.Y.Z.staging-*` folder in the scoped `artifacts/release` directory
for review; remove only that exact staging folder after inspecting it before
retrying.
