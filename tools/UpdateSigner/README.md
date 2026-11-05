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
