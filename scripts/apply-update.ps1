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
$appDir = Join-Path $InstallRoot "app"
$backupDir = Join-Path $InstallRoot "app-rollback"
$metadataPath = Join-Path $InstallRoot "install.json"
$metadataBackup = $null
$startedNewHost = $false

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
    if ($HostPid -gt 0) {
        $running = Get-Process -Id $HostPid -ErrorAction SilentlyContinue
        if ($running) {
            Wait-Process -Id $HostPid -Timeout 120 -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $backupDir) {
        Remove-Item $backupDir -Recurse -Force
    }

    if (Test-Path $appDir) {
        Copy-Item $appDir $backupDir -Recurse -Force
    }

    if (Test-Path $metadataPath) {
        $metadataBackup = Get-Content $metadataPath -Raw
    }

    $installer = Join-Path $PackageDir "install.ps1"
    if (-not (Test-Path $installer)) {
        throw "The update package has no install.ps1."
    }

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

    if (Test-Path $backupDir) {
        Remove-Item $backupDir -Recurse -Force
    }

    Write-Result "succeeded" ""
    if (-not $NoStart) {
        Start-Process -FilePath (Join-Path $appDir "DownloadManager.Host.exe") `
            -ArgumentList "--background" -WorkingDirectory $appDir -WindowStyle Hidden
        $startedNewHost = $true
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    try {
        if (Test-Path $appDir) {
            Remove-Item $appDir -Recurse -Force
        }

        if (Test-Path $backupDir) {
            Move-Item $backupDir $appDir -Force
        }

        if ($null -ne $metadataBackup) {
            [System.IO.File]::WriteAllText(
                $metadataPath,
                $metadataBackup,
                (New-Object System.Text.UTF8Encoding($false)))
        }

        if (-not $NoStart -and (Test-Path (Join-Path $appDir "DownloadManager.Host.exe"))) {
            Start-Process -FilePath (Join-Path $appDir "DownloadManager.Host.exe") `
                -ArgumentList "--background" -WorkingDirectory $appDir -WindowStyle Hidden
        }
    }
    catch {
        $message = "$message; rollback error: $($_.Exception.Message)"
    }

    Write-Result "failed" $message
    exit 1
}
