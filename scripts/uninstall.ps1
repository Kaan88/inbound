[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "DownloadManager"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$installRoot = $InstallRoot
$appDir = Join-Path $installRoot "app"

Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$nativeKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager"
if (Test-Path $nativeKey) {
    Remove-Item $nativeKey -Recurse -Force
    Write-Host "Removed Chrome native-host registration."
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name "DownloadManagerHost" -ErrorAction SilentlyContinue
}

$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Download Manager.lnk"
if (Test-Path $shortcut) {
    Remove-Item $shortcut -Force
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

if ($RemoveData) {
    Remove-Item $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed application data, jobs and partial files."
}
else {
    Write-Host "Kept download state under $installRoot (use -RemoveData to delete it)."
}
