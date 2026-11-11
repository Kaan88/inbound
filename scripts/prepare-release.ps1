[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$InstallerPath,
    [string]$UpdatePackagePath,
    [string]$SignerExe,
    [string]$BuildsRepository = "Kaan88/inbound",
    # EXT.S4: the signed Firefox add-on, published with the release (Firefox's update file points here).
    [string]$FirefoxXpiPath,
    [switch]$NoFirefoxXpi
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+(?:\.\d+){2,3}$') {
    throw "Version must contain 3 or 4 numeric components, for example 1.2.3."
}
if ($BuildsRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "BuildsRepository must use owner/repository form."
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $root "artifacts\installer\DownloadManagerSetup-$Version.exe"
}
if (-not $UpdatePackagePath) {
    $UpdatePackagePath = Join-Path $root "artifacts\package\download-manager-$Version.zip"
}
if (-not $SignerExe) {
    $SignerExe = Join-Path $root "tools\UpdateSigner\bin\Release\net10.0\UpdateSigner.exe"
}

$firefoxXpiName = $null
if (-not $NoFirefoxXpi) {
    $extensionVersion = ([IO.File]::ReadAllText((Join-Path $root "extensions\chrome\package.json")) | ConvertFrom-Json).version
    $firefoxXpiName = "inbound-bridge-$extensionVersion-firefox.xpi"
    if (-not $FirefoxXpiPath) { $FirefoxXpiPath = Join-Path $root "artifacts\firefox-addon\$firefoxXpiName" }
    if (-not (Test-Path -LiteralPath $FirefoxXpiPath -PathType Leaf)) {
        throw "The signed Firefox add-on is missing: $FirefoxXpiPath. Run scripts\sign-extension-firefox.ps1 -ReleaseTag v$Version, or pass -NoFirefoxXpi."
    }
    # Firefox's update file must point at this release and this exact file, or updates would fail or go elsewhere.
    $firefoxHash = (Get-FileHash -LiteralPath $FirefoxXpiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $firefoxUpdates = [IO.File]::ReadAllText((Join-Path $root "extensions\firefox-updates.json")) | ConvertFrom-Json
    $entry = @($firefoxUpdates.addons.'inbound-bridge@kaan88'.updates | Where-Object { $_.version -eq $extensionVersion })
    $expectedLink = "https://github.com/$BuildsRepository/releases/download/v$Version/$firefoxXpiName"
    if ($entry.Count -ne 1 -or $entry[0].update_link -cne $expectedLink -or $entry[0].update_hash -cne "sha256:$firefoxHash") {
        # An app release with an unchanged extension re-points the file at this release with the XPI already
        # signed: AMO signs a version number only once.
        throw "extensions\firefox-updates.json does not list $firefoxXpiName at $expectedLink with its hash. Run scripts\sign-extension-firefox.ps1 -ReleaseTag v$Version -SignedXpi `"$FirefoxXpiPath`" (or bump the extension version and sign it)."
    }
}

foreach ($requiredPath in @($InstallerPath, $UpdatePackagePath, $PrivateKeyPath, $SignerExe)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required file not found: $requiredPath"
    }
}
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$package = (Resolve-Path -LiteralPath $UpdatePackagePath).Path
$privateKey = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
$signer = (Resolve-Path -LiteralPath $SignerExe).Path
if ([IO.Path]::GetFileName($installer) -cne "DownloadManagerSetup-$Version.exe") {
    throw "Installer filename must be DownloadManagerSetup-$Version.exe."
}
if ([IO.Path]::GetFileName($package) -cne "download-manager-$Version.zip") {
    throw "Update package filename must be download-manager-$Version.zip."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($requiredEntry in @(
        "app/DownloadManager.Host.exe",
        "app/DownloadManager.Desktop.WinUI.exe",
        # The bundled runtimes: without them the update only runs on a PC that happens to have them.
        "app/hostfxr.dll",
        "app/coreclr.dll",
        "app/Microsoft.ui.xaml.dll",
        "app/DownloadManager.Desktop.WinUI.pri",
        "install.ps1",
        "apply-update.ps1"
    )) {
        if ($entries -cnotcontains $requiredEntry) {
            throw "Update ZIP is missing required entry '$requiredEntry'."
        }
    }

    $installEntry = $archive.GetEntry("install.ps1")
    $reader = [IO.StreamReader]::new($installEntry.Open())
    try { $installScript = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $versionPattern = '\[string\]\s*\$Version\s*=\s*"' + [regex]::Escape($Version) + '"'
    if ($installScript -notmatch $versionPattern) {
        throw "The packaged install.ps1 does not declare version $Version."
    }
}
finally {
    $archive.Dispose()
}

$releaseRoot = Join-Path $root "artifacts\release"
if (-not (Test-Path -LiteralPath $releaseRoot)) {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
}
$releaseRoot = (Resolve-Path -LiteralPath $releaseRoot).Path
$expectedReleaseRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts\release"))
if (-not $releaseRoot.Equals($expectedReleaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output directory resolves outside the repository's artifacts\release folder."
}
$outputDir = Join-Path $releaseRoot "v$Version"
if (Test-Path -LiteralPath $outputDir) {
    throw "Release output path already exists; review or move it before preparing this version: $outputDir"
}

$stagingDir = Join-Path $releaseRoot (".v$Version.staging-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stagingDir | Out-Null
$zipName = "download-manager-$Version.zip"
$manifestUrl = "https://github.com/$BuildsRepository/releases/download/v$Version/$zipName"
Copy-Item -LiteralPath $installer -Destination (Join-Path $stagingDir "DownloadManagerSetup-$Version.exe")
Copy-Item -LiteralPath $package -Destination (Join-Path $stagingDir $zipName)

$signerArgs = @($stagingDir, $Version, $manifestUrl, (Join-Path $stagingDir $zipName), $privateKey, "stable")
& $signer @signerArgs
if ($LASTEXITCODE -ne 0) {
    throw "UpdateSigner failed with exit code $LASTEXITCODE. Staging files remain at $stagingDir for review."
}

$checksumFiles = @(
    "DownloadManagerSetup-$Version.exe",
    $zipName,
    "manifest.json",
    "public.pem"
)
if ($firefoxXpiName) {
    Copy-Item -LiteralPath $FirefoxXpiPath -Destination (Join-Path $stagingDir $firefoxXpiName)
    $checksumFiles += $firefoxXpiName
}
foreach ($file in $checksumFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDir $file) -PathType Leaf)) {
        throw "UpdateSigner or staging did not produce required file '$file'. Staging files remain at $stagingDir."
    }
}
$checksumLines = foreach ($file in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $stagingDir $file) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$file"
}
# LF line ends, so `sha256sum -c` on Linux reads it as well as Windows tools do.
[IO.File]::WriteAllText((Join-Path $stagingDir "SHA256SUMS"), (($checksumLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

[IO.Directory]::Move($stagingDir, $outputDir)
Write-Host "Prepared local release assets at $outputDir"
$uploads = @($checksumFiles) + "SHA256SUMS"
Write-Host "Upload these $($uploads.Count) files explicitly: $($uploads -join ', ')"
