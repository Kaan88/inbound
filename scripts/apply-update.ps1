[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [int]$HostPid = 0,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\')) {
    throw "InstallRoot must be an application directory, not a drive root."
}
$appDir = Join-Path $InstallRoot "app"
$backupDir = Join-Path $InstallRoot "app-rollback"
$extensionDir = Join-Path $InstallRoot "extension"
$extensionBackupDir = Join-Path $InstallRoot "extension-rollback"
$hadExtension = $false
$metadataPath = Join-Path $InstallRoot "install.json"
$metadataBackup = $null
$startedNewHost = $false
$replacementStarted = $false
# Never hold the directory that install.ps1 needs to replace as our current directory.
Set-Location -LiteralPath $PackageDir
[Environment]::CurrentDirectory = (Get-Location).ProviderPath
$newProcesses = [Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-InstalledApplication {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $appDir "DownloadManager.Host.exe"
    $start.Arguments = "--background"
    $start.WorkingDirectory = $appDir
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $newProcesses.Add([System.Diagnostics.Process]::Start($start))
    if ($frontends.Count -gt 0) {
        $start.FileName = Join-Path $appDir "DownloadManager.Desktop.WinUI.exe"
        $start.Arguments = ""
        $newProcesses.Add([System.Diagnostics.Process]::Start($start))
    }
}

foreach ($targetPath in @($appDir, $backupDir, $extensionDir, $extensionBackupDir)) {
    if (-not [IO.Path]::GetFullPath($targetPath).StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Update target is outside the installation directory."
    }
    if ((Test-Path -LiteralPath $targetPath) -and
        ((Get-Item -LiteralPath $targetPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Update targets must not be directory links."
    }
}

function Write-Result([string]$status, [string]$errorText) {
    $payload = [ordered]@{
        status = $status
        version = $Version
        error = $errorText
        at = (Get-Date).ToString("O")
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ($payload | ConvertTo-Json -Depth 3),
        (New-Object System.Text.UTF8Encoding($false)))
}

try {
    # Older frontends restart the Host on disconnect. Close only this install's
    # UI processes before waiting for the old Host, so they cannot race upgrade.
    $appPrefix = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'
    $frontends = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -eq 'DownloadManager.Desktop.WinUI.exe' -and $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($frontend in $frontends) {
        Stop-Process -Id $frontend.ProcessId -Force -ErrorAction Stop
    }
    if ($HostPid -gt 0) {
        $running = Get-Process -Id $HostPid -ErrorAction SilentlyContinue
        if ($running) {
            Wait-Process -Id $HostPid -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $HostPid -ErrorAction SilentlyContinue) {
                throw "The Host did not exit for the update. Application files were not replaced."
            }
        }
    }
    # The native bridge can briefly outlive the Host and hold its install files.
    $bridges = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -eq 'DownloadManager.NativeHost.exe' -and $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($bridge in $bridges) {
        Stop-Process -Id $bridge.ProcessId -Force -ErrorAction Stop
    }

    if (Test-Path $backupDir) {
        Remove-Item $backupDir -Recurse -Force
    }

    if (Test-Path $appDir) {
        Copy-Item $appDir $backupDir -Recurse -Force
    }
    if (Test-Path $extensionBackupDir) {
        Remove-Item $extensionBackupDir -Recurse -Force
    }
    $hadExtension = Test-Path $extensionDir
    if ($hadExtension) {
        Copy-Item $extensionDir $extensionBackupDir -Recurse -Force
    }

    if (Test-Path $metadataPath) {
        $metadataBackup = Get-Content $metadataPath -Raw
    }

    $installer = Join-Path $PackageDir "install.ps1"
    if (-not (Test-Path $installer)) {
        throw "The update package has no install.ps1."
    }

    $replacementStarted = $true
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
        -Source (Join-Path $PackageDir "app") `
        -InstallRoot $InstallRoot `
        -Version $Version `
        -NoShortcut -NoLaunch
    if ($LASTEXITCODE -ne 0) {
        throw "The installer exited with $LASTEXITCODE."
    }

    if (-not (Test-Path $metadataPath)) {
        throw "The installer did not write install.json."
    }

    $metadata = Get-Content $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.version -ne $Version) {
        throw "Installed version '$($metadata.version)' does not match expected '$Version'."
    }

    if (-not (Test-Path (Join-Path $appDir "DownloadManager.Host.exe"))) {
        throw "The installed app is missing DownloadManager.Host.exe."
    }

    # Keep the backup through restart. The next update replaces it only after
    # its Host has exited, so a launch failure cannot discard both versions.
    if (-not $NoStart) {
        Start-InstalledApplication
        $startedNewHost = $true
    }

    Write-Result "succeeded" ""

    exit 0
}
catch {
    $message = $_.Exception.Message
    # If reopening partially succeeded, release only processes created by us
    # before attempting to put the previous files back.
    foreach ($newProcess in $newProcesses) {
        try { if (-not $newProcess.HasExited) { $newProcess.Kill(); $newProcess.WaitForExit(10000) | Out-Null } }
        catch { $message = "$message; restart cleanup error: $($_.Exception.Message)" }
    }
    # Repair the version record even when a locked binary prevents file rollback.
    if ($replacementStarted -and $null -ne $metadataBackup) {
        try {
            [System.IO.File]::WriteAllText($metadataPath, $metadataBackup,
                (New-Object System.Text.UTF8Encoding($false)))
        }
        catch { $message = "$message; metadata rollback error: $($_.Exception.Message)" }
    }
    try {
        if ($replacementStarted -and (Test-Path $appDir)) {
            Remove-Item $appDir -Recurse -Force
        }

        if ($replacementStarted -and (Test-Path $backupDir)) {
            Move-Item $backupDir $appDir -Force
        }

        if ($replacementStarted -and (Test-Path $extensionDir)) {
            Remove-Item $extensionDir -Recurse -Force
        }
        if ($replacementStarted -and $hadExtension -and (Test-Path $extensionBackupDir)) {
            Move-Item $extensionBackupDir $extensionDir -Force
        }

        if ($replacementStarted -and -not $NoStart -and (Test-Path (Join-Path $appDir "DownloadManager.Host.exe"))) {
            Start-InstalledApplication
        }
    }
    catch {
        $message = "$message; rollback error: $($_.Exception.Message)"
    }

    Write-Result "failed" $message
    exit 1
}
