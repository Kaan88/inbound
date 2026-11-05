[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager"),
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
