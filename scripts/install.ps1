[CmdletBinding()]
param(
    [string]$Source,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager"),
    [string]$Version = "0.6.1",
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [switch]$LaunchAtSignIn
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')) {
    throw "InstallRoot must be an application directory, not a drive root."
}
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

$appPrefix = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'
$installedProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    $_.Name -in @('DownloadManager.Host.exe', 'DownloadManager.Desktop.WinUI.exe')
})
if ($installedProcesses | Where-Object Name -eq 'DownloadManager.Host.exe') {
    throw "Exit the installed Inbound Download Manager Host before replacing its files. Downloads were not interrupted."
}
foreach ($frontendProcess in $installedProcesses) {
    Stop-Process -Id $frontendProcess.ProcessId -Force -ErrorAction Stop
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
$installMetadata = [ordered]@{
    version = $Version
    channel = "stable"
    installRoot = $installRoot
    installedAt = (Get-Date).ToString("O")
}
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
# Publish the version only after replacing and registering all application files.
[System.IO.File]::WriteAllText(
    (Join-Path $installRoot "install.json"),
    ($installMetadata | ConvertTo-Json -Depth 3),
    (New-Object System.Text.UTF8Encoding($false)))

if (-not $NoShortcut) {
    try {
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu "Inbound Download Manager.lnk"))
        $shortcut.TargetPath = Join-Path $appDir "DownloadManager.Host.exe"
        $shortcut.Arguments = "--background"
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Description = "Inbound Download Manager background host"
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
Inbound Download Manager installed at:
  $appDir

Browser extension:
  Chrome or Edge: install Inbound Download Manager Bridge from the Chrome Web Store:
    https://chromewebstore.google.com/detail/olhbcdbjdnfoiinllnlpandggadfabfm
  Or, in Chrome, load the unpacked copy:
  1. Open chrome://extensions
  2. Enable Developer mode
  3. Choose "Load unpacked"
  4. Select: $extensionTarget
  5. Confirm the extension ID is $extensionId

  Firefox: install Inbound Download Manager from Firefox Add-ons:
    https://addons.mozilla.org/firefox/addon/inbound-download-manager/

Native host registration (Chrome and Edge share one manifest; Firefox has its own):
  HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager
  HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.kaank.downloadmanager
  HKCU\Software\Mozilla\NativeMessagingHosts\com.kaank.downloadmanager

The host lives in the notification area. Closing the main window hides it;
downloads and browser handoff continue. Use the tray Exit action to stop it.
"@
Write-Host $instructions
Set-Content -Path (Join-Path $installRoot "INSTALL.txt") -Value $instructions -Encoding UTF8
