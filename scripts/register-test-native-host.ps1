[CmdletBinding()]
param(
    # The test build's native host, for example src\DownloadManager.NativeHost\bin\Release\net10.0\DownloadManager.NativeHost.exe.
    [string]$NativeExe,
    # Removes this script's registration again. Leaves a key that this script did not write.
    [switch]$Remove
)

# Registers a test build's native host for Chrome for Testing (and Chromium) only, under
# HKCU\Software\Chromium\NativeMessagingHosts. Chrome for Testing reads that key before Chrome's, so a
# browser test reaches the test build while Chrome's, Edge's and Firefox's keys (the installed app's)
# stay as they are. The key is shared by everything on this PC that runs as this user, so the script
# refuses when one is already there: another test track may be using it (ROADMAP TEST.3).
# Run it, run the browser test, then run it again with -Remove.

$ErrorActionPreference = "Stop"
$hostName = "com.kaank.downloadmanager"
$key = "HKCU:\Software\Chromium\NativeMessagingHosts\$hostName"
$marker = "Inbound Download Manager browser bridge (test registration)"
$extensionOrigins = @(
    "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/",
    "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/"
)

function Get-RegisteredManifest {
    if (-not (Test-Path $key)) { return $null }
    return (Get-Item $key).GetValue("")
}

function Test-OurManifest([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $false }
    try { return ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).description -eq $marker) }
    catch { return $false }
}

if ($Remove) {
    $existing = Get-RegisteredManifest
    if ($null -eq $existing -and -not (Test-Path $key)) {
        Write-Host "No test registration to remove."
        return
    }
    # A key that names this script's manifest path, whose file was deleted since, is still this script's.
    $oursButGone = $existing -and $existing.EndsWith("\native-host-test\$hostName.json") -and -not (Test-Path -LiteralPath $existing)
    if (-not (Test-OurManifest $existing) -and -not $oursButGone) {
        throw "The Chromium key names '$existing', which this script did not write. Nothing was removed."
    }
    Remove-Item -Path $key -Force
    if (-not $oursButGone) { Remove-Item -LiteralPath $existing -Force }
    $folder = Split-Path -Parent $existing
    if ((Test-Path -LiteralPath $folder) -and -not (Get-ChildItem -LiteralPath $folder -Force)) { Remove-Item -LiteralPath $folder -Force }
    Write-Host "Removed the test registration ($existing)."
    return
}

if (-not $NativeExe) { throw "Give -NativeExe, the test build's DownloadManager.NativeHost.exe." }
if (-not (Test-Path -LiteralPath $NativeExe)) { throw "Native host executable not found: $NativeExe" }
if (Test-Path $key) {
    throw "A native host is already registered under the Chromium key ('$(Get-RegisteredManifest)'). Another test may be using it; nothing was changed. Run with -Remove when it is this script's and no test is running."
}

$exe = (Resolve-Path -LiteralPath $NativeExe).Path
$manifestDir = Join-Path (Split-Path -Parent $exe) "native-host-test"
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$manifestPath = Join-Path $manifestDir "$hostName.json"
$manifest = [ordered]@{
    name = $hostName
    description = $marker
    path = $exe
    type = "stdio"
    allowed_origins = $extensionOrigins
}
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
New-Item -Path $key -Force | Out-Null
Set-Item -Path $key -Value $manifestPath
Write-Host "Registered $hostName for Chrome for Testing and Chromium only"
Write-Host "  manifest: $manifestPath"
Write-Host "  native:   $exe"
Write-Host "Remove it after the test: scripts\register-test-native-host.ps1 -Remove"
