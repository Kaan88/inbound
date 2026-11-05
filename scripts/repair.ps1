[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "DownloadManager"),
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

$required = @(
    "DownloadManager.Host.exe",
    "DownloadManager.NativeHost.exe",
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

Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

& (Join-Path $PSScriptRoot "register-native-host.ps1") -AppDir $AppDir -Force

$extensionTarget = Join-Path $InstallRoot "extension"
if (-not (Test-Path (Join-Path $extensionTarget "manifest.json"))) {
    $extensionSource = if (Test-Path (Join-Path $PSScriptRoot "extension")) {
        Join-Path $PSScriptRoot "extension"
    }
    else {
        Join-Path $repoRoot "extensions\chrome\dist"
    }
    if (Test-Path (Join-Path $extensionSource "manifest.json")) {
        if (Test-Path $extensionTarget) {
            Remove-Item $extensionTarget -Recurse -Force
        }
        Copy-Item $extensionSource $extensionTarget -Recurse -Force
        Write-Host "Restored extension files at $extensionTarget."
    }
    else {
        Write-Warning "Extension files were not found; reload the unpacked extension manually."
    }
}

if (-not $NoShortcut) {
    try {
        $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        Remove-Item (Join-Path $startMenu "Download Manager.lnk") -Force -ErrorAction SilentlyContinue
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu "Inbound Download Manager.lnk"))
        $shortcut.TargetPath = Join-Path $AppDir "DownloadManager.Host.exe"
        $shortcut.Arguments = "--background"
        $shortcut.WorkingDirectory = $AppDir
        $shortcut.Description = "Inbound Download Manager background host"
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
Chrome extension: $extensionTarget
Native host: com.kaank.downloadmanager
"@
}
else {
    Add-Content -Path $installFile -Encoding UTF8 -Value "Repaired at $(Get-Date -Format O)"
}

if (-not $NoStart) {
    Start-Process -FilePath (Join-Path $AppDir "DownloadManager.Host.exe") -ArgumentList "--background"
    Write-Host "Started the background host."
}

Write-Host "Repair complete."
