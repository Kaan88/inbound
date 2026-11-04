[CmdletBinding()]
param(
    [string]$Source,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "DownloadManager"),
    [string]$Version = "0.1.0",
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [switch]$LaunchAtSignIn
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installRoot = $InstallRoot
$appDir = Join-Path $installRoot "app"

if (-not $Source) {
    if (Test-Path (Join-Path $PSScriptRoot "app")) {
        $Source = Join-Path $PSScriptRoot "app"
    }
    else {
        $Source = Join-Path $repoRoot "artifacts\app"
    }
}
if (-not (Test-Path (Join-Path $Source "DownloadManager.Host.exe"))) {
    throw "Published app not found at $Source. Run scripts\publish.ps1 first."
}

$extensionSource = if (Test-Path (Join-Path $PSScriptRoot "extension")) {
    Join-Path $PSScriptRoot "extension"
}
else {
    Join-Path $repoRoot "extensions\chrome\dist"
}
if (-not (Test-Path (Join-Path $extensionSource "manifest.json"))) {
    throw "Chrome extension build not found at $extensionSource."
}

Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
$installMetadata = [ordered]@{
    version = $Version
    channel = "stable"
    installRoot = $installRoot
    installedAt = (Get-Date).ToString("O")
}
[System.IO.File]::WriteAllText(
    (Join-Path $installRoot "install.json"),
    ($installMetadata | ConvertTo-Json -Depth 3),
    (New-Object System.Text.UTF8Encoding($false)))
if (Test-Path $appDir) {
    Remove-Item $appDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
Copy-Item (Join-Path $Source "*") $appDir -Recurse -Force

$extensionTarget = Join-Path $installRoot "extension"
if (Test-Path $extensionTarget) {
    Remove-Item $extensionTarget -Recurse -Force
}
Copy-Item $extensionSource $extensionTarget -Recurse -Force

& (Join-Path $PSScriptRoot "register-native-host.ps1") -AppDir $appDir -Force

if (-not $NoShortcut) {
    try {
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu "Download Manager.lnk"))
        $shortcut.TargetPath = Join-Path $appDir "DownloadManager.Host.exe"
        $shortcut.Arguments = "--background"
        $shortcut.WorkingDirectory = $appDir
        $shortcut.Description = "Download Manager background host"
        $shortcut.Save()
        Write-Host "Created Start Menu shortcut."
    }
    catch {
        Write-Warning "Could not create the Start Menu shortcut: $($_.Exception.Message)"
    }
}

if ($LaunchAtSignIn) {
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "DownloadManagerHost" -Value "`"$(Join-Path $appDir 'DownloadManager.Host.exe')`" --background"
    Write-Host "Enabled launch at sign-in."
}

if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $appDir "DownloadManager.Host.exe") -ArgumentList "--background"
    Start-Sleep -Seconds 2
    Start-Process -FilePath (Join-Path $appDir "DownloadManager.Desktop.WinUI.exe")
}

$extensionId = "pknddmneejadnnngffojnoecojhdfalc"
$instructions = @"
Download Manager installed at:
  $appDir

Chrome extension:
  1. Open chrome://extensions
  2. Enable Developer mode
  3. Choose "Load unpacked"
  4. Select: $extensionTarget
  5. Confirm the extension ID is $extensionId

Native host registration:
  HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager

The host lives in the notification area. Closing the main window hides it;
downloads and browser handoff continue. Use the tray Exit action to stop it.
"@
Write-Host $instructions
Set-Content -Path (Join-Path $installRoot "INSTALL.txt") -Value $instructions -Encoding UTF8
