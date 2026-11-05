# Public builds repository

The source repository remains private. Public installers and update-feed assets
go to `Kaan88/download-manager-builds` as GitHub release assets. Preparing assets
is a local, reviewable step; `scripts/prepare-release.ps1` does not build the
application, create a GitHub release, or upload files.

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
five reviewable files under `artifacts/release/vX.Y.Z/`:

- `DownloadManagerSetup-X.Y.Z.exe`
- `download-manager-X.Y.Z.zip`
- `manifest.json`
- `public.pem`
- `SHA256SUMS`

Review the manifest URL, version, archive contents, public key, and hashes. Then
create a release tagged `vX.Y.Z` in the builds repository and upload those five
files explicitly. The manifest points at the versioned ZIP release asset. Do not
upload the private signing key or any staging directory. Keep using the same
private key for all releases because installed clients trust its public key.

The output folder must not already exist. Failed preparation leaves its uniquely
named `.vX.Y.Z.staging-*` folder in the scoped `artifacts/release` directory
for review; remove only that exact staging folder after inspecting it before
retrying.
