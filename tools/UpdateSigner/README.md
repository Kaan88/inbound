# Release manifest signing

Generate the release key once, in a private location excluded from source control:

```powershell
dotnet run --project tools/UpdateSigner -- --generate-key secrets/update-signing.pem
```

The parent directory must already exist. Generation refuses to overwrite an
existing key. Back up the key securely; future releases must use the same key
as the public key trusted by installed applications.

Sign the update ZIP (the package containing `apply-update.ps1`, not the setup EXE):

```powershell
dotnet run --project tools/UpdateSigner -- artifacts/release 0.1.1 https://HOST/download-manager-0.1.1.zip artifacts/package/download-manager-0.1.1.zip secrets/update-signing.pem stable
```

Output contains `manifest.json` and `public.pem` only. Publish the manifest and
ZIP; never publish the signing key. The manifest binds the version, channel,
download URL, exact byte length and SHA-256 using RSA/SHA-256. Installed clients
must trust the matching public key before using the feed.

## AppImage

The AppImage updates itself from `manifest-linux-x64.json` in the same release (an installed AppImage derives its
name from the Windows `manifest.json` address). Sign the `.AppImage` with the same key, naming that manifest:

```sh
scripts/prepare-appimage-release.sh 0.6.6 artifacts/Inbound-0.6.6-x86_64.AppImage secrets/update-signing.pem
```

Upload `Inbound-<version>-x86_64.AppImage` and `manifest-linux-x64.json` with the Windows assets.
