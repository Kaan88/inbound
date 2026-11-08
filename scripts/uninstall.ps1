[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$installRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')) {
    throw "InstallRoot must be an application directory, not a drive root."
}
$appDir = Join-Path $installRoot "app"

Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# Chrome and Edge each have a key pointing at one manifest; Firefox has its own key and manifest.
# Keep in step with BrowserBridge.NativeHostKeyPaths and BrowserBridge.FirefoxNativeHostKeyPath.
# A key is removed only when it points at this install's manifest or at a file that no longer exists,
# so another program's working registration under the same name is left alone.
function Test-NativeKeyIsOurs([string]$registered, [string]$ownManifest) {
    if (-not $registered) { return $true }
    try {
        if (-not (Test-Path -LiteralPath $registered)) { return $true }
        return [IO.Path]::GetFullPath($registered) -ieq [IO.Path]::GetFullPath($ownManifest)
    }
    catch { return $false }
}

$hostName = "com.kaank.downloadmanager"
$chromiumManifest = Join-Path $installRoot "native-host\$hostName.json"
$firefoxManifest = Join-Path $installRoot "native-host\$hostName.firefox.json"
$nativeKeys = [ordered]@{
    "Chrome"  = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
    "Edge"    = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    "Firefox" = "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName"
}
foreach ($browser in $nativeKeys.Keys) {
    $nativeKey = $nativeKeys[$browser]
    $ownManifest = if ($browser -eq "Firefox") { $firefoxManifest } else { $chromiumManifest }
    if (-not (Test-Path $nativeKey)) { continue }
    $registered = (Get-Item $nativeKey).GetValue("")
    if (-not (Test-NativeKeyIsOurs $registered $ownManifest)) {
        Write-Warning "Left the $browser native-host registration alone: it points at $registered, not at this install."
        continue
    }
    Remove-Item $nativeKey -Recurse -Force
    Write-Host "Removed $browser native-host registration."
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name "DownloadManagerHost" -ErrorAction SilentlyContinue
}

# Current name plus the shortcut created by versions named plain "Download Manager".
foreach ($name in @("Inbound Download Manager.lnk")) {
    $shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name"
    if (Test-Path $shortcut) {
        Remove-Item $shortcut -Force
    }
}

if (Test-Path $appDir) {
    Remove-Item $appDir -Recurse -Force
    Write-Host "Removed application files."
}
$extension = Join-Path $installRoot "extension"
if (Test-Path $extension) {
    Remove-Item $extension -Recurse -Force
    Write-Host "Removed the unpacked extension files. Remove the extension from chrome://extensions as well."
}

foreach ($leftover in @('native-host', 'install.json', 'INSTALL.txt')) {
    Remove-Item (Join-Path $installRoot $leftover) -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Get-ChildItem $installRoot -Force -ErrorAction SilentlyContinue)) {
    Remove-Item $installRoot -Force -ErrorAction SilentlyContinue
}

$dataRoot = Join-Path $env:LOCALAPPDATA "Inbound Download Manager"
if ($RemoveData) {
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed application data and the download list. Downloaded files and their .part files were not touched."
}
else {
    Write-Host "Kept download list and settings under $dataRoot (use -RemoveData to delete them)."
}
