[CmdletBinding()]
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA "DownloadManager\app"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$hostName = "com.kaank.downloadmanager"
$extensionOrigin = "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/"
$nativeExe = Join-Path $AppDir "DownloadManager.NativeHost.exe"
if (-not (Test-Path $nativeExe)) {
    throw "Native host executable not found: $nativeExe. Run publish.ps1 first."
}

$manifestDir = Join-Path $env:LOCALAPPDATA "DownloadManager\native-host"
$manifestPath = Join-Path $manifestDir "$hostName.json"
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null

$manifest = [ordered]@{
    name = $hostName
    description = "Download Manager Chrome bridge"
    path = (Resolve-Path $nativeExe).Path
    type = "stdio"
    allowed_origins = @($extensionOrigin)
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

$key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
if ((Test-Path $key) -and (-not $Force)) {
    $existing = (Get-Item $key).GetValue("")
    if ($existing -and $existing -ne $manifestPath) {
        throw "A different native host is already registered: $existing. Re-run with -Force to replace it."
    }
}
New-Item -Path $key -Force | Out-Null
Set-Item -Path $key -Value $manifestPath

Write-Host "Registered $hostName"
Write-Host "  manifest: $manifestPath"
Write-Host "  native:   $nativeExe"
Write-Host "  origin:   $extensionOrigin"
