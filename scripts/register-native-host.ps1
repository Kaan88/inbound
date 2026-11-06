[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager\app"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$hostName = "com.kaank.downloadmanager"
# The Chrome Web Store listing, and the unpacked build that Setup and the update package carry.
# Edge users install the same store listing, so the same IDs apply in both browsers.
$extensionOrigins = @(
    "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/",
    "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/"
)
# Chrome and Edge each read their own key; both point at the same manifest file. Keep in step with
# BrowserBridge.NativeHostKeyPaths and installer\DownloadManager.iss.
$browserKeys = [ordered]@{
    "Chrome" = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
    "Edge"   = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
}
$nativeExe = Join-Path $AppDir "DownloadManager.NativeHost.exe"
if (-not (Test-Path $nativeExe)) {
    throw "Native host executable not found: $nativeExe. Run publish.ps1 first."
}

$manifestDir = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($AppDir))) "native-host"
$manifestPath = Join-Path $manifestDir "$hostName.json"

# Check every key before writing any, so a conflict in one browser's key cannot leave the other
# browser registered and the first one not. A key whose manifest file is gone is not a conflict: it
# cannot work for anyone, and the 0.4.0 uninstaller leaves one behind after an in-app update.
if (-not $Force) {
    $conflicts = @()
    foreach ($browser in $browserKeys.Keys) {
        $key = $browserKeys[$browser]
        if (Test-Path $key) {
            $existing = (Get-Item $key).GetValue("")
            if ($existing -and $existing -ne $manifestPath -and (Test-Path -LiteralPath $existing)) {
                $conflicts += "$browser ($existing)"
            }
        }
    }
    if ($conflicts.Count -gt 0) {
        throw "A different native host is already registered for: $($conflicts -join '; '). Nothing was changed. Re-run with -Force to replace it."
    }
}

New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$manifest = [ordered]@{
    name = $hostName
    description = "Inbound Download Manager browser bridge"
    path = (Resolve-Path $nativeExe).Path
    type = "stdio"
    allowed_origins = $extensionOrigins
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

# Chrome's key must be written. Edge's is optional: a machine that cannot write it must still be able
# to install, repair and update, and the Host tries again at every start.
$registered = @()
foreach ($browser in $browserKeys.Keys) {
    $key = $browserKeys[$browser]
    try {
        New-Item -Path $key -Force | Out-Null
        Set-Item -Path $key -Value $manifestPath
        $registered += $browser
    }
    catch {
        if ($browser -ne "Edge") { throw }
        Write-Warning "Could not register $hostName for Edge, so Edge may not reach the app: $($_.Exception.Message)"
    }
}

Write-Host "Registered $hostName for $($registered -join ' and ')"
Write-Host "  manifest: $manifestPath"
Write-Host "  native:   $nativeExe"
Write-Host "  origins:  $($extensionOrigins -join ', ')"
