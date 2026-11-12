[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager"),
    [string]$AppDir,
    [string]$Source,
    [switch]$NoShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $AppDir) {
    $AppDir = Join-Path $InstallRoot "app"
}

# Only this install's processes, closed as Setup closes them (stop-for-setup.ps1, beside this script): the Host is asked
# to finish its writes and exit and is waited for, never force-killed, and a test instance or another build elsewhere
# is left alone (audit 4 M7). Before the files are restored, which a running copy would hold. Run in its own process,
# as it ends with exit.
$stopScript = Join-Path $PSScriptRoot "stop-for-setup.ps1"
if (Test-Path $stopScript) {
    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    # Windows PowerShell 5.1 turns native stderr into a terminating error under 'Stop' when output is captured; the
    # exit code decides, so the message below is the one shown.
    $ErrorActionPreference = "Continue"
    $closed = $false
    try {
        & $powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -AppDir $AppDir
        $closed = $LASTEXITCODE -eq 0
    }
    catch { $closed = $false }
    finally { $ErrorActionPreference = "Stop" }
    if (-not $closed) {
        throw "Inbound Download Manager is still running. Exit it from its tray icon, then run repair again."
    }
}
else {
    Write-Warning "stop-for-setup.ps1 was not found beside this script; if the app is running, exit it from its tray icon first."
}

$required = @(
    "DownloadManager.Host.exe",
    "DownloadManager.NativeHost.exe",
    "DownloadManager.Server.exe",
    "DownloadManager.Desktop.WinUI.exe"
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $AppDir $_)) }
if ($missing.Count -gt 0) {
    if ($Source -and (Test-Path (Join-Path $Source "DownloadManager.Host.exe"))) {
        New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
        Copy-Item (Join-Path $Source "*") $AppDir -Recurse -Force
        Write-Host "Restored application files from $Source."
    }
    else {
        throw "Repair cannot continue; missing: $($missing -join ', '). Reinstall or pass -Source."
    }
}

& (Join-Path $PSScriptRoot "register-native-host.ps1") -AppDir $AppDir -Force

if (-not $NoShortcut) {
    try {
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu "Inbound Download Manager.lnk"))
        # As Setup writes it ([Icons]): --open, so a click on it shows the window.
        $shortcut.TargetPath = Join-Path $AppDir "DownloadManager.Host.exe"
        $shortcut.Arguments = "--open"
        $shortcut.WorkingDirectory = (Split-Path -Parent ([IO.Path]::GetFullPath($AppDir)))
        $shortcut.IconLocation = Join-Path $AppDir "DownloadManager.Host.exe"
        $shortcut.Description = "Inbound Download Manager"
        $shortcut.Save()
        Write-Host "Restored the Start Menu shortcut."
    }
    catch {
        Write-Warning "Could not restore the Start Menu shortcut: $($_.Exception.Message)"
    }
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$installFile = Join-Path $InstallRoot "INSTALL.txt"
if (-not (Test-Path $installFile)) {
    Set-Content -Path $installFile -Encoding UTF8 -Value @"
Inbound Download Manager repaired at $(Get-Date -Format O)

Application: $AppDir
Native host: com.kaank.downloadmanager
"@
}
else {
    Add-Content -Path $installFile -Encoding UTF8 -Value "Repaired at $(Get-Date -Format O)"
}

if (-not $NoStart) {
    Start-Process -FilePath (Join-Path $AppDir "DownloadManager.Host.exe") -ArgumentList "--background"
    Write-Host "Started the background program."
}

Write-Host "Repair complete."
