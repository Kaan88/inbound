[CmdletBinding()]
param(
    [string]$ZipPath
)

# Makes a folder that holds the store package and loads unpacked under the store's ID, so it can be
# tested with the desktop app before the listing is public. Unpacking the package as it is does not
# do that: it has no "key", so Chrome gives it an ID from its folder, and the app refuses that ID.
#
# The only change to the package is the added "key", the public half of the store's own key. For
# local testing only. Never upload this folder: the store refuses a package with a different key.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$extension = Join-Path $root "extensions\chrome"
$outDir = Join-Path $root "artifacts\chrome-store"
$target = Join-Path $outDir "store-id-test"

if (-not $ZipPath) {
    $package = [IO.File]::ReadAllText((Join-Path $extension "package.json")) | ConvertFrom-Json
    $ZipPath = Join-Path $outDir "inbound-bridge-$($package.version)-chrome.zip"
}
if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "No package at $ZipPath. Run scripts\package-extension-store.ps1 first, or pass -ZipPath."
}

$key = [IO.File]::ReadAllText((Join-Path $extension "store-public-key.txt")).Trim()
$bridge = [IO.File]::ReadAllText((Join-Path $root "src\DownloadManager.Contracts\BrowserBridge.cs"))
$storeId = [regex]::Match($bridge, 'StoreExtensionId\s*=\s*"([a-p]{32})"').Groups[1].Value
if (-not $storeId) { throw "Could not read the store ID from BrowserBridge.cs." }

# Chrome's rule: the ID is the first 16 bytes of the key's SHA-256, each half-byte written as a-p.
$hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Convert]::FromBase64String($key))
$id = -join ($hash[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
if ($id -ne $storeId) { throw "store-public-key.txt gives the ID $id, not the store's $storeId." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
[IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $target)

$manifestPath = Join-Path $target "manifest.json"
$manifestText = [IO.File]::ReadAllText($manifestPath)
if ($manifestText -match '"key"\s*:') { throw "This package already has a key, so it is not a store package." }
# Added as text, so the rest of the file stays exactly as it was packaged.
$withKey = ([regex]'\{').Replace($manifestText, "{`n  `"key`": `"$key`",", 1)
$check = $withKey | ConvertFrom-Json
if ($check.key -ne $key) { throw "The key could not be added to the manifest." }
[IO.File]::WriteAllText($manifestPath, $withKey, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Store ID test folder: $target"
Write-Host "  ID $id. In chrome://extensions, remove any other copy, then Load unpacked on this folder."
Write-Host "  For local testing only. Do not upload it."
