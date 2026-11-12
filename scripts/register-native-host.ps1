[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager\app"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$hostName = "com.kaank.downloadmanager"
# The Chrome Web Store listing; the Edge Add-ons listing (its own ID; Edge users can also install the Chrome Web Store
# listing); and the unpacked development build's fixed ID (extensions\chrome\dist, loaded by hand or in Chrome for
# Testing). Setup and the update package no longer carry an unpacked copy.
$extensionOrigins = @(
    "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/",
    "chrome-extension://ifoagddcenmjpbklkhghbdmgplldcdci/",
    "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/"
)
# Chrome and Edge each read their own key; both point at the same manifest file. Keep in step with
# BrowserBridge.NativeHostKeyPaths and installer\DownloadManager.iss.
$browserKeys = [ordered]@{
    "Chrome"  = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
    "Edge"    = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    # Firefox reads its own key and a manifest of its own, which names add-on IDs (allowed_extensions)
    # where Chrome's names origins. Keep in step with BrowserBridge.FirefoxNativeHostKeyPath.
    "Firefox" = "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName"
}
$firefoxExtensionId = "inbound-bridge@kaan88"
$nativeExe = Join-Path $AppDir "DownloadManager.NativeHost.exe"
if (-not (Test-Path $nativeExe)) {
    throw "Native host executable not found: $nativeExe. Run publish.ps1 first."
}

$manifestDir = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($AppDir))) "native-host"
$manifestPath = Join-Path $manifestDir "$hostName.json"
$firefoxManifestPath = Join-Path $manifestDir "$hostName.firefox.json"
$browserManifests = @{ "Chrome" = $manifestPath; "Edge" = $manifestPath; "Firefox" = $firefoxManifestPath }

# Check every key before writing any, so a conflict in one browser's key cannot leave the other
# browser registered and the first one not. A key whose manifest file is gone is not a conflict: it
# cannot work for anyone, and the 0.4.0 uninstaller leaves one behind after an in-app update.
if (-not $Force) {
    $conflicts = @()
    foreach ($browser in $browserKeys.Keys) {
        $key = $browserKeys[$browser]
        if (Test-Path $key) {
            $existing = (Get-Item $key).GetValue("")
            if ($existing -and $existing -ne $browserManifests[$browser] -and (Test-Path -LiteralPath $existing)) {
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
$firefoxManifest = [ordered]@{
    name = $hostName
    description = "Inbound Download Manager browser bridge"
    path = (Resolve-Path $nativeExe).Path
    type = "stdio"
    allowed_extensions = @($firefoxExtensionId)
}
[System.IO.File]::WriteAllText($firefoxManifestPath, ($firefoxManifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

# Chrome's key must be written. Edge's and Firefox's are optional: a machine that cannot write them
# must still be able to install, repair and update, and the Host tries Edge again at every start.
$registered = @()
foreach ($browser in $browserKeys.Keys) {
    $key = $browserKeys[$browser]
    try {
        New-Item -Path $key -Force | Out-Null
        Set-Item -Path $key -Value $browserManifests[$browser]
        $registered += $browser
    }
    catch {
        if ($browser -eq "Chrome") { throw }
        Write-Warning "Could not register $hostName for $browser, so $browser may not reach the app: $($_.Exception.Message)"
    }
}

Write-Host "Registered $hostName for $($registered -join ' and ')"
Write-Host "  manifest: $manifestPath"
Write-Host "  native:   $nativeExe"
Write-Host "  origins:  $($extensionOrigins -join ', ')"
Write-Host "  firefox:  $firefoxManifestPath ($firefoxExtensionId)"
